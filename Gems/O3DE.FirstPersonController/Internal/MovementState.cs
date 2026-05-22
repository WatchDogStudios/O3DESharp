/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

namespace O3DE.FirstPersonController.Internal;

/// <summary>
/// Discrete states the character can be in. Transitions are pure
/// functions of the previous state + a snapshot of input + physics
/// queries; see <see cref="MovementStateTransitions.Next"/>.
/// </summary>
public enum MovementState
{
    Idle,
    Walking,
    Sprinting,
    Crouching,
    Jumping,
    Falling,
}

/// <summary>
/// Snapshot of the inputs the transition table consults. Read-only by
/// design: the transition function is a pure mapping
/// (state, inputs) -> next state.
/// </summary>
public readonly struct MovementStateInputs
{
    public bool Grounded { get; init; }
    public bool SprintHeld { get; init; }
    public bool CrouchHeld { get; init; }
    public bool JumpPressed { get; init; }
    public bool AnyMoveAxisNonzero { get; init; }
    public bool JumpHoldElapsed { get; init; }
    public bool CanStandUp { get; init; }
    public float VerticalVelocity { get; init; }
}

public static class MovementStateTransitions
{
    public static MovementState Next(MovementState current, MovementStateInputs i)
    {
        // Falling overrides everything (except Jumping, which exits to
        // Falling on its own conditions).
        if (current != MovementState.Jumping && !i.Grounded)
        {
            return MovementState.Falling;
        }

        return current switch
        {
            MovementState.Idle =>
                i.JumpPressed && i.Grounded ? MovementState.Jumping :
                i.AnyMoveAxisNonzero        ? MovementState.Walking :
                                              MovementState.Idle,

            MovementState.Walking =>
                i.JumpPressed && i.Grounded     ? MovementState.Jumping :
                i.CrouchHeld                    ? MovementState.Crouching :
                (i.SprintHeld && i.AnyMoveAxisNonzero && !i.CrouchHeld)
                                                ? MovementState.Sprinting :
                !i.AnyMoveAxisNonzero           ? MovementState.Idle :
                                                  MovementState.Walking,

            MovementState.Sprinting =>
                i.JumpPressed && i.Grounded     ? MovementState.Jumping :
                (!i.SprintHeld || i.CrouchHeld) ? MovementState.Walking :
                !i.AnyMoveAxisNonzero           ? MovementState.Idle :
                                                  MovementState.Sprinting,

            MovementState.Crouching =>
                (!i.CrouchHeld && i.CanStandUp) ? MovementState.Walking :
                                                  MovementState.Crouching,

            MovementState.Jumping =>
                (i.VerticalVelocity < 0f || i.JumpHoldElapsed) ? MovementState.Falling :
                                                                 MovementState.Jumping,

            MovementState.Falling =>
                i.Grounded
                    ? (i.AnyMoveAxisNonzero ? MovementState.Walking : MovementState.Idle)
                    : MovementState.Falling,

            _ => current,
        };
    }
}
