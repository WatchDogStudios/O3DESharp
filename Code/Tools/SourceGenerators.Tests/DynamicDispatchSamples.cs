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

        // The 8 non-EBus NativeReflection dispatch methods O3DESHARP1001 also
        // covers. Each gets a constant (CLOSED-WORLD, silent) and a dynamic
        // (OPEN-WORLD, must warn) call, exercising exactly the argument
        // position(s) that method's overload checks - not always 0/1 like the
        // EBus methods above. DestroyInstance is deliberately absent: it has
        // no name-shaped call-site argument (instance.TypeName is resolved
        // from the NativeObject at runtime, not literal at the call site).

        public static void ConstantInvokeStaticMethod()
        {
            NativeReflection.InvokeStaticMethod("KnownClass", "DoThing"); // CLOSED-WORLD
        }

        public static void DynamicInvokeStaticMethodClassName(string className)
        {
            NativeReflection.InvokeStaticMethod(className, "DoThing"); // OPEN-WORLD
        }

        public static void DynamicInvokeStaticMethodMethodName(string methodName)
        {
            NativeReflection.InvokeStaticMethod("KnownClass", methodName); // OPEN-WORLD
        }

        public static void ConstantInvokeInstanceMethod(NativeObject obj)
        {
            NativeReflection.InvokeInstanceMethod(obj, "DoThing"); // CLOSED-WORLD
        }

        public static void DynamicInvokeInstanceMethod(NativeObject obj, string methodName)
        {
            NativeReflection.InvokeInstanceMethod(obj, methodName); // OPEN-WORLD
        }

        public static void ConstantInvokeGlobalMethod()
        {
            NativeReflection.InvokeGlobalMethod("GlobalDoThing"); // CLOSED-WORLD
        }

        public static void DynamicInvokeGlobalMethod(string methodName)
        {
            NativeReflection.InvokeGlobalMethod(methodName); // OPEN-WORLD
        }

        public static void ConstantGetProperty(NativeObject obj)
        {
            NativeReflection.GetProperty<int>(obj, "Health"); // CLOSED-WORLD
        }

        public static void DynamicGetProperty(NativeObject obj, string propertyName)
        {
            NativeReflection.GetProperty<int>(obj, propertyName); // OPEN-WORLD
        }

        public static void ConstantSetProperty(NativeObject obj)
        {
            NativeReflection.SetProperty(obj, "Health", 5); // CLOSED-WORLD
        }

        public static void DynamicSetProperty(NativeObject obj, string propertyName)
        {
            NativeReflection.SetProperty(obj, propertyName, 5); // OPEN-WORLD
        }

        public static void ConstantGetGlobalProperty()
        {
            NativeReflection.GetGlobalProperty<bool>("GlobalFlag"); // CLOSED-WORLD
        }

        public static void DynamicGetGlobalProperty(string propertyName)
        {
            NativeReflection.GetGlobalProperty<bool>(propertyName); // OPEN-WORLD
        }

        public static void ConstantSetGlobalProperty()
        {
            NativeReflection.SetGlobalProperty("GlobalFlag", true); // CLOSED-WORLD
        }

        public static void DynamicSetGlobalProperty(string propertyName)
        {
            NativeReflection.SetGlobalProperty(propertyName, true); // OPEN-WORLD
        }

        public static void ConstantCreateInstance()
        {
            NativeReflection.CreateInstance("KnownType"); // CLOSED-WORLD
        }

        public static void DynamicCreateInstance(string className)
        {
            NativeReflection.CreateInstance(className); // OPEN-WORLD
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
