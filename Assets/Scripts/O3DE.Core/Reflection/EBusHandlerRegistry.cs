/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace O3DE.Reflection
{
    /// <summary>
    /// Runtime registry for source-generator-emitted EBus handlers.
    ///
    /// The flow when an [EBus]-attributed class connects:
    ///   1. Source-generated <c>ConnectTo&lt;BusName&gt;()</c> calls
    ///      <see cref="Register"/> with the class instance + a managed
    ///      dispatch callback.
    ///   2. <see cref="Register"/> allocates a monotonic <c>HandlerToken</c>
    ///      and stores (token -> (instance, dispatchCallback)).
    ///   3. <see cref="Register"/> calls into the C++ side via the
    ///      <c>Reflection_RegisterEBusHandler</c> internal call,
    ///      passing the bus name + address (if addressed) + the
    ///      token. The C++ side spins up a BehaviorEBusHandler that
    ///      proxies events back to this managed registry, keyed by
    ///      token.
    ///   4. When the bus fires an event, C++ calls
    ///      <see cref="DispatchEvent"/> with the token + event name +
    ///      JSON args, and we route to the right managed delegate.
    ///   5. On disconnect, source-generated <c>DisconnectFrom...</c>
    ///      calls <see cref="Unregister"/> which tears down both sides.
    ///
    /// Thread-safety: <see cref="ConcurrentDictionary"/> guards the
    /// registry. The dispatch callback may be invoked from any thread
    /// the underlying EBus fires from - callers (the source generator's
    /// emitted shim) marshal back to the editor main thread if their
    /// user code needs that guarantee.
    /// </summary>
    public static class EBusHandlerRegistry
    {
        /// <summary>
        /// Callback invoked by the C++ side when the bus fires an event
        /// addressed at a registered handler. Receives the event name +
        /// args-as-JSON; returns a JSON result envelope or null for
        /// void-returning events.
        /// </summary>
        public delegate string? DispatchCallback(string eventName, string argsJson);

        private sealed class HandlerEntry
        {
            public required object Instance { get; init; }
            public required string BusName { get; init; }
            public required DispatchCallback Dispatch { get; init; }
        }

        private static readonly ConcurrentDictionary<long, HandlerEntry> s_handlers = new();
        private static long s_nextToken = 0;

        /// <summary>
        /// Called by source-generated ConnectTo&lt;BusName&gt;().
        /// Allocates a token, stores the dispatch callback, and asks the
        /// C++ side to register a BehaviorEBusHandler bound to this token.
        /// </summary>
        /// <param name="instance">The user object owning the handler methods.</param>
        /// <param name="busName">EBus name as reflected in BehaviorContext.</param>
        /// <param name="address">Bus address as ulong (0 for broadcast-only buses).
        /// EntityId-addressed buses pass the entity id here; other address types
        /// would need their own bridge.</param>
        /// <param name="dispatch">Callback to invoke when the bus fires an
        /// event for this handler. Owned by the source generator.</param>
        /// <returns>A handler token; pass to <see cref="Unregister"/> to disconnect.
        /// Returns 0 if registration failed on the native side.</returns>
        public static long Register(object instance, string busName, ulong address, DispatchCallback dispatch)
        {
            if (instance is null) throw new ArgumentNullException(nameof(instance));
            if (busName is null) throw new ArgumentNullException(nameof(busName));
            if (dispatch is null) throw new ArgumentNullException(nameof(dispatch));

            // Atomic counter increment; token 0 reserved for "invalid".
            long token = System.Threading.Interlocked.Increment(ref s_nextToken);
            s_handlers[token] = new HandlerEntry
            {
                Instance = instance,
                BusName = busName,
                Dispatch = dispatch,
            };

            try
            {
                unsafe
                {
                    long nativeResult = ReflectionInternalCalls.Reflection_RegisterEBusHandler(
                        busName, address, token);
                    if (nativeResult == 0)
                    {
                        // Native registration failed; clean up our side
                        // and return 0 so the caller knows to no-op the
                        // disconnect path.
                        s_handlers.TryRemove(token, out _);
                        return 0;
                    }
                }
            }
            catch (Exception ex)
            {
                // The internal call binding may not be resolved yet
                // (Phase 18-E1 ships the C# side and stubs the C++
                // bridge; if the C++ side hasn't landed in this build,
                // the unmanaged call throws). Swallow + clean up so
                // user scripts that use [EBus] don't crash the editor;
                // the handler just won't fire until the bridge lands.
                Debug.LogWarning(
                    $"[EBusHandlerRegistry] Native Reflection_RegisterEBusHandler " +
                    $"unavailable ({ex.GetType().Name}: {ex.Message}). The handler " +
                    $"is registered on the managed side but won't fire until the " +
                    $"C++ managed-handler bridge ships.");
                // We KEEP the managed entry registered so unit tests and
                // mocks can drive DispatchEvent directly without needing
                // the native bridge - they just call DispatchEvent(token, ...)
                // and exercise the managed shim.
            }
            return token;
        }

        /// <summary>
        /// Called by source-generated DisconnectFrom&lt;BusName&gt;().
        /// Removes the managed entry and asks the C++ side to tear down
        /// its BehaviorEBusHandler. Safe to call with token=0 (no-op).
        /// </summary>
        public static void Unregister(long token)
        {
            if (token == 0) return;
            if (!s_handlers.TryRemove(token, out _))
            {
                // Double-unregister or token from a failed Register;
                // log and bail.
                Debug.LogWarning($"[EBusHandlerRegistry] Unregister(token={token}): not found");
                return;
            }
            try
            {
                unsafe
                {
                    ReflectionInternalCalls.Reflection_UnregisterEBusHandler(token);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[EBusHandlerRegistry] Native Reflection_UnregisterEBusHandler " +
                    $"failed ({ex.GetType().Name}: {ex.Message}). Managed cleanup " +
                    $"already done; the native handler (if it exists) will leak " +
                    $"until the editor shuts down.");
            }
        }

        /// <summary>
        /// Called by the C++ managed-handler bridge when an EBus event
        /// fires for one of our registered handlers. Looks up the
        /// dispatch callback by token, invokes it with the event name +
        /// argsJson, returns the resulting JSON envelope.
        ///
        /// Returns null if the token isn't known (handler was destroyed
        /// or never existed), which the C++ side treats as "no handler
        /// registered" and falls through to the default broadcast value.
        /// </summary>
        public static string? DispatchEvent(long token, string eventName, string argsJson)
        {
            if (!s_handlers.TryGetValue(token, out var entry))
            {
                return null;
            }
            try
            {
                return entry.Dispatch(eventName, argsJson);
            }
            catch (Exception ex)
            {
                // A throwing handler shouldn't bring down the editor.
                // Log + swallow + return a synthetic error envelope so
                // the C++ side can record that the dispatch failed.
                Debug.LogError(
                    $"[EBusHandlerRegistry] Handler {entry.Instance.GetType().Name}." +
                    $"{eventName} threw {ex.GetType().Name}: {ex.Message}");
                return $"{{\"error\":\"managed handler threw: {ex.GetType().Name}: {ex.Message.Replace("\"", "\\\"")}\"}}";
            }
        }

        /// <summary>
        /// Test/diagnostic accessor. Returns the count of currently
        /// registered handlers. Useful for sanity-checking handler
        /// lifetime in tests + leak detection.
        /// </summary>
        public static int RegisteredHandlerCount => s_handlers.Count;
    }
}
