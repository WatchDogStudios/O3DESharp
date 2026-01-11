/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using O3DE;

namespace Examples
{
    /// <summary>
    /// Example PlayerController script demonstrating the C# scripting API for O3DE.
    ///
    /// This script shows how to:
    /// - Use the ScriptComponent lifecycle methods
    /// - Access and modify entity transforms
    /// - Use the Time class for frame-rate independent movement
    /// - Use the Physics class for raycasting
    /// - Use the Debug class for logging
    ///
    /// To use this script:
    /// 1. Attach a "C# Script" component to an entity in the O3DE Editor
    /// 2. Set the Script Class name to "Examples.PlayerController"
    /// 3. Run the game!
    /// </summary>
    public class PlayerController : ScriptComponent
    {
        // ============================================================
        // Configuration - These could be exposed to the editor in the future
        // ============================================================

        /// <summary>
        /// Movement speed in units per second
        /// </summary>
        private float moveSpeed = 10.0f;

        /// <summary>
        /// Rotation speed in degrees per second
        /// </summary>
        private float rotationSpeed = 90.0f;

        /// <summary>
        /// How high the player can jump
        /// </summary>
        private float jumpHeight = 5.0f;

        /// <summary>
        /// Gravity strength
        /// </summary>
        private float gravity = 20.0f;

        // ============================================================
        // Runtime State
        // ============================================================

        /// <summary>
        /// Current vertical velocity (for jumping/falling)
        /// </summary>
        private float verticalVelocity = 0.0f;

        /// <summary>
        /// Whether the player is currently on the ground
        /// </summary>
        private bool isGrounded = false;

        /// <summary>
        /// Initial position (for respawning)
        /// </summary>
        private Vector3 spawnPosition;

        /// <summary>
        /// Time when the script was created
        /// </summary>
        private float startTime;

        // ============================================================
        // Lifecycle Methods
        // ============================================================

        /// <summary>
        /// Called when the script is first activated.
        /// Use this for initialization.
        /// </summary>
        public override void OnCreate()
        {
            // Log that we've started
            Debug.Log($"PlayerController: Initialized on entity '{Name}'");

            // Store the spawn position for respawning
            spawnPosition = Transform.Position;
            startTime = Time.TotalTime;

            // Log initial position
            Debug.Log($"PlayerController: Spawn position is {spawnPosition}");
        }

        /// <summary>
        /// Called every frame.
        /// </summary>
        /// <param name="deltaTime">Time since last frame in seconds</param>
        public override void OnUpdate(float deltaTime)
        {
            // Check if we're on the ground
            CheckGrounded();

            // Handle movement input
            HandleMovement(deltaTime);

            // Handle jumping
            HandleJump(deltaTime);

            // Apply gravity
            ApplyGravity(deltaTime);

            // Check for falling off the world
            CheckFallOffWorld();

            // Debug visualization (every second)
            if (Time.FrameCount % 60 == 0)
            {
                DebugLogState();
            }
        }

        /// <summary>
        /// Called when the script is deactivated.
        /// Use this for cleanup.
        /// </summary>
        public override void OnDestroy()
        {
            float playTime = Time.TotalTime - startTime;
            Debug.Log($"PlayerController: Destroyed after {playTime:F1} seconds of play time");
        }

        /// <summary>
        /// Called when the entity's transform changes.
        /// </summary>
        public override void OnTransformChanged()
        {
            // You can react to transform changes here
            // For example, update any cached values
        }

        // ============================================================
        // Movement Logic
        // ============================================================

        /// <summary>
        /// Handles WASD/Arrow key movement
        /// </summary>
        private void HandleMovement(float deltaTime)
        {
            // Build movement vector from input
            // Note: Input system integration is TODO - this is a placeholder
            Vector3 moveDirection = Vector3.Zero;

            // In a real implementation, you would check:
            // if (Input.IsKeyDown(KeyCode.W)) moveDirection += Transform.Forward;
            // if (Input.IsKeyDown(KeyCode.S)) moveDirection -= Transform.Forward;
            // if (Input.IsKeyDown(KeyCode.A)) moveDirection -= Transform.Right;
            // if (Input.IsKeyDown(KeyCode.D)) moveDirection += Transform.Right;

            // For now, let's just rotate and move forward automatically as a demo
            // Rotate the player slowly
            Transform.Rotate(new Vector3(0, 0, rotationSpeed * deltaTime * 0.5f));

            // Move forward constantly (demo movement)
            moveDirection = Transform.Forward;

            // Normalize if we have movement
            if (moveDirection.SqrMagnitude > 0.01f)
            {
                moveDirection = moveDirection.Normalized;

                // Apply movement
                Vector3 movement = moveDirection * moveSpeed * deltaTime;
                Transform.Translate(movement);
            }
        }

