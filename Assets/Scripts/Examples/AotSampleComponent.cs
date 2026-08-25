/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using O3DE;
using O3DE.Reflection;

namespace Examples
{
    /// <summary>
    /// The sample game's NativeAOT exercise.
    ///
    /// Three things have to be true of it, and each is asserted somewhere:
    ///   1. It is a concrete ScriptComponent subclass with a public
    ///      parameterless constructor, so HostExportsGenerator emits a
    ///      ScriptTypeRegistry factory for it and a shipping image can
    ///      construct it without Activator.
    ///   2. Its normal EBus traffic uses constant names, so it dispatches
    ///      statically and stays silent under O3DESHARP1001.
    ///   3. It has one deliberately-dynamic call, so the closed-world
    ///      diagnostic is proven to fire on real game code rather than only on
    ///      a synthetic fixture.
    /// </summary>
    public class AotSampleComponent : ScriptComponent
    {
        [ExposedProperty("Broadcast Interval")]
        public float BroadcastInterval = 1.0f;

        private float _elapsed;

        public override void OnCreate()
        {
            Debug.Log("[AotSample] created");
        }

        public override void OnUpdate(float deltaTime)
        {
            _elapsed += deltaTime;
            if (_elapsed < BroadcastInterval)
            {
                return;
            }
            _elapsed = 0.0f;

            // Constant bus and event names: resolvable at compile time, so the
            // generated table covers them and the shipping image dispatches
            // without touching managed reflection.
            NativeReflection.BroadcastEBusEvent("TickBus", "OnTick", deltaTime); // CLOSED-WORLD
        }

        /// <summary>
        /// Deliberately dynamic. This is expected to produce O3DESHARP1001 at
        /// build time and NotSupportedException if reached in a NativeAOT image
        /// - it is the sample's proof that the restriction is diagnosed rather
        /// than silently degraded, so the warning here is intentional and must
        /// NOT be suppressed.
        /// </summary>
        public void DispatchByRuntimeName(string busName, string eventName)
        {
            NativeReflection.BroadcastEBusEvent(busName, eventName); // OPEN-WORLD
        }
    }
}
