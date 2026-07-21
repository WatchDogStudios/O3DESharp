//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using System.Collections.Generic;
using O3DE;
using O3DE.Interop;

namespace O3DE.Core.Tests.Interop;

/// <summary>
/// The handle table is what makes a STATIC [UnmanagedCallersOnly] thunk able to
/// service INSTANCE lifecycle callbacks: C++ holds an opaque int per component
/// and hands it back on every call. These pin the properties the native side
/// relies on - handles are never 0 (0 is the native "no handle" sentinel),
/// they are not reused while live, and resolving a stale handle is safe.
/// </summary>
public class ScriptComponentBridgeTests
{
    [Fact]
    public void Register_ReturnsNonZeroHandle()
    {
        var handle = ScriptComponentBridge.Register(new object());
        try
        {
            handle.Should().NotBe(0, "0 is the native sentinel for 'no handle'");
        }
        finally
        {
            ScriptComponentBridge.Unregister(handle);
        }
    }

    [Fact]
    public void Resolve_ReturnsTheRegisteredInstance()
    {
        var instance = new object();
        var handle = ScriptComponentBridge.Register(instance);
        try
        {
            ScriptComponentBridge.Resolve(handle).Should().BeSameAs(instance);
        }
        finally
        {
            ScriptComponentBridge.Unregister(handle);
        }
    }

    [Fact]
    public void DistinctInstances_GetDistinctHandles()
    {
        var a = ScriptComponentBridge.Register(new object());
        var b = ScriptComponentBridge.Register(new object());
        try
        {
            a.Should().NotBe(b);
        }
        finally
        {
            ScriptComponentBridge.Unregister(a);
            ScriptComponentBridge.Unregister(b);
        }
    }

    [Fact]
    public void Resolve_AfterUnregister_ReturnsNull()
    {
        var handle = ScriptComponentBridge.Register(new object());
        ScriptComponentBridge.Unregister(handle);

        // Native code can legitimately race a teardown against an in-flight
        // tick; resolving a dead handle must be safe, not throw.
        ScriptComponentBridge.Resolve(handle).Should().BeNull();
    }

    [Fact]
    public void Resolve_UnknownHandle_ReturnsNull()
    {
        ScriptComponentBridge.Resolve(0).Should().BeNull();
        ScriptComponentBridge.Resolve(999999).Should().BeNull();
        ScriptComponentBridge.Resolve(-1).Should().BeNull();
    }

    [Fact]
    public void Unregister_IsIdempotent()
    {
        var handle = ScriptComponentBridge.Register(new object());
        ScriptComponentBridge.Unregister(handle);

        // Double-unregister must not throw - teardown paths can run twice.
        var act = () => ScriptComponentBridge.Unregister(handle);
        act.Should().NotThrow();
    }
}

/// <summary>
/// Pins the native ABI. LifecycleId values are passed as raw integers from
/// CSharpScriptComponent.cpp's enum of the same name; if these drift apart,
/// Tick starts calling OnDestroy and nothing fails to compile on either side.
/// </summary>
public class LifecycleIdAbiTests
{
    [Theory]
    [InlineData(LifecycleId.OnCreate, 1)]
    [InlineData(LifecycleId.OnDestroy, 2)]
    [InlineData(LifecycleId.Tick, 3)]
    [InlineData(LifecycleId.OnTransformChanged, 4)]
    public void LifecycleId_HasStableNativeValue(LifecycleId id, int expected)
    {
        ((int)id).Should().Be(expected,
            "these integers are the native ABI - renumbering silently misroutes callbacks");
    }

    [Fact]
    public void Dispatch_UnknownLifecycleId_ReturnsFalse()
    {
        var component = new DispatchProbe();
        ScriptComponentBridge.Dispatch(component, (LifecycleId)9999, 0f).Should().BeFalse();
        component.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Dispatch_NonScriptComponent_ReturnsFalse()
    {
        ScriptComponentBridge.Dispatch(new object(), LifecycleId.Tick, 0f).Should().BeFalse();
    }

    [Fact]
    public void Dispatch_RoutesTickWithDeltaTime()
    {
        var component = new DispatchProbe();

        ScriptComponentBridge.Dispatch(component, LifecycleId.Tick, 0.25f).Should().BeTrue();

        component.Calls.Should().ContainSingle().Which.Should().Be("Tick:0.25");
    }

    [Theory]
    [InlineData(LifecycleId.OnCreate, "OnCreate")]
    [InlineData(LifecycleId.OnDestroy, "OnDestroy")]
    [InlineData(LifecycleId.OnTransformChanged, "OnTransformChanged")]
    public void Dispatch_RoutesEachLifecycleToItsCallback(LifecycleId id, string expected)
    {
        var component = new DispatchProbe();

        ScriptComponentBridge.Dispatch(component, id, 0f).Should().BeTrue();

        component.Calls.Should().ContainSingle().Which.Should().Be(expected);
    }
}

/// <summary>
/// Records which callbacks fired, so routing can be asserted.
///
/// NOTE: overrides OnUpdate, NOT Tick. In ScriptComponent, `Tick(float)` is a
/// public NON-virtual method (the native entry point, which also drives the
/// Invoke/InvokeRepeating timer machinery) and it calls the virtual
/// `OnUpdate(float)`. Attempting `override void Tick` does not compile.
/// Dispatching LifecycleId.Tick therefore surfaces here as an OnUpdate call.
/// </summary>
internal sealed class DispatchProbe : ScriptComponent
{
    public List<string> Calls { get; } = new List<string>();

    public override void OnCreate() => Calls.Add("OnCreate");
    public override void OnDestroy() => Calls.Add("OnDestroy");
    public override void OnUpdate(float deltaTime) =>
        Calls.Add($"Tick:{deltaTime.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    public override void OnTransformChanged() => Calls.Add("OnTransformChanged");
}
