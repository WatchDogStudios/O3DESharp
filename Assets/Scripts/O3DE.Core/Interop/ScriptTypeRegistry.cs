/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.Collections.Generic;

namespace O3DE.Interop
{
    /// <summary>
    /// Maps a script type name to a factory that constructs it.
    ///
    /// ManagedExports.CreateInstance receives a type NAME (it comes out of a
    /// component's serialized config). The reflective way to turn that into an
    /// object - Assembly.GetType + Activator.CreateInstance - is precisely what
    /// NativeAOT cannot statically see, so the shipping image would fail to
    /// construct any script at all.
    ///
    /// Instead, HostExportsGenerator emits one
    ///     Register("Ns.Type", static () => new Ns.Type());
    /// per ScriptComponent subclass at compile time. That is a direct `new`,
    /// visible to the AOT compiler, and it behaves identically under CoreCLR -
    /// so the editor and the shipping build share one code path rather than
    /// diverging at the one point most likely to break silently.
    /// </summary>
    public static class ScriptTypeRegistry
    {
        private static readonly object s_lock = new object();
        private static readonly Dictionary<string, Func<object>> s_factories =
            new Dictionary<string, Func<object>>(StringComparer.Ordinal);

        /// <summary>Number of registered script types.</summary>
        public static int Count
        {
            get { lock (s_lock) { return s_factories.Count; } }
        }

        /// <summary>
        /// Register (or replace) the factory for a script type. Replacing is a
        /// normal outcome: a hot-reload re-runs the generated registrations
        /// against the newly loaded assembly.
        /// </summary>
        public static void Register(string typeName, Func<object> factory)
        {
            if (typeName is null) throw new ArgumentNullException(nameof(typeName));
            if (factory is null) throw new ArgumentNullException(nameof(factory));

            lock (s_lock)
            {
                s_factories[typeName] = factory;
            }
        }

        /// <summary>True if a factory is registered for this type name.</summary>
        public static bool Contains(string typeName)
        {
            if (typeName is null) return false;
            lock (s_lock) { return s_factories.ContainsKey(typeName); }
        }

        /// <summary>
        /// Construct an instance, or null if the name is unknown or the factory
        /// threw. Never throws: the only caller is an [UnmanagedCallersOnly]
        /// thunk, and an exception crossing that boundary terminates the
        /// process instead of being catchable.
        /// </summary>
        public static object? Create(string typeName)
        {
            if (typeName is null) return null;

            Func<object>? factory;
            lock (s_lock)
            {
                if (!s_factories.TryGetValue(typeName, out factory))
                {
                    return null;
                }
            }

            try
            {
                // Deliberately outside the lock: a user constructor can run
                // arbitrary code, including registering more types.
                return factory();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ScriptTypeRegistry] Constructing '{typeName}' threw: {ex}");
                return null;
            }
        }

        /// <summary>Drop every registration. Called before a hot-reload swap.</summary>
        public static void Clear()
        {
            lock (s_lock) { s_factories.Clear(); }
        }
    }
}
