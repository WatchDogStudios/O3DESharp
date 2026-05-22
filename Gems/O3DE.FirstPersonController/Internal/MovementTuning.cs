/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

namespace O3DE.FirstPersonController.Internal;

/// <summary>
/// Grouped tuning parameters for the FPC component. Defaults match
/// upstream Porcupine-Factory/FirstPersonController at main HEAD on
/// 2026-05-21. Designers override individual fields via
/// [ExposedProperty]; the static <see cref="Defaults"/> instance is
/// what fresh component instances start from.
/// </summary>
public struct MovementTuning
{
    // ---- horizontal ----
    public float WalkSpeed;             // m/s
    public float Acceleration;          // m/s²
    public float Deceleration;          // damping factor per second
    public float SprintScaleForward;    // multiplier on WalkSpeed when sprinting

    // ---- vertical ----
    public float Gravity;               // m/s² (positive value, applied downward)
    public float JumpInitialVelocity;   // m/s
    public float JumpHoldDistance;      // m above ground where hold ends
    public bool DoubleJumpEnabled;

    // ---- crouch ----
    public float StandingEyeHeight;     // m (eye above feet)
    public float CrouchEyeHeight;       // m
    public float CrouchPidKp;
    public float CrouchPidKi;
    public float CrouchPidKd;

    // ---- ground check ----
    public float GroundedSphereCastOffset;  // m above origin
    public float MaxGroundedAngleDegrees;
    public float CharacterRadius;       // m

    public static MovementTuning Defaults => new()
    {
        WalkSpeed = 5.0f,
        Acceleration = 30.0f,
        Deceleration = 1.5f,
        SprintScaleForward = 1.5f,
        Gravity = 30.0f,
        JumpInitialVelocity = 6.0f,
        JumpHoldDistance = 0.8f,
        DoubleJumpEnabled = false,
        StandingEyeHeight = 1.6f,
        CrouchEyeHeight = 1.1f,
        CrouchPidKp = 12.0f,
        CrouchPidKi = 0.0f,
        CrouchPidKd = 0.5f,
        GroundedSphereCastOffset = 0.001f,
        MaxGroundedAngleDegrees = 30.0f,
        CharacterRadius = 0.4f,
    };
}
