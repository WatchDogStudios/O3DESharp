/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using O3DE;
using O3DE.Reflection;
using static O3DE.Reflection.NativeReflection;

namespace O3DESharp.SourceGenerators.Smoke
{
    /// <summary>
    /// Fixture for the closed-world diagnostic. Every call below is tagged so
    /// Editor/Tests/test_closed_world_diagnostic.py can assert BOTH directions:
    /// that open-world sites warn, and - just as importantly - that closed-world
    /// sites do not. A false positive trains people to ignore the diagnostic,
    /// which is worse than not having it.
    /// </summary>
    public static class DynamicDispatchSamples
    {
        private const string ConstBus = "TickBus";

        public static void ConstantNames(float dt, ulong entityId)
        {
            NativeReflection.BroadcastEBusEvent("TickBus", "OnTick", dt); // CLOSED-WORLD
            NativeReflection.SendEBusEvent("TransformBus", "GetWorldTranslation", entityId); // CLOSED-WORLD
            NativeReflection.BroadcastEBusEvent(ConstBus, "OnTick", dt); // CLOSED-WORLD
            NativeReflection.BroadcastEBusEvent("Tick" + "Bus", "OnTick", dt); // CLOSED-WORLD
        }

        public static void RuntimeComputedNames(string bus, string evt, float dt, ulong entityId)
        {
            NativeReflection.BroadcastEBusEvent(bus, "OnTick", dt); // OPEN-WORLD
            NativeReflection.BroadcastEBusEvent("TickBus", evt, dt); // OPEN-WORLD
            NativeReflection.SendEBusEvent(bus, evt, entityId); // OPEN-WORLD
            NativeReflection.BroadcastEBusEvent($"{bus}Notifications", "OnTick", dt); // OPEN-WORLD
        }

        // Reached via `using static O3DE.Reflection.NativeReflection;` above,
        // so the invocation expression is an unqualified IdentifierNameSyntax
        // rather than a MemberAccessExpressionSyntax. Regression case for the
        // syntactic pre-filter that used to only accept the qualified shape
        // and silently skipped this one.
        public static void UnqualifiedRuntimeComputedNames(string bus, float dt)
        {
            BroadcastEBusEvent(bus, "OnTick", dt); // OPEN-WORLD
        }

        // Same method names, unrelated type. Proves the analyzer's semantic
        // ContainingType check - not just the method name - is what decides
        // correctness: these look exactly like the open-world calls above,
        // but must NOT warn.
        public static void UnrelatedTypeSameMethodNames(string bus, string evt, float dt)
        {
            NotNativeReflection.BroadcastEBusEvent(bus, "OnTick", dt); // UNRELATED-TYPE-NO-WARN
            NotNativeReflection.SendEBusEvent(bus, evt, dt); // UNRELATED-TYPE-NO-WARN
        }
    }

    /// <summary>
    /// Unrelated type with method names that collide with NativeReflection's
    /// EBus API. Exists only so DynamicDispatchSamples.UnrelatedTypeSameMethodNames
    /// can prove the analyzer discriminates by ContainingType, not by name.
    /// </summary>
    public static class NotNativeReflection
    {
        public static object? BroadcastEBusEvent(string busName, string eventName, params object[] args) => null;
        public static object? SendEBusEvent(string busName, string eventName, params object[] args) => null;
    }
}
