/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using System;
using O3DE.FirstPersonController.Internal;

namespace O3DE.FirstPersonController.SmokeTests;

/// <summary>
/// Golden-trajectory regression. Runs 1000 ticks of synthetic input
/// against the pure-logic helpers (state machine, input accumulator,
/// PID) and asserts the cumulative observed counts match recorded
/// expectations. Bypasses the ScriptComponent + reflected bus layer —
/// those need a hosted O3DE editor and aren't reachable from a plain
/// console app.
///
/// Exit code 0 = pass, non-zero = mismatch (CI fails the build).
/// </summary>
public static class Program
{
    public static int Main()
    {
        var input = new InputAxisAccumulator();
        var state = MovementState.Idle;

        int walkingTicks = 0;
        int sprintingTicks = 0;
        int jumpingTicks = 0;
        int fallingTicks = 0;
        int idleTicks = 0;
        int crouchingTicks = 0;

        // Phased synthetic input: walk for 200, sprint for 200, jump,
        // fall, settle.
        for (int t = 0; t < 1000; t++)
        {
            // Reset accumulator each tick; replay configured input for this phase.
            input = new InputAxisAccumulator();
            float vertVel = 0f;
            bool grounded = true;
            bool jumpPressed = false;

            if (t < 200)
            {
                input.OnHeld("Forward", 1.0f);
            }
            else if (t < 400)
            {
                input.OnHeld("Forward", 1.0f);
                input.OnHeld("Sprint", 1.0f);
            }
            else if (t == 400)
            {
                input.OnPressed("Jump");
                jumpPressed = true;
            }
            else if (t < 450)
            {
                grounded = false;
                vertVel = 5.0f - (t - 400) * 0.5f;
            }
            else if (t < 500)
            {
                grounded = false;
                vertVel = -2.0f;
            }
            else
            {
                grounded = true;
            }

            var snap = input.Snapshot();
            var stateInputs = new MovementStateInputs
            {
                Grounded = grounded,
                SprintHeld = snap.SprintHeld,
                CrouchHeld = snap.CrouchHeld,
                JumpPressed = jumpPressed,
                AnyMoveAxisNonzero = Math.Abs(snap.Forward) > 0.001f || Math.Abs(snap.Strafe) > 0.001f,
                JumpHoldElapsed = false,
                CanStandUp = true,
                VerticalVelocity = vertVel,
            };

            state = MovementStateTransitions.Next(state, stateInputs);

            switch (state)
            {
                case MovementState.Walking:   walkingTicks++;   break;
                case MovementState.Sprinting: sprintingTicks++; break;
                case MovementState.Jumping:   jumpingTicks++;   break;
                case MovementState.Falling:   fallingTicks++;   break;
                case MovementState.Idle:      idleTicks++;      break;
                case MovementState.Crouching: crouchingTicks++; break;
            }
        }

        // Golden reference counts (recorded at plan-write time; update
        // if the state-machine semantics intentionally change).
        Console.WriteLine($"[FPC smoke] state-tick counts: " +
            $"Walking={walkingTicks}, Sprinting={sprintingTicks}, " +
            $"Jumping={jumpingTicks}, Falling={fallingTicks}, " +
            $"Idle={idleTicks}, Crouching={crouchingTicks}");

        // Acceptance windows (±5 ticks for each phase, generous so
        // small numerical changes don't trip the gate).
        //
        // Golden values (computed from the actual state machine logic):
        //   Jumping=11: jump fires at t=400; Jumping state persists while
        //               vertVel >= 0, which is true until t=411 (vertVel
        //               = 5.0 - 11*0.5 = -0.5 first negative value).
        //   Falling=89: t=411..449 (39 ticks, vertVel < 0) +
        //               t=450..499 (50 ticks, vertVel=-2.0) = 89 ticks.
        bool ok =
            Math.Abs(walkingTicks   - 200) <= 5 &&
            Math.Abs(sprintingTicks - 200) <= 5 &&
            Math.Abs(jumpingTicks   - 11)  <= 2 &&
            Math.Abs(fallingTicks   - 89)  <= 5 &&
            Math.Abs(idleTicks      - 500) <= 5 &&
            crouchingTicks == 0;

        if (!ok)
        {
            Console.Error.WriteLine("[FPC smoke] FAIL: tick-count expectations not met. " +
                "Either the state-machine semantics changed (update the goldens) or there's " +
                "a real regression.");
            return 1;
        }

        Console.WriteLine("[FPC smoke] PASS");
        return 0;
    }
}
