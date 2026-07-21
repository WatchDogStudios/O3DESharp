//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace O3DE.Interop
{
    /// <summary>
    /// Which lifecycle callback a native Invoke is requesting. Values are part
    /// of the native ABI - C++ passes these integers - so they must never be
    /// renumbered without changing CSharpScriptComponent.cpp in lockstep.
    /// </summary>
    public enum LifecycleId
    {
        OnCreate = 1,
        OnDestroy = 2,
        Tick = 3,
        OnTransformChanged = 4,
    }

    /// <summary>
    /// The native-callable entry point for script component lifecycle calls.
    ///
    /// Coral's ManagedObject.InvokeMethod does a managed-side string lookup plus
    /// reflection dispatch on every call, which for Tick means every frame per
    /// component. This bridge is resolved once to a raw function pointer via
    /// Coral's GetFunctionPointer, after which calls cost an indirect call plus
    /// an array index.
    ///
    /// The thunk target must be static, but lifecycle callbacks are per
    /// instance, so C++ holds an opaque int handle per component and passes it
    /// back on every call. Handle 0 is reserved as the native "no handle"
    /// sentinel and is never issued.
    /// </summary>
    public static class ScriptComponentBridge
    {
        private static readonly object s_lock = new object();
        private static readonly Dictionary<int, object> s_instances = new Dictionary<int, object>();
        private static int s_nextHandle = 1; // 0 is the "no handle" sentinel

        /// <summary>Register an instance and return its native handle (never 0).</summary>
        public static int Register(object instance)
        {
            if (instance is null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            lock (s_lock)
            {
                int handle = s_nextHandle++;
                if (s_nextHandle == 0)
                {
                    // Wrapped past int.MaxValue; skip the sentinel.
                    s_nextHandle = 1;
                }
                s_instances[handle] = instance;
                return handle;
            }
        }

        /// <summary>Drop a handle. Safe to call more than once.</summary>
        public static void Unregister(int handle)
        {
            lock (s_lock)
            {
                s_instances.Remove(handle);
            }
        }

        /// <summary>Resolve a handle, or null if it is unknown or already released.</summary>
        public static object? Resolve(int handle)
        {
            lock (s_lock)
            {
                return s_instances.TryGetValue(handle, out var instance) ? instance : null;
            }
        }

        /// <summary>
        /// Native entry point. Returns 1 if the call was dispatched, 0 if the
        /// handle was dead or the component does not implement the callback -
        /// in which case the native side simply does nothing (it must NOT fall
        /// back to InvokeMethod here; a dead handle means the component is gone).
        ///
        /// Must never throw: an exception crossing an [UnmanagedCallersOnly]
        /// boundary terminates the process.
        /// </summary>
        [UnmanagedCallersOnly]
        public static int Invoke(int handle, int lifecycleId, float arg)
        {
            try
            {
                object? instance = Resolve(handle);
                if (instance is null)
                {
                    return 0;
                }

                return Dispatch(instance, (LifecycleId)lifecycleId, arg) ? 1 : 0;
            }
            catch (Exception ex)
            {
                // Swallow: never let an exception cross the native boundary.
                Debug.LogError($"ScriptComponentBridge.Invoke failed: {ex}");
                return 0;
            }
        }

        /// <summary>
        /// Route to the concrete callback. Separated from Invoke so it is
        /// reachable from tests (Invoke itself is [UnmanagedCallersOnly] and
        /// cannot be called from managed code).
        /// </summary>
        internal static bool Dispatch(object instance, LifecycleId id, float arg)
        {
            if (instance is not ScriptComponent component)
            {
                return false;
            }

            switch (id)
            {
                case LifecycleId.OnCreate:
                    component.OnCreate();
                    return true;
                case LifecycleId.OnDestroy:
                    component.OnDestroy();
                    return true;
                case LifecycleId.Tick:
                    component.Tick(arg);
                    return true;
                case LifecycleId.OnTransformChanged:
                    component.OnTransformChanged();
                    return true;
                default:
                    return false;
            }
        }
    }
}
