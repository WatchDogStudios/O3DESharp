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

public class MovementStateTransitionTests
{
    [Fact]
    public void Idle_Transitions_To_Walking_When_Move_Axis_Is_Held_And_Grounded()
    {
        var inputs = new MovementStateInputs
        {
            Grounded = true,
            AnyMoveAxisNonzero = true,
        };

        var next = MovementStateTransitions.Next(MovementState.Idle, inputs);
        next.Should().Be(MovementState.Walking);
    }

    public static TheoryData<MovementState, MovementStateInputs, MovementState> TransitionCases =>
        new()
        {
            // ---- from Idle ----
            { MovementState.Idle, new() { Grounded = true, AnyMoveAxisNonzero = true }, MovementState.Walking },
            { MovementState.Idle, new() { Grounded = false }, MovementState.Falling },
            { MovementState.Idle, new() { Grounded = true, JumpPressed = true }, MovementState.Jumping },
            { MovementState.Idle, new() { Grounded = true }, MovementState.Idle },

            // ---- from Walking ----
            { MovementState.Walking, new() { Grounded = true, SprintHeld = true, AnyMoveAxisNonzero = true }, MovementState.Sprinting },
            { MovementState.Walking, new() { Grounded = true, CrouchHeld = true }, MovementState.Crouching },
            { MovementState.Walking, new() { Grounded = false }, MovementState.Falling },
            { MovementState.Walking, new() { Grounded = true, JumpPressed = true, AnyMoveAxisNonzero = true }, MovementState.Jumping },
            { MovementState.Walking, new() { Grounded = true, AnyMoveAxisNonzero = false }, MovementState.Idle },

            // ---- from Sprinting ----
            { MovementState.Sprinting, new() { Grounded = true, SprintHeld = false, AnyMoveAxisNonzero = true }, MovementState.Walking },
            { MovementState.Sprinting, new() { Grounded = true, SprintHeld = true, CrouchHeld = true }, MovementState.Walking },
            { MovementState.Sprinting, new() { Grounded = false }, MovementState.Falling },

            // ---- from Crouching ----
            { MovementState.Crouching, new() { Grounded = true, CrouchHeld = false, CanStandUp = true }, MovementState.Walking },
            { MovementState.Crouching, new() { Grounded = true, CrouchHeld = false, CanStandUp = false }, MovementState.Crouching },
            { MovementState.Crouching, new() { Grounded = false }, MovementState.Falling },

            // ---- from Jumping ----
            { MovementState.Jumping, new() { VerticalVelocity = -1.0f }, MovementState.Falling },
            { MovementState.Jumping, new() { JumpHoldElapsed = true, VerticalVelocity = 5.0f }, MovementState.Falling },
            { MovementState.Jumping, new() { JumpHoldElapsed = false, VerticalVelocity = 5.0f }, MovementState.Jumping },

            // ---- from Falling ----
            { MovementState.Falling, new() { Grounded = true, AnyMoveAxisNonzero = false }, MovementState.Idle },
            { MovementState.Falling, new() { Grounded = true, AnyMoveAxisNonzero = true }, MovementState.Walking },
            { MovementState.Falling, new() { Grounded = false }, MovementState.Falling },
        };

    [Theory]
    [MemberData(nameof(TransitionCases))]
    public void Transition_Table_Cases(MovementState current, MovementStateInputs inputs, MovementState expected)
    {
        var next = MovementStateTransitions.Next(current, inputs);
        next.Should().Be(expected, $"transition from {current} with inputs {inputs}");
    }
}