        /// <summary>
        /// Handles jumping logic
        /// </summary>
        private void HandleJump(float deltaTime)
        {
            // Check for jump input (placeholder - would use Input system)
            // if (Input.IsKeyPressed(KeyCode.Space) && isGrounded)
            // {
            //     Jump();
            // }

            // Apply vertical velocity
            if (verticalVelocity != 0)
            {
                Vector3 position = Transform.Position;
                position.Z += verticalVelocity * deltaTime;
                Transform.Position = position;
            }
        }

        /// <summary>
        /// Makes the player jump
        /// </summary>
        private void Jump()
        {
            if (isGrounded)
            {
                // Calculate initial velocity needed for desired jump height
                // Using v² = 2gh, so v = sqrt(2gh)
                verticalVelocity = MathF.Sqrt(2.0f * gravity * jumpHeight);
                isGrounded = false;
                Debug.Log("PlayerController: Jump!");
            }
        }

        /// <summary>
        /// Applies gravity to the player
        /// </summary>
        private void ApplyGravity(float deltaTime)
        {
            if (!isGrounded)
            {
                verticalVelocity -= gravity * deltaTime;
            }
            else
            {
                verticalVelocity = 0;
            }
        }

        /// <summary>
        /// Checks if the player is on the ground using a raycast
        /// </summary>
        private void CheckGrounded()
        {
            Vector3 position = Transform.Position;

            // Cast a ray downward to check for ground
            RaycastHit hit = Physics.Raycast(
                position + new Vector3(0, 0, 0.1f), // Slightly above feet
                Vector3.Down,
                0.2f // Short distance
            );

            bool wasGrounded = isGrounded;
            isGrounded = hit.Hit;

            // Log landing
            if (!wasGrounded && isGrounded)
            {
                Debug.Log("PlayerController: Landed!");
            }

            // Snap to ground if close
            if (isGrounded && hit.Distance < 0.15f)
            {
                Vector3 newPos = position;
                newPos.Z = hit.Point.Z;
                Transform.Position = newPos;
            }
        }

        /// <summary>
        /// Checks if the player has fallen off the world and respawns them
        /// </summary>
        private void CheckFallOffWorld()
        {
            const float fallThreshold = -50.0f;

            if (Transform.Position.Z < fallThreshold)
            {
                Debug.LogWarning("PlayerController: Fell off the world! Respawning...");
                Respawn();
            }
        }

        /// <summary>
        /// Respawns the player at their starting position
        /// </summary>
        public void Respawn()
        {
            Transform.Position = spawnPosition;
            Transform.Rotation = Quaternion.Identity;
            verticalVelocity = 0;
            isGrounded = false;

            Debug.Log($"PlayerController: Respawned at {spawnPosition}");
        }

        // ============================================================
        // Debug Helpers
        // ============================================================

        /// <summary>
        /// Logs the current state for debugging
        /// </summary>
        private void DebugLogState()
        {
            Debug.LogDebug($"PlayerController State:");
            Debug.LogDebug($"  Position: {Transform.Position}");
            Debug.LogDebug($"  Rotation: {Transform.EulerAngles}");
            Debug.LogDebug($"  Grounded: {isGrounded}");
            Debug.LogDebug($"  Vertical Velocity: {verticalVelocity:F2}");
            Debug.LogDebug($"  FPS: {Time.FPS:F0}");
        }

        // ============================================================
        // Public API for other scripts
        // ============================================================

        /// <summary>
        /// Gets the current movement speed
        /// </summary>
        public float GetMoveSpeed() => moveSpeed;

        /// <summary>
        /// Sets the movement speed
        /// </summary>
        public void SetMoveSpeed(float speed)
        {
            moveSpeed = MathF.Max(0, speed);
            Debug.Log($"PlayerController: Move speed set to {moveSpeed}");
        }

        /// <summary>
        /// Checks if the player is currently grounded
        /// </summary>
        public bool IsGrounded() => isGrounded;

        /// <summary>
        /// Teleports the player to a specific position
        /// </summary>
        public void TeleportTo(Vector3 position)
        {
            Transform.Position = position;
            verticalVelocity = 0;
            Debug.Log($"PlayerController: Teleported to {position}");
        }

        /// <summary>
        /// Makes the player look at a target position
        /// </summary>
        public void LookAt(Vector3 target)
        {
            Transform.LookAt(target);
        }
    }
}
