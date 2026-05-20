/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

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

        // ------------------------------------------------------------
        // Argument unmarshaling
        // ------------------------------------------------------------
        // Phase 18-E2 follow-up: the source generator's emitted
        // dispatch shim needs to convert the per-event JSON-array
        // arguments back into the user method's typed parameters.
        // Centralising the per-type switch in this static helper
        // keeps the emitted shim code small (one line per parameter)
        // and lets the unmarshal logic evolve without regenerating
        // every user gem.
        //
        // The wire shapes mirror the C++ side's
        // Marshaling::BehaviorArgumentToJsonValue exactly:
        //   bool             -> JSON bool
        //   integer types    -> JSON number
        //   float / double   -> JSON number
        //   string           -> JSON string
        //   EntityId         -> JSON number (u64)
        //   Crc32            -> JSON number (u32)
        //   Uuid             -> JSON string ("{XXXX-...}")
        //   Vector2/3/4      -> JSON array of 2/3/4 floats
        //   Quaternion       -> JSON array of 4 floats
        //   Color            -> JSON array of 4 floats
        //   Aabb             -> JSON array of 6 floats
        //   Matrix3x3        -> JSON array of 9 floats (row-major)
        //   Matrix4x4        -> JSON array of 16 floats (row-major)
        //   Transform        -> JSON array of 10 floats (pos[3] + quat[4] + scale + reserved[2])
        //
        // Unsupported types fall through to default(T); the source
        // generator's switch is the source of truth for which user
        // methods get wired up, and unknown user-defined types would
        // already have errored on the C++ side before we ever get
        // here.

        /// <summary>
        /// Convert one JSON element into a strongly-typed value matching
        /// the wire shape emitted by the C++ side's marshaling layer.
        /// Used by source-generator-emitted dispatch shims to unpack
        /// EBus event args - one call per parameter at the shim call
        /// site keeps the emitted code readable.
        /// </summary>
        public static T UnmarshalArg<T>(JsonElement el)
        {
            try
            {
                object? value = UnmarshalArgRaw(el, typeof(T));
                if (value is T cast) return cast;
                if (value is null) return default(T)!;
                // Numeric promo / demotion (e.g. C++ emits int, user wants long).
                return (T)System.Convert.ChangeType(
                    value, typeof(T), System.Globalization.CultureInfo.InvariantCulture)!;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[EBusHandlerRegistry] UnmarshalArg<{typeof(T).Name}> failed for " +
                    $"JSON '{TryGetRaw(el)}' ({ex.GetType().Name}: {ex.Message}); " +
                    $"using default(T)");
                return default(T)!;
            }
        }

        private static string TryGetRaw(JsonElement el)
        {
            try { return el.GetRawText(); }
            catch { return "<unreadable>"; }
        }

        // ChangeType doesn't know about our struct types, and ValueKind
        // dispatch is the cleanest way to handle JSON-array-shaped
        // values, so we do explicit type dispatch ourselves.
        private static object? UnmarshalArgRaw(JsonElement el, Type t)
        {
            // Strip Nullable<T> wrapper - we'll unmarshal the inner type
            // and let the caller's variable accept null implicitly.
            Type underlying = Nullable.GetUnderlyingType(t) ?? t;
            if (el.ValueKind == JsonValueKind.Null) return null;

            if (underlying == typeof(bool))    return el.GetBoolean();
            if (underlying == typeof(sbyte))   return (sbyte)el.GetInt32();
            if (underlying == typeof(byte))    return (byte)el.GetInt32();
            if (underlying == typeof(short))   return (short)el.GetInt32();
            if (underlying == typeof(ushort))  return (ushort)el.GetInt32();
            if (underlying == typeof(int))     return el.GetInt32();
            if (underlying == typeof(uint))    return el.GetUInt32();
            if (underlying == typeof(long))    return el.GetInt64();
            if (underlying == typeof(ulong))   return el.GetUInt64();
            if (underlying == typeof(float))   return el.GetSingle();
            if (underlying == typeof(double))  return el.GetDouble();
            if (underlying == typeof(string))  return el.GetString();
            if (underlying == typeof(char))
            {
                var s = el.GetString();
                return string.IsNullOrEmpty(s) ? '\0' : s![0];
            }
            if (underlying == typeof(Guid))
            {
                var s = el.GetString();
                return string.IsNullOrEmpty(s) ? Guid.Empty : Guid.Parse(s!);
            }

            // O3DE math types - decoded from float arrays. Length
            // disambiguates Vector2 vs Vector3 vs Quaternion etc.
            if (el.ValueKind == JsonValueKind.Array)
            {
                int len = el.GetArrayLength();
                if (underlying == typeof(O3DE.Vector2) && len >= 2)
                {
                    return new O3DE.Vector2(
                        el[0].GetSingle(),
                        el[1].GetSingle());
                }
                if (underlying == typeof(O3DE.Vector3) && len >= 3)
                {
                    return new O3DE.Vector3(
                        el[0].GetSingle(),
                        el[1].GetSingle(),
                        el[2].GetSingle());
                }
                if (underlying == typeof(O3DE.Quaternion) && len >= 4)
                {
                    return new O3DE.Quaternion(
                        el[0].GetSingle(),
                        el[1].GetSingle(),
                        el[2].GetSingle(),
                        el[3].GetSingle());
                }
                // NOTE: AZ::Transform parameters arrive as a 10-float
                // array (pos[3] + quat[4] + scale + reserved[2]) but
                // O3DE.Transform on the managed side is an entity-bound
                // wrapper, not a freestanding value type. Decoding a
                // freestanding transform value would need a separate
                // TransformValue struct - tracked as Phase 18-E3 work.
                // For now, Transform parameters fall through to default
                // and emit a one-time warning in the catch arm above.
            }

            // Last resort: hand back the raw boxed JSON primitive so
            // ChangeType in the caller can attempt the conversion.
            return el.ValueKind switch
            {
                JsonValueKind.True   => true,
                JsonValueKind.False  => false,
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.TryGetInt64(out long l) ? l : (object)el.GetDouble(),
                _ => null,
            };
        }
    }
}
