//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System.Collections.Generic;
using O3DE;

namespace O3DE.Core.Tests.Math;

/// <summary>
/// Tests for the Equals/GetHashCode/operator== contract on Vector2, Vector3,
/// and Quaternion. Prior to this fix, Equals used an epsilon tolerance while
/// GetHashCode hashed exact float bits, which violates the .NET contract
/// that "if two objects compare as equal, they must have the same hash
/// code" and silently broke Dictionary/HashSet lookups for near-equal keys.
/// The fix drops epsilon tolerance from Equals entirely (exact float
/// equality, matching System.Numerics.Vector3's convention). These tests
/// pin that behavior so a future regression back to epsilon-tolerant
/// Equals is caught immediately.
/// </summary>
public class EqualityTests
{
    // 1e-6f, not 1e-7f: at the magnitudes used in this file (~0.1-4.5),
    // adding 1e-7f rounds away to zero in float32 (the delta is smaller
    // than one ULP at these magnitudes), which would make the
    // "near-equal" cases below silently degenerate into "exactly-equal"
    // cases and defeat the point of the test. Verified bit-distinct at
    // every magnitude used in this file - re-verify with a ULP check
    // if you add a new literal, especially a much larger or smaller one.
    private const float NearEqualDelta = 1e-6f;

    // ---- Vector3 -------------------------------------------------------

    [Fact]
    public void Vector3_ExactlyEqualInstances_AreEqual_AndHaveEqualHashCodes()
    {
        var a = new Vector3(1.5f, -2.25f, 3.0f);
        var b = new Vector3(1.5f, -2.25f, 3.0f);

        a.Equals(b).Should().BeTrue();
        (a == b).Should().BeTrue();
        (a != b).Should().BeFalse();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Vector3_NearEqualButNotExact_AreNotEqual()
    {
        var a = new Vector3(1.0f, 2.0f, 3.0f);
        var b = new Vector3(1.0f + NearEqualDelta, 2.0f, 3.0f);

        a.Equals(b).Should().BeFalse();
        (a == b).Should().BeFalse();
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void Vector3_EqualInstances_UseableAsDictionaryKey()
    {
        var dict = new Dictionary<Vector3, string>
        {
            [new Vector3(1f, 2f, 3f)] = "hello"
        };

        dict[new Vector3(1f, 2f, 3f)].Should().Be("hello");
        dict.ContainsKey(new Vector3(1f, 2f, 3f)).Should().BeTrue();
    }

    // ---- Vector2 -------------------------------------------------------

    [Fact]
    public void Vector2_ExactlyEqualInstances_AreEqual_AndHaveEqualHashCodes()
    {
        var a = new Vector2(4.5f, -1.25f);
        var b = new Vector2(4.5f, -1.25f);

        a.Equals(b).Should().BeTrue();
        (a == b).Should().BeTrue();
        (a != b).Should().BeFalse();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Vector2_NearEqualButNotExact_AreNotEqual()
    {
        var a = new Vector2(1.0f, 2.0f);
        var b = new Vector2(1.0f, 2.0f + NearEqualDelta);

        a.Equals(b).Should().BeFalse();
        (a == b).Should().BeFalse();
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void Vector2_EqualInstances_UseableAsDictionaryKey()
    {
        var dict = new Dictionary<Vector2, string>
        {
            [new Vector2(1f, 2f)] = "world"
        };

        dict[new Vector2(1f, 2f)].Should().Be("world");
        dict.ContainsKey(new Vector2(1f, 2f)).Should().BeTrue();
    }

    // ---- Quaternion ------------------------------------------------------

    [Fact]
    public void Quaternion_ExactlyEqualInstances_AreEqual_AndHaveEqualHashCodes()
    {
        var a = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f);
        var b = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f);

        a.Equals(b).Should().BeTrue();
        (a == b).Should().BeTrue();
        (a != b).Should().BeFalse();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Quaternion_NearEqualButNotExact_AreNotEqual()
    {
        var a = new Quaternion(0f, 0f, 0f, 1.0f);
        var b = new Quaternion(0f, 0f, 0f, 1.0f + NearEqualDelta);

        a.Equals(b).Should().BeFalse();
        (a == b).Should().BeFalse();
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void Quaternion_EqualInstances_UseableAsDictionaryKey()
    {
        var dict = new Dictionary<Quaternion, string>
        {
            [Quaternion.Identity] = "identity"
        };

        dict[Quaternion.Identity].Should().Be("identity");
        dict.ContainsKey(new Quaternion(0f, 0f, 0f, 1f)).Should().BeTrue();
    }
}
