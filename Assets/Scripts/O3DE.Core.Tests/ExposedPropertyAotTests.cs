//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using O3DE;

namespace O3DE.Core.Tests;

/// <summary>
/// The inspector's exposed-property walk is live reflection over user script
/// types. It stays reflective by design (Phase 7's string round-trip), but it
/// has to be ANNOTATED so the trim/AOT analyzer can see which members are
/// needed - an unannotated GetFields call is the analyzer's cue that anything
/// could be removed.
///
/// These assert the annotation exists (a future edit that drops it would
/// otherwise only show up as a warning someone ignores) and that the behaviour
/// is unchanged by it.
/// </summary>
public class ExposedPropertyAotTests
{
    private class Sample
    {
        [ExposedProperty] public float Speed = 10.0f;
        [ExposedProperty("Max Health")] public int MaxHealth = 100;
        public string NotExposed = "ignored";
    }

    private class Derived : Sample
    {
        [ExposedProperty] public bool Extra = true;
    }

    [Fact]
    public void TypeOverload_CarriesTheDynamicallyAccessedMembersAnnotation()
    {
        var method = typeof(ExposedPropertyHelpers)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(ExposedPropertyHelpers.Enumerate)
                         && m.GetParameters().Length == 2);

        var attr = method.GetParameters()[0]
            .GetCustomAttribute<DynamicallyAccessedMembersAttribute>();

        attr.Should().NotBeNull(
            "without the annotation the trim analyzer cannot tell which members the walk needs");
        attr!.MemberTypes.Should().HaveFlag(DynamicallyAccessedMemberTypes.PublicFields);
        attr.MemberTypes.Should().HaveFlag(DynamicallyAccessedMemberTypes.NonPublicFields);
        attr.MemberTypes.Should().HaveFlag(DynamicallyAccessedMemberTypes.PublicProperties);
        attr.MemberTypes.Should().HaveFlag(DynamicallyAccessedMemberTypes.NonPublicProperties);
    }

    [Fact]
    public void ObjectOverload_StillEnumeratesTheSameMembers()
    {
        var names = ExposedPropertyHelpers.Enumerate(new Sample()).Select(m => m.Name).ToList();

        names.Should().BeEquivalentTo(new[] { "Speed", "MaxHealth" });
        names.Should().NotContain("NotExposed");
    }

    [Fact]
    public void BothOverloads_AgreeOnTheSameInstance()
    {
        var instance = new Derived();

        var viaObject = ExposedPropertyHelpers.Enumerate(instance).Select(m => m.Name).ToList();
        var viaType = ExposedPropertyHelpers.Enumerate(typeof(Derived), instance)
            .Select(m => m.Name).ToList();

        viaType.Should().Equal(viaObject);
    }

    [Fact]
    public void InheritanceWalkStillReachesBaseTypeMembers()
    {
        // The base-type walk is the part that cannot be annotated (Type.BaseType
        // carries no annotations); assert it still works so the suppression is
        // covering a known-good path rather than a broken one.
        var names = ExposedPropertyHelpers.Enumerate(new Derived()).Select(m => m.Name).ToList();

        names.Should().Contain("Extra");
        names.Should().Contain("Speed");
        names.Should().Contain("MaxHealth");
    }

    [Fact]
    public void SnapshotDefaults_IsUnchanged()
    {
        var defaults = ExposedPropertyHelpers.SnapshotDefaults(new Sample());

        defaults["Speed"].Should().Be("10");
        defaults["MaxHealth"].Should().Be("100");
    }
}
