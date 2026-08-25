/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using O3DE.Reflection;

namespace O3DE.Interop
{
    /// <summary>
    /// Bodies of the five ManagedExports entry points.
    ///
    /// Kept as ordinary managed methods so they are unit-testable; the
    /// [UnmanagedCallersOnly] thunks that HostExportsGenerator emits into
    /// ManagedExports.g.cs are thin UTF-8 marshaling wrappers with no logic of
    /// their own. Same split ScriptComponentBridge already uses for
    /// Invoke/Dispatch, and for the same reason: an [UnmanagedCallersOnly]
    /// method cannot be called from managed code at all.
    ///
    /// Every method here is total - it returns a sentinel rather than throwing.
    /// The caller is native code across an [UnmanagedCallersOnly] boundary,
    /// where an escaping exception terminates the process.
    /// </summary>
    public static class ManagedExportsImpl
    {
        /// <summary>
        /// Construct a script instance by name and return its native handle.
        /// 0 means failure (unknown type, or the constructor threw) - native
        /// code treats 0 as "no component".
        /// </summary>
        public static int CreateInstance(string typeName)
        {
            try
            {
                object? instance = ScriptTypeRegistry.Create(typeName);
                return instance is null ? 0 : ScriptComponentBridge.Register(instance);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManagedExports] CreateInstance('{typeName}') failed: {ex}");
                return 0;
            }
        }

        /// <summary>
        /// Dispatch one lifecycle callback. Returns 1 if it was dispatched, 0
        /// if the handle is dead or the id is unknown. A dead handle is a
        /// normal outcome (teardown racing an in-flight tick), not an error.
        /// </summary>
        public static int InvokeLifecycle(int handle, int lifecycleId, float arg)
        {
            try
            {
                object? instance = ScriptComponentBridge.Resolve(handle);
                if (instance is null)
                {
                    return 0;
                }
                return ScriptComponentBridge.Dispatch(instance, (LifecycleId)lifecycleId, arg) ? 1 : 0;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManagedExports] InvokeLifecycle(handle={handle}, id={lifecycleId}) failed: {ex}");
                return 0;
            }
        }

        /// <summary>
        /// Route an EBus event to the managed handler registered under
        /// <paramref name="token"/>. Returns the handler's JSON result, or null
        /// when no handler took it (which the thunk reports as "0 bytes
        /// needed", not as an error).
        /// </summary>
        public static string? DispatchEBusEvent(long token, string eventName, string argsJson)
        {
            try
            {
                return EBusHandlerRegistry.DispatchEvent(token, eventName, argsJson);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManagedExports] DispatchEBusEvent(token={token}, event='{eventName}') failed: {ex}");
                return null;
            }
        }

        /// <summary>Release a script instance handle. Safe to call twice.</summary>
        public static void DestroyInstance(int handle)
        {
            try
            {
                ScriptComponentBridge.Unregister(handle);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManagedExports] DestroyInstance(handle={handle}) failed: {ex}");
            }
        }

        /// <summary>
        /// Prepare the managed side for an AssemblyLoadContext swap: drop every
        /// live handle and every type registration, because both point into the
        /// context about to be unloaded, and clear the reflection caches.
        /// Returns 1 on success.
        ///
        /// In a NativeAOT image there is no ALC and no hot-reload by design, so
        /// this returns 0 and the host reports SupportsHotReload() == false.
        /// </summary>
        public static int HotReloadSwap()
        {
#if O3DE_HOST_NATIVEAOT
            // Not a failure - hot-reload is editor-only by design. Returning 0
            // is how the host learns that, rather than by probing for it.
            return 0;
#else
            try
            {
                ScriptComponentBridge.ClearAll();
                ScriptTypeRegistry.Clear();
                NativeReflection.ClearCache();
                return 1;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManagedExports] HotReloadSwap failed: {ex}");
                return 0;
            }
#endif
        }
    }
}
