//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System.Collections.Generic;
using System.Linq;
using O3DESharp.BindingGenerator.Configuration;
using O3DESharp.BindingGenerator.Generation;

namespace O3DESharp.BindingGenerator.Tests;

/// <summary>
/// The join is the one place a mistake produces a WRONG binding rather than a
/// missing one. A missing binding falls back to BehaviorMethod::Call and costs
/// speed; a wrong one calls a function pointer with a mismatched signature.
/// Every ambiguous case must therefore resolve to "not bindable".
/// </summary>
public class NativeBindingJoinTests
{
    private static NativeBindingManifestMethod Method(
        string cls, string name, bool isStatic = false, params string[] argStorageClasses)
    {
        return new NativeBindingManifestMethod
        {
            ReflectedName = name,
            OwningClassName = cls,
            OwningClassTypeId = "{00000000-0000-0000-0000-000000000001}",
            IsStatic = isStatic,
            BindingId = $"{cls}::{name}",
            Return = new NativeBindingManifestArgument { CppTypeName = "void", StorageClass = "Value" },
            Arguments = argStorageClasses
                .Select(sc => new NativeBindingManifestArgument { StorageClass = sc, CppTypeName = "int" })
                .ToList(),
        };
    }

    private static NativeBindingManifestDocument Doc(params NativeBindingManifestMethod[] methods)
        => new() { Methods = methods.ToList() };

    [Fact]
    public void MatchingCallSite_PopulatesSymbolAndBinds()
    {
        var doc = Doc(Method("Vector3", "GetLength"));
        var sites = new[] { new CallSiteSymbol("Vector3", "GetLength", "AZ::Vector3::GetLength", false) };

        var report = NativeBindingJoin.Apply(doc, sites);

        doc.Methods[0].NativeQualifiedSymbol.Should().Be("AZ::Vector3::GetLength");
        doc.Methods[0].Bindable.Should().BeTrue();
        doc.Methods[0].NonBindableReason.Should().Be("None");
        report.Bound.Should().Be(1);
    }

    [Fact]
    public void NoMatchingCallSite_IsUnresolvedNotBound()
    {
        var doc = Doc(Method("Vector3", "GetLength"));

        var report = NativeBindingJoin.Apply(doc, System.Array.Empty<CallSiteSymbol>());

        doc.Methods[0].Bindable.Should().BeFalse();
        doc.Methods[0].NonBindableReason.Should().Be("UnresolvedNativeSymbol");
        doc.Methods[0].NativeQualifiedSymbol.Should().BeEmpty();
        report.Bound.Should().Be(0);
    }

    [Fact]
    public void SameReflectedNameOnDifferentClasses_DoesNotCrossJoin()
    {
        // The join key is (className, reflectedName). If it ever degrades to
        // reflectedName alone, Transform::GetLength would bind to
        // AZ::Vector3::GetLength - a wrong pointer with a plausible name.
        var doc = Doc(Method("Vector3", "GetLength"), Method("Transform", "GetLength"));
        var sites = new[] { new CallSiteSymbol("Vector3", "GetLength", "AZ::Vector3::GetLength", false) };

        NativeBindingJoin.Apply(doc, sites);

        doc.Methods.Single(m => m.OwningClassName == "Vector3").NativeQualifiedSymbol
            .Should().Be("AZ::Vector3::GetLength");
        doc.Methods.Single(m => m.OwningClassName == "Transform").NativeQualifiedSymbol
            .Should().BeEmpty("Transform::GetLength has no call site and must not inherit Vector3's symbol");
    }

    [Fact]
    public void DuplicateCallSitesForSameKey_RefusesToBind()
    {
        // Two different &C::Method expressions reflected under one script name
        // means an overload set. Picking either is a coin flip, so bind neither.
        var doc = Doc(Method("Vector3", "Set"));
        var sites = new[]
        {
            new CallSiteSymbol("Vector3", "Set", "AZ::Vector3::Set", false),
            new CallSiteSymbol("Vector3", "Set", "AZ::Vector3::SetFloat3", false),
        };

        NativeBindingJoin.Apply(doc, sites);

        doc.Methods[0].Bindable.Should().BeFalse();
        doc.Methods[0].NonBindableReason.Should().Be("Overloaded");
    }

    [Fact]
    public void LambdaReflectedCallSite_IsNotBound()
    {
        var doc = Doc(Method("Vector3", "Weird"));
        var sites = new[] { new CallSiteSymbol("Vector3", "Weird", "", true) };

        NativeBindingJoin.Apply(doc, sites);

        doc.Methods[0].Bindable.Should().BeFalse();
        doc.Methods[0].NonBindableReason.Should().Be("ReflectedViaLambda");
    }

    [Fact]
    public void UnknownArgStorageClass_IsNotBound()
    {
        var doc = Doc(Method("Vector3", "Odd", false, "Value", "Unknown"));
        var sites = new[] { new CallSiteSymbol("Vector3", "Odd", "AZ::Vector3::Odd", false) };

        NativeBindingJoin.Apply(doc, sites);

        doc.Methods[0].Bindable.Should().BeFalse();
        doc.Methods[0].NonBindableReason.Should().Be("UnsupportedArgStorage");
    }

    [Fact]
    public void UnknownReturnStorageClass_IsNotBound()
    {
        var doc = Doc(Method("Vector3", "OddRet"));
        doc.Methods[0].Return.StorageClass = "Unknown";
        doc.Methods[0].Return.CppTypeName = "SomethingExotic";
        var sites = new[] { new CallSiteSymbol("Vector3", "OddRet", "AZ::Vector3::OddRet", false) };

        NativeBindingJoin.Apply(doc, sites);

        doc.Methods[0].Bindable.Should().BeFalse();
        doc.Methods[0].NonBindableReason.Should().Be("UnsupportedArgStorage");
    }

    [Fact]
    public void VoidReturn_IsBindable()
    {
        // "void" carries StorageClass Value but is not a real value - it must
        // not be mistaken for an unsupported storage class.
        var doc = Doc(Method("Vector3", "Reset"));
        var sites = new[] { new CallSiteSymbol("Vector3", "Reset", "AZ::Vector3::Reset", false) };

        NativeBindingJoin.Apply(doc, sites);

        doc.Methods[0].Bindable.Should().BeTrue();
    }

    [Fact]
    public void Report_CountsReasons()
    {
        var doc = Doc(
            Method("A", "Ok"),
            Method("B", "Missing"),
            Method("C", "Lambda"));
        var sites = new[]
        {
            new CallSiteSymbol("A", "Ok", "AZ::A::Ok", false),
            new CallSiteSymbol("C", "Lambda", "", true),
        };

        var report = NativeBindingJoin.Apply(doc, sites);

        report.Total.Should().Be(3);
        report.Bound.Should().Be(1);
        report.Unbound.Should().Be(2);
        report.ReasonCounts["UnresolvedNativeSymbol"].Should().Be(1);
        report.ReasonCounts["ReflectedViaLambda"].Should().Be(1);
    }
}
