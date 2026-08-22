//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using System.Collections.Generic;
using O3DE.Interop;

namespace O3DE.Core.Tests.Interop;

/// <summary>
/// The five ManagedExports bodies. They are plain managed methods precisely so
/// they can be tested here; the generated [UnmanagedCallersOnly] thunks are
/// thin UTF-8 marshaling wrappers with no logic of their own.
///
/// The invariant running through all of these: an export must never throw. Its
/// caller is native code across an [UnmanagedCallersOnly] boundary, where an
/// escaping exception terminates the process rather than being catchable.
/// </summary>
public class ManagedExportsImplTests : IDisposable
{
    public ManagedExportsImplTests()
    {
        ScriptTypeRegistry.Clear();
        ScriptComponentBridge.ClearAll();
    }

    public void Dispose()
    {
        ScriptTypeRegistry.Clear();
        ScriptComponentBridge.ClearAll();
    }

    private sealed class Probe : ScriptComponent
    {
        public List<string> Calls { get; } = new List<string>();
        public override void OnCreate() => Calls.Add("OnCreate");
        public override void OnDestroy() => Calls.Add("OnDestroy");
        public override void OnUpdate(float dt) => Calls.Add("Tick");
    }

    [Fact]
    public void CreateInstance_UnknownType_ReturnsZeroHandle()
    {
        ManagedExportsImpl.CreateInstance("Nope.Missing").Should().Be(0,
            "0 is the native 'no handle' sentinel; native code must not get a live handle for a dead name");
    }

    [Fact]
    public void CreateInstance_NullName_ReturnsZeroHandle()
    {
        ManagedExportsImpl.CreateInstance(null!).Should().Be(0);
    }

    [Fact]
    public void CreateInstance_RegisteredType_ReturnsResolvableHandle()
    {
        ScriptTypeRegistry.Register("Probe", () => new Probe());

        int handle = ManagedExportsImpl.CreateInstance("Probe");

        handle.Should().NotBe(0);
        ScriptComponentBridge.Resolve(handle).Should().BeOfType<Probe>();
    }

    [Fact]
    public void InvokeLifecycle_RoutesToTheComponent()
    {
        ScriptTypeRegistry.Register("Probe", () => new Probe());
        int handle = ManagedExportsImpl.CreateInstance("Probe");

        ManagedExportsImpl.InvokeLifecycle(handle, (int)LifecycleId.OnCreate, 0f).Should().Be(1);
        ManagedExportsImpl.InvokeLifecycle(handle, (int)LifecycleId.Tick, 0.25f).Should().Be(1);

        var probe = (Probe)ScriptComponentBridge.Resolve(handle)!;
        probe.Calls.Should().Equal("OnCreate", "Tick");
    }

    [Fact]
    public void InvokeLifecycle_DeadHandle_ReturnsZeroWithoutThrowing()
    {
        // Native teardown can race an in-flight tick. Zero means "nothing to
        // do", which is the correct outcome, not an error.
        ManagedExportsImpl.InvokeLifecycle(999999, (int)LifecycleId.Tick, 0f).Should().Be(0);
    }

    [Fact]
    public void InvokeLifecycle_UnknownLifecycleId_ReturnsZero()
    {
        ScriptTypeRegistry.Register("Probe", () => new Probe());
        int handle = ManagedExportsImpl.CreateInstance("Probe");

        ManagedExportsImpl.InvokeLifecycle(handle, 9999, 0f).Should().Be(0);
    }

    [Fact]
    public void DestroyInstance_ReleasesTheHandle()
    {
        ScriptTypeRegistry.Register("Probe", () => new Probe());
        int handle = ManagedExportsImpl.CreateInstance("Probe");

        ManagedExportsImpl.DestroyInstance(handle);

        ScriptComponentBridge.Resolve(handle).Should().BeNull();
    }

    [Fact]
    public void DestroyInstance_UnknownHandle_IsSilentlyFine()
    {
        var act = () => ManagedExportsImpl.DestroyInstance(4242);
        act.Should().NotThrow("teardown paths can run twice");
    }

    [Fact]
    public void DispatchEBusEvent_UnknownToken_ReturnsNull()
    {
        // Null means "no handler took this", which the native side reports as
        // "0 bytes needed", not as an error.
        ManagedExportsImpl.DispatchEBusEvent(0L, "OnTick", "[]").Should().BeNull();
        ManagedExportsImpl.DispatchEBusEvent(123456L, "OnTick", "[]").Should().BeNull();
    }

    [Fact]
    public void DispatchEBusEvent_MalformedArgsJson_ReturnsNullRatherThanThrowing()
    {
        ManagedExportsImpl.DispatchEBusEvent(1L, "OnTick", "{not json").Should().BeNull();
    }

    [Fact]
    public void HotReloadSwap_InTheCoralBuild_ClearsStateAndSucceeds()
    {
        ScriptTypeRegistry.Register("Probe", () => new Probe());
        ManagedExportsImpl.CreateInstance("Probe");

        int result = ManagedExportsImpl.HotReloadSwap();

#if O3DE_HOST_NATIVEAOT
        result.Should().Be(0, "a NativeAOT image has no AssemblyLoadContext to swap");
#else
        result.Should().Be(1);
        ScriptComponentBridge.ClearAll().Should().Be(0, "the swap already dropped every handle");
        ScriptTypeRegistry.Count.Should().Be(0, "the swap already dropped every registration");
#endif
    }
}
