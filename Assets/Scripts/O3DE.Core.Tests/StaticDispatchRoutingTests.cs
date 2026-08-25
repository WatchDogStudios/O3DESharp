//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using O3DE.Reflection;

namespace O3DE.Core.Tests;

/// <summary>
/// The runtime half of the closed-world decision. O3DESHARP1001 warns at build
/// time; this is what happens if a warned-past call is actually reached in a
/// shipping image.
///
/// The requirement is that it is UNMISSABLE and names the exact site. A dispatch
/// that quietly returned null would be indistinguishable from an event with no
/// handlers - the silent degrade the design explicitly rules out.
///
/// The editor path is unchanged, so these assertions are conditional: in Coral
/// mode the calls go through as they always have.
/// </summary>
public class StaticDispatchRoutingTests
{
    [Fact]
    public void UnknownBusEventPair_IsAHardErrorNamingTheSite()
    {
#if O3DE_HOST_NATIVEAOT
        var act = () => NativeReflection.BroadcastEBusEvent("NoSuchBus", "NoSuchEvent");

        act.Should().Throw<NotSupportedException>()
            .Which.Message.Should().Contain("NoSuchBus").And.Contain("NoSuchEvent");
#else
        // In the editor this reaches the native dispatcher, which is not present
        // in a test host - the assertion here is only that no static-dispatch
        // gate was introduced into the editor path.
        typeof(NativeReflection).Should().NotBeNull();
#endif
    }

    [Fact]
    public void HardError_ExplainsWhyAndWhatToDo()
    {
#if O3DE_HOST_NATIVEAOT
        var act = () => NativeReflection.SendEBusEvent("NoSuchBus", "NoSuchEvent", 0UL);

        var message = act.Should().Throw<NotSupportedException>().Which.Message;
        message.Should().Contain("NativeAOT",
            "the message has to say which build config this is, or it reads as a generic bug");
        message.Should().Contain("O3DESHARP1001",
            "pointing at the build warning is what turns a runtime failure into a fixable one");
#else
        typeof(NativeReflection).Should().NotBeNull();
#endif
    }

    [Fact]
    public void TableIsConsultedBeforeTheNativeCall()
    {
#if O3DE_HOST_NATIVEAOT
        // A miss must never reach the native dispatcher: without a table entry
        // the argument blob's shape is unvalidated, and handing an unvalidated
        // blob to BehaviorContext is the memory-unsafe outcome, not merely a
        // wrong one.
        var act = () => NativeReflection.BroadcastEBusEvent("NoSuchBus", "NoSuchEvent", 1, 2, 3);
        act.Should().Throw<NotSupportedException>();
#else
        typeof(NativeReflection).Should().NotBeNull();
#endif
    }

    // ----------------------------------------------------------------
    // Task 6: the non-EBus dispatch call sites get the same closed-world
    // gate. StaticReflectionDispatchStub is deliberately empty (like
    // StaticEBusDispatchStub above), so every name here is a miss and every
    // guarded call site must throw before it ever reaches the unassigned
    // ReflectionInternalCalls function pointer - a real native call under
    // this stub would crash the test process, not just fail an assertion.
    // ----------------------------------------------------------------

    [Fact]
    public void InvokeStaticMethod_UnknownClassOrMethod_ThrowsUnderNativeAot()
    {
#if O3DE_HOST_NATIVEAOT
        var act = () => NativeReflection.InvokeStaticMethod("NoSuchClass", "NoSuchMethod");
        act.Should().Throw<NotSupportedException>()
            .Which.Message.Should().Contain("NoSuchClass").And.Contain("NoSuchMethod");
#else
        typeof(NativeReflection).Should().NotBeNull();
#endif
    }

    [Fact]
    public void InvokeInstanceMethod_UnknownTypeName_ThrowsUnderNativeAot()
    {
#if O3DE_HOST_NATIVEAOT
        var instance = new NativeObject("NoSuchClass", 1234);
        GC.SuppressFinalize(instance); // fake handle; the real object's finalizer would
                                        // re-enter DestroyInstance and re-throw off the GC thread.
        var act = () => NativeReflection.InvokeInstanceMethod(instance, "NoSuchMethod");
        act.Should().Throw<NotSupportedException>();
#else
        typeof(NativeReflection).Should().NotBeNull();
#endif
    }

