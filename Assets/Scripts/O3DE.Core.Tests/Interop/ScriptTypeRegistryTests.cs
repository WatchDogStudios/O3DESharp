//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using O3DE.Interop;

namespace O3DE.Core.Tests.Interop;

/// <summary>
/// ManagedExports.CreateInstance turns a type NAME into an object. The
/// reflective way to do that - Assembly.GetType + Activator.CreateInstance -
/// is exactly what NativeAOT cannot see through. A registry of generated
/// `static () => new T()` factories is AOT-safe and behaves identically in
/// the editor, so both artifacts share one code path.
/// </summary>
[Collection("GlobalScriptState")]
public class ScriptTypeRegistryTests : IDisposable
{
    public ScriptTypeRegistryTests() => ScriptTypeRegistry.Clear();
    public void Dispose() => ScriptTypeRegistry.Clear();

    private sealed class Probe { public int Serial; }

    [Fact]
    public void Create_UnknownType_ReturnsNull()
    {
        // Native code calls this with a name that came out of a component's
        // serialized config; an old/renamed class must not throw across the
        // [UnmanagedCallersOnly] boundary.
        ScriptTypeRegistry.Create("Nope.NotRegistered").Should().BeNull();
    }

    [Fact]
    public void Register_ThenCreate_UsesTheFactory()
    {
        int calls = 0;
        ScriptTypeRegistry.Register("Probe", () => { calls++; return new Probe { Serial = calls }; });

        ScriptTypeRegistry.Create("Probe").Should().BeOfType<Probe>();
        calls.Should().Be(1);
    }

    [Fact]
    public void Create_ReturnsAFreshInstanceEachTime()
    {
        ScriptTypeRegistry.Register("Probe", () => new Probe());

        var a = ScriptTypeRegistry.Create("Probe");
        var b = ScriptTypeRegistry.Create("Probe");

        a.Should().NotBeSameAs(b, "each component gets its own script instance");
    }

    [Fact]
    public void Register_SameNameTwice_LastOneWins()
    {
        // A hot-reload re-runs the generated registrations against the new
        // assembly. Re-registering must replace, not throw or duplicate.
        ScriptTypeRegistry.Register("Probe", () => new Probe { Serial = 1 });
        ScriptTypeRegistry.Register("Probe", () => new Probe { Serial = 2 });

        ((Probe)ScriptTypeRegistry.Create("Probe")!).Serial.Should().Be(2);
        ScriptTypeRegistry.Count.Should().Be(1);
    }

    [Fact]
    public void Register_RejectsNullsLoudly()
    {
        var nullName = () => ScriptTypeRegistry.Register(null!, () => new Probe());
        nullName.Should().Throw<ArgumentNullException>();

        var nullFactory = () => ScriptTypeRegistry.Register("Probe", null!);
        nullFactory.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Create_FactoryThatThrows_ReturnsNullRatherThanPropagating()
    {
        // The caller is an [UnmanagedCallersOnly] thunk - an escaping
        // exception terminates the process rather than being catchable.
        ScriptTypeRegistry.Register("Bad", () => throw new InvalidOperationException("boom"));

        ScriptTypeRegistry.Create("Bad").Should().BeNull();
    }

    [Fact]
    public void Contains_AndCount_TrackRegistrations()
    {
        ScriptTypeRegistry.Contains("Probe").Should().BeFalse();
        ScriptTypeRegistry.Register("Probe", () => new Probe());
        ScriptTypeRegistry.Contains("Probe").Should().BeTrue();
        ScriptTypeRegistry.Count.Should().Be(1);
    }

    [Fact]
    public void ClearAllHandles_DropsEveryLiveHandleAndReportsHowMany()
    {
        // Called from HotReloadSwap: every handle points at an instance in the
        // ALC about to be unloaded, so all of them must go before the swap.
        var a = ScriptComponentBridge.Register(new object());
        var b = ScriptComponentBridge.Register(new object());

        ScriptComponentBridge.ClearAll().Should().Be(2);

        ScriptComponentBridge.Resolve(a).Should().BeNull();
        ScriptComponentBridge.Resolve(b).Should().BeNull();
        ScriptComponentBridge.ClearAll().Should().Be(0);
    }
}
