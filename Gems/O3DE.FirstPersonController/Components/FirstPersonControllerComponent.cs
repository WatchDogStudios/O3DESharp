/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using System;
using O3DE;
using O3DE.FirstPersonController.Internal;

namespace O3DE.FirstPersonController.Components;

/// <summary>
/// C# port of Porcupine-Factory/FirstPersonController's
/// FirstPersonControllerComponent. M1 implements walking, sprinting,
/// jumping, falling, grounded detection, and PhysX-backed movement.
/// M2 adds crouch + the full ~90-method request bus surface.
///
/// Setup:
///   1. Attach a CSharpScript component to the player entity
///   2. Set Script Class to
///      "O3DE.FirstPersonController.Components.FirstPersonControllerComponent"
///   3. The entity must also have a PhysX Character Controller
///      component (the FPC reads ground state + drives velocity
///      through CharacterControllerRequestBus)
/// </summary>
[EBus("InputEventNotificationBus")]
public sealed partial class FirstPersonControllerComponent : ScriptComponent
{
    // ---- Inspector-tunable parameters (defaults match upstream FPC) ----

    [ExposedProperty("Walk Speed", Tooltip = "Top horizontal speed in m/s when not sprinting")]
    public float WalkSpeed = MovementTuning.Defaults.WalkSpeed;

    [ExposedProperty("Acceleration", Tooltip = "Horizontal accel toward target speed (m/s²)")]
    public float Acceleration = MovementTuning.Defaults.Acceleration;

    [ExposedProperty("Deceleration", Tooltip = "Horizontal damping factor per second")]
    public float Deceleration = MovementTuning.Defaults.Deceleration;

    [ExposedProperty("Sprint Multiplier", Tooltip = "Speed multiplier on WalkSpeed when sprinting forward")]
    public float SprintScaleForward = MovementTuning.Defaults.SprintScaleForward;

    [ExposedProperty("Gravity", Tooltip = "Downward acceleration in m/s² (positive)")]
    public float Gravity = MovementTuning.Defaults.Gravity;

    [ExposedProperty("Jump Initial Velocity", Tooltip = "Upward velocity applied on jump press (m/s)")]
    public float JumpInitialVelocity = MovementTuning.Defaults.JumpInitialVelocity;

    [ExposedProperty("Jump Hold Distance", Tooltip = "Vertical distance above ground where jump-hold ends (m)")]
    public float JumpHoldDistance = MovementTuning.Defaults.JumpHoldDistance;

    [ExposedProperty("Max Grounded Angle (deg)", Tooltip = "Slope angle above which the character cannot stand")]
    public float MaxGroundedAngleDegrees = MovementTuning.Defaults.MaxGroundedAngleDegrees;

    [ExposedProperty("Character Radius (m)", Tooltip = "Used by the sphere-cast ground check")]
    public float CharacterRadius = MovementTuning.Defaults.CharacterRadius;

    [ExposedProperty("Eye Height (m)", Tooltip = "Standing eye height above feet; used by camera child component")]
    public float StandingEyeHeight = MovementTuning.Defaults.StandingEyeHeight;

    // ---- Internal state ----

    private readonly InputAxisAccumulator m_input = new();
    private GroundCheck m_groundCheck = null!;
    private MovementState m_state = MovementState.Idle;
    private Vector3 m_horizontalVelocity;
    private float m_verticalVelocity;
    private float m_jumpHoldStartZ;
    private bool m_disabled;

    public override void OnCreate()
    {
        Log("FirstPersonControllerComponent: activated");

        if (Transform == null || !Transform.IsValid)
        {
            LogError("[FPC] FirstPersonControllerComponent: Transform binding is null/invalid. Disabling.");
            m_disabled = true;
            return;
        }

        m_groundCheck = new GroundCheck
        {
            MaxSlopeDegrees = MaxGroundedAngleDegrees,
        };

        // Subscribe to named input events. The source generator emits
        // ConnectToInputEventNotificationBus from the [EBus] attribute
        // above the class; calling it here registers all the
        // [EBusHandler] methods below.
        try
        {
            ConnectToInputEventNotificationBus();
        }
        catch (Exception ex)
        {
            LogWarning($"[FPC] Could not connect to InputEventNotificationBus " +
                       $"({ex.GetType().Name}: {ex.Message}). Input will not be received " +
                       $"until the StartingPointInput gem is enabled in this project.");
        }
    }

    public override void OnUpdate(float deltaTime)
    {
        if (m_disabled) return;

        try
        {
            TickOnce(deltaTime);
        }
        catch (Exception ex)
        {
            LogError($"[FPC] OnUpdate threw {ex.GetType().Name}: {ex.Message} — frame skipped");
        }
    }

    public override void OnDestroy()
    {
        try
        {
            DisconnectFromInputEventNotificationBus();
        }
        catch
        {
            // Disconnect-on-shutdown errors aren't actionable; swallow.
        }
        Log($"FirstPersonControllerComponent: deactivated in state {m_state}");
    }

