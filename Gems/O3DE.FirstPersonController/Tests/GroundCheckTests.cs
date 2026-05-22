/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using FluentAssertions;
using O3DE;
using O3DE.FirstPersonController.Internal;
using Xunit;

namespace O3DE.FirstPersonController.Tests;

public class GroundCheckTests
{
    /// <summary>
    /// Fake physics service for tests. Returns whatever the test
    /// configured; never calls into a real scene query.
    /// </summary>
    private sealed class FakePhysics : IPhysicsQueryService
    {
        public bool ShouldHit { get; set; }
        public Vector3 HitNormal { get; set; } = Vector3.Up;
        public float HitDistance { get; set; }

        public bool SphereCast(Vector3 origin, float radius, Vector3 direction,
                               float maxDistance, out Vector3 hitNormal, out float hitDistance)
        {
            hitNormal = HitNormal;
            hitDistance = HitDistance;
            return ShouldHit;
        }
    }

    [Fact]
    public void Grounded_When_SphereCast_Hits_With_Vertical_Normal()
    {
        var physics = new FakePhysics
        {
            ShouldHit = true,
            HitNormal = Vector3.Up,
            HitDistance = 0.1f,
        };

        var check = new GroundCheck { MaxSlopeDegrees = 30f };
        check.Update(
            worldPosition: new Vector3(0, 0, 1),
            characterRadius: 0.4f,
            physics: physics,
            dt: 0.016f);

        check.Grounded.Should().BeTrue();
    }

    [Fact]
    public void Falling_When_SphereCast_Misses()
    {
        var physics = new FakePhysics { ShouldHit = false };

        var check = new GroundCheck { MaxSlopeDegrees = 30f };
        check.Update(new Vector3(0, 0, 5), 0.4f, physics, 0.016f);

        check.Grounded.Should().BeFalse();
    }

    [Fact]
    public void Falling_When_Slope_Exceeds_Max()
    {
        // 45-degree slope - normal points outward at 45deg from vertical.
        // Should fail a 30-degree max.
        var slopedNormal = new Vector3(
            (float)System.Math.Sin(System.Math.PI / 4),
            0f,
            (float)System.Math.Cos(System.Math.PI / 4)
        );
        var physics = new FakePhysics
        {
            ShouldHit = true,
            HitNormal = slopedNormal,
            HitDistance = 0.1f,
        };

        var check = new GroundCheck { MaxSlopeDegrees = 30f };
        check.Update(new Vector3(0, 0, 1), 0.4f, physics, 0.016f);

        check.Grounded.Should().BeFalse("45-degree slope exceeds 30-degree max");
    }

    [Fact]
    public void Grounded_When_Slope_Within_Max()
    {
        // 20-degree slope, max 30 - should pass.
        double angleRad = 20.0 * System.Math.PI / 180.0;
        var normal = new Vector3(
            (float)System.Math.Sin(angleRad),
            0f,
            (float)System.Math.Cos(angleRad)
        );
        var physics = new FakePhysics
        {
            ShouldHit = true,
            HitNormal = normal,
            HitDistance = 0.1f,
        };

        var check = new GroundCheck { MaxSlopeDegrees = 30f };
        check.Update(new Vector3(0, 0, 1), 0.4f, physics, 0.016f);

        check.Grounded.Should().BeTrue();
    }
}
