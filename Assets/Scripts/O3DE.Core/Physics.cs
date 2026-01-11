/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using Coral.Managed.Interop;

namespace O3DE
{
    /// <summary>
    /// Provides access to physics-related functionality in O3DE.
    /// This class wraps the PhysX physics system queries.
    /// </summary>
    public static class Physics
    {
        /// <summary>
        /// Default maximum distance for raycasts
        /// </summary>
        public const float DefaultMaxDistance = 1000f;

        #region Raycasting

        /// <summary>
        /// Casts a ray from origin in the specified direction and returns information about what was hit.
        /// </summary>
        /// <param name="origin">The starting point of the ray in world space</param>
        /// <param name="direction">The direction of the ray (will be normalized)</param>
        /// <param name="maxDistance">The maximum distance the ray should travel</param>
        /// <returns>A RaycastHit containing information about what was hit, or a hit with Hit=false if nothing was hit</returns>
        public static RaycastHit Raycast(Vector3 origin, Vector3 direction, float maxDistance = DefaultMaxDistance)
        {
            // Normalize the direction
            direction = direction.Normalized;
            unsafe { return InternalCalls.Physics_Raycast(origin, direction, maxDistance); }
        }

        /// <summary>
        /// Casts a ray from origin in the specified direction and returns whether anything was hit.
        /// </summary>
        /// <param name="origin">The starting point of the ray in world space</param>
        /// <param name="direction">The direction of the ray (will be normalized)</param>
        /// <param name="maxDistance">The maximum distance the ray should travel</param>
        /// <returns>True if the ray hit something, false otherwise</returns>
        public static bool RaycastCheck(Vector3 origin, Vector3 direction, float maxDistance = DefaultMaxDistance)
        {
            return Raycast(origin, direction, maxDistance).Hit;
        }

        /// <summary>
        /// Casts a ray from origin in the specified direction and outputs the hit information.
        /// </summary>
        /// <param name="origin">The starting point of the ray in world space</param>
        /// <param name="direction">The direction of the ray (will be normalized)</param>
        /// <param name="hit">Output parameter containing hit information</param>
        /// <param name="maxDistance">The maximum distance the ray should travel</param>
        /// <returns>True if the ray hit something, false otherwise</returns>
        public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit, float maxDistance = DefaultMaxDistance)
        {
            hit = Raycast(origin, direction, maxDistance);
            return hit.Hit;
        }

        /// <summary>
        /// Casts a ray from one point to another and returns whether anything was hit.
        /// Useful for line-of-sight checks.
        /// </summary>
        /// <param name="from">The starting point of the ray</param>
        /// <param name="to">The end point of the ray</param>
        /// <returns>True if something is blocking the path, false if the path is clear</returns>
        public static bool Linecast(Vector3 from, Vector3 to)
        {
            Vector3 direction = to - from;
            float distance = direction.Magnitude;

            if (distance < 1e-6f)
                return false;

            return RaycastCheck(from, direction, distance);
        }

        /// <summary>
        /// Casts a ray from one point to another and outputs the hit information.
        /// </summary>
        /// <param name="from">The starting point of the ray</param>
        /// <param name="to">The end point of the ray</param>
        /// <param name="hit">Output parameter containing hit information</param>
        /// <returns>True if something was hit, false otherwise</returns>
        public static bool Linecast(Vector3 from, Vector3 to, out RaycastHit hit)
        {
            Vector3 direction = to - from;
            float distance = direction.Magnitude;

            if (distance < 1e-6f)
            {
                hit = new RaycastHit { Hit = false };
                return false;
            }

            return Raycast(from, direction, out hit, distance);
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Casts a ray downward from the specified position to find the ground.
        /// </summary>
        /// <param name="position">The position to cast from</param>
        /// <param name="maxDistance">Maximum distance to check for ground</param>
        /// <returns>The hit result, or a miss if no ground was found</returns>
        public static RaycastHit GroundCheck(Vector3 position, float maxDistance = 100f)
        {
            return Raycast(position, Vector3.Down, maxDistance);
        }

        /// <summary>
        /// Casts a ray downward from the specified position to find the ground height.
        /// </summary>
        /// <param name="position">The position to cast from</param>
        /// <param name="groundHeight">Output parameter for the ground height (Y coordinate)</param>
        /// <param name="maxDistance">Maximum distance to check for ground</param>
        /// <returns>True if ground was found, false otherwise</returns>
        public static bool GetGroundHeight(Vector3 position, out float groundHeight, float maxDistance = 100f)
        {
            RaycastHit hit = GroundCheck(position, maxDistance);
            if (hit.Hit)
            {
                groundHeight = hit.Point.Z;
                return true;
            }
            groundHeight = 0f;
            return false;
        }

        /// <summary>
        /// Checks if there is a clear line of sight between two points.
        /// </summary>
        /// <param name="from">The starting point</param>
        /// <param name="to">The target point</param>
        /// <returns>True if there is a clear line of sight, false if something is blocking</returns>
        public static bool HasLineOfSight(Vector3 from, Vector3 to)
        {
            return !Linecast(from, to);
        }

        /// <summary>
        /// Checks if there is a clear line of sight between two entities.
        /// </summary>
        /// <param name="fromEntity">The source entity</param>
        /// <param name="toEntity">The target entity</param>
        /// <returns>True if there is a clear line of sight, false if something is blocking</returns>
        public static bool HasLineOfSight(Entity fromEntity, Entity toEntity)
        {
            if (fromEntity == null || !fromEntity.IsValid || toEntity == null || !toEntity.IsValid)
                return false;

            Vector3 from = fromEntity.Transform.Position;
            Vector3 to = toEntity.Transform.Position;

            // Perform the linecast
            if (Linecast(from, to, out RaycastHit hit))
            {
                // Something was hit - check if it's the target entity
                // If we hit the target entity itself, there's line of sight
                return hit.EntityId == toEntity.Id;
            }

            // Nothing was hit, clear line of sight
            return true;
        }

        /// <summary>
        /// Casts a ray from an entity in its forward direction.
        /// </summary>
        /// <param name="entity">The entity to cast from</param>
        /// <param name="maxDistance">Maximum distance for the raycast</param>
        /// <returns>The raycast hit result</returns>
        public static RaycastHit RaycastForward(Entity entity, float maxDistance = DefaultMaxDistance)
        {
            if (entity == null || !entity.IsValid)
                return new RaycastHit { Hit = false };

            Transform transform = entity.Transform;
            return Raycast(transform.Position, transform.Forward, maxDistance);
        }

        /// <summary>
        /// Gets the distance to the nearest object in the specified direction.
        /// </summary>
        /// <param name="origin">The starting point</param>
        /// <param name="direction">The direction to check</param>
        /// <param name="maxDistance">Maximum distance to check</param>
        /// <returns>The distance to the nearest object, or maxDistance if nothing was hit</returns>
        public static float DistanceToNearest(Vector3 origin, Vector3 direction, float maxDistance = DefaultMaxDistance)
        {
            RaycastHit hit = Raycast(origin, direction, maxDistance);
            return hit.Hit ? hit.Distance : maxDistance;
        }

        #endregion
    }
}