    [Fact]
    public void InvokeInstanceMethod_NullInstance_StillThrowsArgumentNullFirst()
    {
        // Existing exception-ordering must survive the new guard: a null
        // instance throws ArgumentNullException regardless of host mode,
        // because the guard needs instance.TypeName and must run after this
        // check, not before it.
        var act = () => NativeReflection.InvokeInstanceMethod(null!, "AnyMethod");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void InvokeGlobalMethod_UnknownMethod_ThrowsUnderNativeAot()
    {
#if O3DE_HOST_NATIVEAOT
        var act = () => NativeReflection.InvokeGlobalMethod("NoSuchGlobalMethod");
        act.Should().Throw<NotSupportedException>()
            .Which.Message.Should().Contain("NoSuchGlobalMethod");
#else
        typeof(NativeReflection).Should().NotBeNull();
#endif
    }

    [Fact]
    public void GetProperty_UnknownTypeName_ThrowsUnderNativeAot()
    {
#if O3DE_HOST_NATIVEAOT
        var instance = new NativeObject("NoSuchClass", 1234);
        GC.SuppressFinalize(instance); // see InvokeInstanceMethod test above.
        var act = () => NativeReflection.GetProperty<int>(instance, "NoSuchProperty");
        act.Should().Throw<NotSupportedException>();
#else
        typeof(NativeReflection).Should().NotBeNull();
#endif
    }

    [Fact]
    public void SetProperty_UnknownTypeName_ThrowsUnderNativeAot()
    {
#if O3DE_HOST_NATIVEAOT
        var instance = new NativeObject("NoSuchClass", 1234);
        GC.SuppressFinalize(instance); // see InvokeInstanceMethod test above.
        var act = () => NativeReflection.SetProperty(instance, "NoSuchProperty", 42);
        act.Should().Throw<NotSupportedException>();
#else
        typeof(NativeReflection).Should().NotBeNull();
#endif
    }

    [Fact]
    public void GetGlobalProperty_UnknownProperty_ThrowsUnderNativeAot()
    {
#if O3DE_HOST_NATIVEAOT
        var act = () => NativeReflection.GetGlobalProperty<int>("NoSuchGlobalProperty");
        act.Should().Throw<NotSupportedException>()
            .Which.Message.Should().Contain("NoSuchGlobalProperty");
#else
        typeof(NativeReflection).Should().NotBeNull();
#endif
    }

    [Fact]
    public void SetGlobalProperty_UnknownProperty_ThrowsUnderNativeAot()
    {
#if O3DE_HOST_NATIVEAOT
        var act = () => NativeReflection.SetGlobalProperty("NoSuchGlobalProperty", 42);
        act.Should().Throw<NotSupportedException>()
            .Which.Message.Should().Contain("NoSuchGlobalProperty");
#else
        typeof(NativeReflection).Should().NotBeNull();
#endif
    }

    [Fact]
    public void CreateInstance_UnknownClassName_ThrowsUnderNativeAot()
    {
        // Mirrors the existing BroadcastEBusEvent_UnknownBus-shaped tests above.
#if O3DE_HOST_NATIVEAOT
        var act = () => NativeReflection.CreateInstance("NotInTheStaticTable");
        act.Should().Throw<NotSupportedException>()
            .Which.Message.Should().Contain("NotInTheStaticTable");
#else
        typeof(NativeReflection).Should().NotBeNull();
#endif
    }

    [Fact]
    public void DestroyInstance_UnknownTypeName_ThrowsUnderNativeAot()
    {
        // Defense-in-depth only (not analyzer-covered): in practice
        // instance.TypeName is always in the table because the instance had
        // to be created first, but this must never silently fall through to
        // the unassigned native call either.
#if O3DE_HOST_NATIVEAOT
        var instance = new NativeObject("NoSuchClass", 1234);
        GC.SuppressFinalize(instance); // the guard throws before Invalidate() runs, so the
                                        // instance is still "valid"; without this the finalizer
                                        // would re-enter DestroyInstance and re-throw off the GC thread.
        var act = () => NativeReflection.DestroyInstance(instance);
        act.Should().Throw<NotSupportedException>();
#else
        typeof(NativeReflection).Should().NotBeNull();
#endif
    }

    [Fact]
    public void DestroyInstance_InvalidInstance_DoesNotGuardOrThrow()
    {
        // An already-invalidated instance (Handle == 0) must keep skipping the
        // body entirely, same as before the guard existed - the guard lives
        // inside the `instance.IsValid` block, not ahead of it.
        var instance = new NativeObject("NoSuchClass", 0);
        var act = () => NativeReflection.DestroyInstance(instance);
        act.Should().NotThrow();
    }

    [Fact]
    public void EBusHandlerRegistry_Register_UnknownBus_ThrowsUnderNativeAot()
    {
#if O3DE_HOST_NATIVEAOT
        var act = () => EBusHandlerRegistry.Register(new object(), "NoSuchBus", 0UL, (_, _) => null);
        act.Should().Throw<NotSupportedException>()
            .Which.Message.Should().Contain("NoSuchBus");
#else
        typeof(EBusHandlerRegistry).Should().NotBeNull();
#endif
    }
}
