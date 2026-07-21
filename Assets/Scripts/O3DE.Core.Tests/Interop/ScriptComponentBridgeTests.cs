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
