/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using O3DE;

namespace O3DE.FirstPersonController.Internal;

/// <summary>
/// Abstraction over the physics scene query API. Real production
/// implementation routes to O3DE's PhysX scene query
/// (Physics::SceneQueryRequests, reflected through O3DESharp).
///
/// Unit tests substitute a fake implementation that returns
/// canned results, so the GroundCheck logic can be exercised
/// without standing up a physics engine.
/// </summary>
public interface IPhysicsQueryService
{
    /// <summary>
    /// Cast a sphere of <paramref name="radius"/> from
    /// <paramref name="origin"/> along <paramref name="direction"/>
    /// up to <paramref name="maxDistance"/>. Returns true on hit.
    /// On hit, <paramref name="hitNormal"/> is the surface normal at
    /// the contact point and <paramref name="hitDistance"/> is the
    /// distance traveled along the cast direction before contact.
    /// </summary>
    bool SphereCast(
        Vector3 origin,
        float radius,
        Vector3 direction,
        float maxDistance,
        out Vector3 hitNormal,
        out float hitDistance);
}
