/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using FluentAssertions;
using O3DE.FirstPersonController.Internal;
using Xunit;

namespace O3DE.FirstPersonController.Tests;

public class InputAxisAccumulatorTests
{
    [Fact]
    public void Default_Snapshot_Has_All_Axes_Zero()
    {
        var acc = new InputAxisAccumulator();
        var snap = acc.Snapshot();
        snap.Forward.Should().Be(0f);
        snap.Strafe.Should().Be(0f);
        snap.YawDelta.Should().Be(0f);
        snap.PitchDelta.Should().Be(0f);
        snap.SprintHeld.Should().BeFalse();
        snap.CrouchHeld.Should().BeFalse();
        snap.JumpPressed.Should().BeFalse();
    }

    [Fact]
    public void OnHeld_Forward_Sets_Forward_Axis_To_Plus_One()
    {
        var acc = new InputAxisAccumulator();
        acc.OnHeld("Forward", 1.0f);
        acc.Snapshot().Forward.Should().Be(1.0f);
    }

    [Fact]
    public void OnHeld_Back_Sets_Forward_Axis_To_Minus_One()
    {
        var acc = new InputAxisAccumulator();
        acc.OnHeld("Back", 1.0f);
        acc.Snapshot().Forward.Should().Be(-1.0f);
    }

    [Fact]
    public void OnHeld_Left_And_Right_Combine_Symmetrically()
    {
        var acc = new InputAxisAccumulator();
        acc.OnHeld("Left", 1.0f);
        acc.OnHeld("Right", 1.0f);
        acc.Snapshot().Strafe.Should().Be(0f, "left and right cancel");
    }

    [Fact]
    public void OnPressed_Jump_Snapshots_True_Once_Then_DrainEdges_Resets()
    {
        var acc = new InputAxisAccumulator();
        acc.OnPressed("Jump");

        acc.Snapshot().JumpPressed.Should().BeTrue("the press should latch until drained");

        acc.DrainEdges();
        acc.Snapshot().JumpPressed.Should().BeFalse("after drain, edge events must clear");
    }

    [Fact]
    public void OnReleased_Sprint_Clears_The_Held_Flag()
    {
        var acc = new InputAxisAccumulator();
        acc.OnHeld("Sprint", 1.0f);
        acc.Snapshot().SprintHeld.Should().BeTrue();

        acc.OnReleased("Sprint");
        acc.Snapshot().SprintHeld.Should().BeFalse();
    }

    [Fact]
    public void OnHeld_Yaw_Accumulates_Across_Multiple_Events_Per_Frame()
    {
        var acc = new InputAxisAccumulator();
        acc.OnHeld("Yaw", 2.5f);
        acc.OnHeld("Yaw", -1.0f);
        acc.Snapshot().YawDelta.Should().BeApproximately(1.5f, 0.0001f);
    }

    [Fact]
    public void DrainEdges_Resets_Yaw_And_Pitch_Deltas_But_Not_Axes()
    {
        var acc = new InputAxisAccumulator();
        acc.OnHeld("Forward", 1.0f);
        acc.OnHeld("Yaw", 5.0f);

        acc.DrainEdges();
        var snap = acc.Snapshot();

        snap.YawDelta.Should().Be(0f, "yaw is per-frame and drains every tick");
        snap.Forward.Should().Be(1.0f, "axes persist while the button is held");
    }

    [Fact]
    public void Unknown_Event_Names_Are_Silently_Ignored()
    {
        var acc = new InputAxisAccumulator();
        var act = () => acc.OnHeld("NotAnEvent", 1.0f);
        act.Should().NotThrow();
        acc.Snapshot().Forward.Should().Be(0f);
    }
}