    private void TickOnce(float dt)
    {
        // 1. Snapshot input
        var snap = m_input.Snapshot();

        // 2. Query physics for ground state
        bool grounded = QueryCharacterControllerGrounded();

        // 3. Compute jump-hold elapsed: above start Z + JumpHoldDistance
        bool jumpHoldElapsed = m_state == MovementState.Jumping
            && Transform.Position.Z - m_jumpHoldStartZ >= JumpHoldDistance;

        // 4. Pure-functional transition decision
        var stateInputs = new MovementStateInputs
        {
            Grounded = grounded,
            SprintHeld = snap.SprintHeld,
            CrouchHeld = snap.CrouchHeld,
            JumpPressed = snap.JumpPressed,
            AnyMoveAxisNonzero = Math.Abs(snap.Forward) > 0.001f || Math.Abs(snap.Strafe) > 0.001f,
            JumpHoldElapsed = jumpHoldElapsed,
            CanStandUp = true,                    // M1: no overhead check yet
            VerticalVelocity = m_verticalVelocity,
        };

        var prev = m_state;
        m_state = MovementStateTransitions.Next(m_state, stateInputs);
        if (m_state != prev) OnStateEnter(m_state, prev);

        // 5. Horizontal velocity: target = direction * speed, accel toward target.
        float targetSpeed = WalkSpeed * (m_state == MovementState.Sprinting ? SprintScaleForward : 1.0f);
        Vector3 forwardWorld = Transform.Forward;
        Vector3 rightWorld = Transform.Right;
        Vector3 targetHoriz = forwardWorld * snap.Forward + rightWorld * snap.Strafe;
        if (targetHoriz.SqrMagnitude > 0.0001f)
        {
            targetHoriz.Normalize();
            targetHoriz = targetHoriz * targetSpeed;
        }
        // Lerp the current horizontal velocity toward target.
        float accelStep = Acceleration * dt;
        Vector3 toTarget = targetHoriz - m_horizontalVelocity;
        if (toTarget.Magnitude > accelStep)
        {
            toTarget.Normalize();
            m_horizontalVelocity = m_horizontalVelocity + toTarget * accelStep;
        }
        else
        {
            m_horizontalVelocity = targetHoriz;
        }

        // 6. Vertical velocity: gravity or jump impulse already applied via OnStateEnter.
        if (grounded && m_state != MovementState.Jumping)
        {
            m_verticalVelocity = 0f;
        }
        else
        {
            m_verticalVelocity -= Gravity * dt;
        }

        // 7. Apply the final velocity to the character controller.
        var velocity = new Vector3(
            m_horizontalVelocity.X,
            m_horizontalVelocity.Y,
            m_verticalVelocity);
        ApplyCharacterControllerVelocity(velocity);

        // 8. Drain edges so the next frame starts fresh
        m_input.DrainEdges();
    }

    private void OnStateEnter(MovementState entered, MovementState from)
    {
        switch (entered)
        {
            case MovementState.Jumping:
                m_verticalVelocity = JumpInitialVelocity;
                m_jumpHoldStartZ = Transform.Position.Z;
                Log($"[FPC] {from} -> Jumping (v0={JumpInitialVelocity:F2})");
                break;
        }
    }

    /// <summary>
    /// Query the PhysX character controller for ground state.
    /// Returns false on any bus error so a missing PhysX component
    /// degrades to "always falling" rather than crashing.
    /// </summary>
    private bool QueryCharacterControllerGrounded()
    {
        try
        {
            object? result = O3DE.Reflection.NativeReflection.SendEBusEvent(
                "CharacterControllerRequestBus", "IsOnGround", EntityId);
            return result is bool b && b;
        }
        catch (Exception ex)
        {
            LogWarning($"[FPC] IsOnGround query failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Apply a velocity to the PhysX character controller for this frame.
    /// </summary>
    private void ApplyCharacterControllerVelocity(Vector3 velocity)
    {
        try
        {
            O3DE.Reflection.NativeReflection.SendEBusEvent(
                "CharacterControllerRequestBus", "AddVelocityForTick", EntityId, velocity);
        }
        catch (Exception ex)
        {
            LogWarning($"[FPC] AddVelocity failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- Named-event handlers ---------------------------------------
    //
    // Each method maps to a named input event. The source generator
    // routes named events to these by string match.

    [EBusHandler("Forward")]    private void OnForward(float v)  => m_input.OnHeld("Forward", v);
    [EBusHandler("Back")]       private void OnBack(float v)     => m_input.OnHeld("Back", v);
    [EBusHandler("Left")]       private void OnLeft(float v)     => m_input.OnHeld("Left", v);
    [EBusHandler("Right")]      private void OnRight(float v)    => m_input.OnHeld("Right", v);
    [EBusHandler("Yaw")]        private void OnYaw(float v)      => m_input.OnHeld("Yaw", v);
    [EBusHandler("Pitch")]      private void OnPitch(float v)    => m_input.OnHeld("Pitch", v);
    [EBusHandler("Sprint")]     private void OnSprint(float v)
    {
        if (v > 0.5f) m_input.OnHeld("Sprint", v);
        else m_input.OnReleased("Sprint");
    }
    [EBusHandler("Crouch")]     private void OnCrouch(float v)
    {
        if (v > 0.5f) m_input.OnHeld("Crouch", v);
        else m_input.OnReleased("Crouch");
    }
    [EBusHandler("Jump")]       private void OnJump(float v)
    {
        if (v > 0.5f) m_input.OnPressed("Jump");
    }
}
