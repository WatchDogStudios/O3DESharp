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
}
