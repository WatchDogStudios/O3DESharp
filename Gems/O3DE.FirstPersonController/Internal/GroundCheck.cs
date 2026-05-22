/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using System;
using O3DE;

namespace O3DE.FirstPersonController.Internal;

/// <summary>
/// Tracks whether the character is standing on a slope it can stand
/// on, by sphere-casting downward each frame. Returns false (i.e.,
/// "falling") if the cast misses, OR if the slope exceeds
/// MaxSlopeDegrees.
/// </summary>
public sealed class GroundCheck
{
    public bool Grounded { get; private set; }
    public Vector3 SlopeNormal { get; private set; } = Vector3.Up;
    public float MaxSlopeDegrees { get; init; } = 30f;
    public float ProbeDistance { get; init; } = 0.5f;

    public void Update(
        Vector3 worldPosition,
        float characterRadius,
        IPhysicsQueryService physics,
        float dt)
    {
        bool hit = physics.SphereCast(
            origin: worldPosition,
            radius: characterRadius,
            direction: -Vector3.Up,
            maxDistance: ProbeDistance,
            out var normal,
            out var _);

        if (!hit)
        {
            Grounded = false;
            SlopeNormal = Vector3.Up;
            return;
        }

        // Angle between hit normal and world-up. If > MaxSlopeDegrees,
        // the surface is too steep to stand on.
        double cosTheta = Vector3.Dot(normal, Vector3.Up);
        cosTheta = Math.Clamp(cosTheta, -1.0, 1.0);
        double angleDeg = Math.Acos(cosTheta) * (180.0 / Math.PI);

        Grounded = angleDeg <= MaxSlopeDegrees;
        SlopeNormal = normal;
    }
}
