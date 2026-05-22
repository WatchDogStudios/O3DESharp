/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

namespace O3DE.FirstPersonController.Internal;

/// <summary>
/// Snapshot of one frame's worth of accumulated input. Returned by
/// <see cref="InputAxisAccumulator.Snapshot"/>. Read-only by design.
/// </summary>
public readonly struct InputSnapshot
{
    public float Forward { get; init; }      // -1..+1 (Back..Forward)
    public float Strafe { get; init; }       // -1..+1 (Left..Right)
    public float YawDelta { get; init; }     // per-frame yaw delta in degrees
    public float PitchDelta { get; init; }   // per-frame pitch delta in degrees
    public bool SprintHeld { get; init; }
    public bool CrouchHeld { get; init; }
    public bool JumpPressed { get; init; }   // edge-triggered (one frame only)
}

/// <summary>
/// Accumulates named-event input across a frame. The FPC component's
/// [EBus] handlers call OnHeld / OnPressed / OnReleased as events
/// arrive; Snapshot + DrainEdges are called once per frame in OnUpdate.
///
/// Recognised event names (case-sensitive):
///   "Forward", "Back"       - drive the forward axis (+/-1)
///   "Left", "Right"         - drive the strafe axis (+/-1)
///   "Yaw"                   - accumulating mouse/stick yaw delta
///   "Pitch"                 - accumulating mouse/stick pitch delta
///   "Sprint"                - hold-style button
///   "Crouch"                - hold-style button
///   "Jump"                  - edge-triggered (OnPressed)
///
/// Unknown names are silently ignored so users can extend their
/// .inputbindings asset without breaking this component.
/// </summary>
public sealed class InputAxisAccumulator
{
    private float m_forwardPositive;
    private float m_forwardNegative;
    private float m_strafePositive;
    private float m_strafeNegative;
    private float m_yawDelta;
    private float m_pitchDelta;
    private bool m_sprintHeld;
    private bool m_crouchHeld;
    private bool m_jumpPressed;

    public InputSnapshot Snapshot() => new()
    {
        Forward = m_forwardPositive - m_forwardNegative,
        Strafe = m_strafePositive - m_strafeNegative,
        YawDelta = m_yawDelta,
        PitchDelta = m_pitchDelta,
        SprintHeld = m_sprintHeld,
        CrouchHeld = m_crouchHeld,
        JumpPressed = m_jumpPressed,
    };

    /// <summary>
    /// Reset edge-triggered values + per-frame deltas. Call once at
    /// the end of every frame in OnUpdate. Held-axis values persist
    /// (cleared by OnReleased only).
    /// </summary>
    public void DrainEdges()
    {
        m_jumpPressed = false;
        m_yawDelta = 0f;
        m_pitchDelta = 0f;
    }

    public void OnHeld(string eventName, float value)
    {
        switch (eventName)
        {
            case "Forward": m_forwardPositive = value; break;
            case "Back": m_forwardNegative = value; break;
            case "Left": m_strafeNegative = value; break;
            case "Right": m_strafePositive = value; break;
            case "Yaw": m_yawDelta += value; break;
            case "Pitch": m_pitchDelta += value; break;
            case "Sprint": m_sprintHeld = true; break;
            case "Crouch": m_crouchHeld = true; break;
            // Unknown names ignored.
        }
    }

    public void OnPressed(string eventName)
    {
        switch (eventName)
        {
            case "Jump": m_jumpPressed = true; break;
            case "Sprint": m_sprintHeld = true; break;
            case "Crouch": m_crouchHeld = true; break;
        }
    }

    public void OnReleased(string eventName)
    {
        switch (eventName)
        {
            case "Forward": m_forwardPositive = 0f; break;
            case "Back": m_forwardNegative = 0f; break;
            case "Left": m_strafeNegative = 0f; break;
            case "Right": m_strafePositive = 0f; break;
            case "Sprint": m_sprintHeld = false; break;
            case "Crouch": m_crouchHeld = false; break;
            // Jump's release is a no-op (already edge-triggered).
        }
    }
}
