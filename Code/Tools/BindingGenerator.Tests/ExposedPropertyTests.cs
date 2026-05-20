//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System.Collections.Generic;
using System.Linq;
using O3DE;

namespace O3DESharp.BindingGenerator.Tests;

/// <summary>
/// Tests for the Phase 7 [ExposedProperty] feature. These exercise the pure-
/// reflection helpers (no Coral runtime needed) - field discovery, default
/// snapshotting, value parsing, and the JSON ingress path the C++
/// CSharpScriptComponent uses to hand editor-configured values to the
/// managed instance on Activate.
/// </summary>
public class ExposedPropertyTests
{
    // ---- Test fixtures -----------------------------------------------

    private class SimpleScript
    {
        [ExposedProperty]
        public float Speed = 1.0f;

        [ExposedProperty("Maximum Health")]
        public int MaxHealth = 50;

        [ExposedProperty]
        public bool CanJump = true;

        [ExposedProperty]
        public string Tag = "default";

        // Not exposed - should never appear in Enumerate output.
        public float ShouldBeIgnored = 99f;

        // Auto-property with attribute.
        [ExposedProperty]
        public double Multiplier { get; set; } = 1.5;
    }

    private class DerivedScript : SimpleScript
    {
        [ExposedProperty]
        public uint Lives = 3;
    }

    // ---- Enumerate ---------------------------------------------------

    [Fact]
    public void Enumerate_OnlyReturnsAttributedMembers()
    {
        var s = new SimpleScript();
        var names = ExposedPropertyHelpers.Enumerate(s).Select(m => m.Name).ToHashSet();

        names.Should().BeEquivalentTo(new[] { "Speed", "MaxHealth", "CanJump", "Tag", "Multiplier" });
        names.Should().NotContain("ShouldBeIgnored");
    }

    [Fact]
    public void Enumerate_WalksBaseClass()
    {
        var d = new DerivedScript();
        var names = ExposedPropertyHelpers.Enumerate(d).Select(m => m.Name).ToHashSet();

        names.Should().Contain("Lives", "derived-class exposed field");
        names.Should().Contain("Speed", "inherited exposed field");
        names.Should().Contain("Multiplier", "inherited exposed property");
    }

    [Fact]
    public void Enumerate_UsesDisplayNameOverride_WhenSupplied()
    {
        var s = new SimpleScript();
        var maxHealth = ExposedPropertyHelpers.Enumerate(s).First(m => m.Name == "MaxHealth");
        maxHealth.DisplayName.Should().Be("Maximum Health");

        var speed = ExposedPropertyHelpers.Enumerate(s).First(m => m.Name == "Speed");
        speed.DisplayName.Should().Be("Speed", "no override -> falls back to member name");
    }

    // ---- SnapshotDefaults --------------------------------------------

    [Fact]
    public void SnapshotDefaults_SerializesPrimitivesWithInvariantCulture()
    {
        var s = new SimpleScript { Speed = 3.14f, Multiplier = 2.5 };
        var defaults = ExposedPropertyHelpers.SnapshotDefaults(s);

        defaults["Speed"].Should().Be("3.14");
        defaults["MaxHealth"].Should().Be("50");
        defaults["CanJump"].Should().Be("true");
        defaults["Tag"].Should().Be("default");
        defaults["Multiplier"].Should().Be("2.5");
    }

    // ---- Apply -------------------------------------------------------

    [Fact]
    public void Apply_MutatesMatchingMembers()
    {
        var s = new SimpleScript();
        var values = new Dictionary<string, string>
        {
            ["Speed"] = "10.5",
            ["MaxHealth"] = "200",
            ["CanJump"] = "false",
            ["Tag"] = "hero",
            ["Multiplier"] = "3.25",
        };

        var failures = ExposedPropertyHelpers.Apply(s, values, _ => { });

        failures.Should().Be(0);
        s.Speed.Should().Be(10.5f);
        s.MaxHealth.Should().Be(200);
        s.CanJump.Should().BeFalse();
        s.Tag.Should().Be("hero");
        s.Multiplier.Should().Be(3.25);
    }

    [Fact]
    public void Apply_IgnoresUnknownKeys_AndReportsParseFailuresViaCallback()
    {
        var s = new SimpleScript();
        var values = new Dictionary<string, string>
        {
            ["Speed"] = "2.0",
            ["NoSuchField"] = "ignore me",         // not on type - silently skipped
            ["MaxHealth"] = "not-a-number",        // bad parse -> reported
        };
        var errors = new List<string>();

        var failures = ExposedPropertyHelpers.Apply(s, values, errors.Add);

        s.Speed.Should().Be(2.0f, "valid entries still apply when others fail");
        s.MaxHealth.Should().Be(50, "default kept when parse fails");
        failures.Should().Be(1);
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("MaxHealth");
        errors[0].Should().Contain("not-a-number");
    }

    [Fact]
    public void Apply_NullOrEmptyValues_IsNoOp()
    {
        var s = new SimpleScript();
        ExposedPropertyHelpers.Apply(s, null).Should().Be(0);
        ExposedPropertyHelpers.Apply(s, new Dictionary<string, string>()).Should().Be(0);

        // Defaults preserved.
        s.Speed.Should().Be(1.0f);
        s.MaxHealth.Should().Be(50);
    }

    // ---- ApplyFromJson (the C++ ingress path) ------------------------

    [Fact]
    public void ApplyFromJson_DispatchesToApply()
    {
        var s = new SimpleScript();
        const string json = "{\"Speed\":\"5.5\",\"MaxHealth\":\"123\",\"CanJump\":\"false\",\"Tag\":\"foo\"}";
        ExposedPropertyHelpers.ApplyFromJson(s, json, _ => { });

        s.Speed.Should().Be(5.5f);
        s.MaxHealth.Should().Be(123);
        s.CanJump.Should().BeFalse();
        s.Tag.Should().Be("foo");
    }

    [Fact]
    public void ApplyFromJson_HandlesEmptyObject()
    {
        var s = new SimpleScript();
        ExposedPropertyHelpers.ApplyFromJson(s, "{}", _ => { });
        s.Speed.Should().Be(1.0f, "no entries -> defaults retained");
    }

    [Fact]
    public void ApplyFromJson_EscapesInValueAreUnescapedOnIngress()
    {
        var s = new SimpleScript();
        // \n inside a string literal should arrive as a newline character on
        // the managed side. This matches what the C++ ::PushExposedPropertiesToScript
        // encoder produces for a value containing a newline.
        const string json = "{\"Tag\":\"hello\\nworld\"}";
        ExposedPropertyHelpers.ApplyFromJson(s, json, _ => { });
        s.Tag.Should().Be("hello\nworld");
    }

    [Fact]
    public void ApplyFromJson_MalformedJsonReportsViaCallback()
    {
        var s = new SimpleScript();
        var errors = new List<string>();
        ExposedPropertyHelpers.ApplyFromJson(s, "{\"unterminated", errors.Add);
        errors.Should().NotBeEmpty();
        s.Speed.Should().Be(1.0f, "no entries applied on parse failure");
    }

    // ---- Round-trip with ParseSimpleStringMap directly ---------------

    [Fact]
    public void ParseSimpleStringMap_ProducesExpectedDictionary()
    {
        const string json = "{\"a\":\"1\", \"b\" : \"two\\nlines\"  ,  \"c\":\"\"}";
        var dict = ExposedPropertyHelpers.ParseSimpleStringMap(json);
        dict.Should().HaveCount(3);
        dict["a"].Should().Be("1");
        dict["b"].Should().Be("two\nlines");
        dict["c"].Should().Be("");
    }

    // ---- GetSchema / GetSchemaJson (Phase 7.5 typed-widget foundation) -----

    [Fact]
    public void GetSchema_TagsKnownPrimitiveTypes()
    {
        var s = new SimpleScript();
        var schema = ExposedPropertyHelpers.GetSchema(s);
        var byName = schema.ToDictionary(x => x.Name, x => x);

        byName["Speed"].TypeTag.Should().Be("float");
        byName["MaxHealth"].TypeTag.Should().Be("int");
        byName["CanJump"].TypeTag.Should().Be("bool");
        byName["Tag"].TypeTag.Should().Be("string");
        byName["Multiplier"].TypeTag.Should().Be("double");
    }

    [Fact]
    public void GetSchema_DefaultsReflectCurrentInstanceValues()
    {
        var s = new SimpleScript { Speed = 42.5f, MaxHealth = 7, Tag = "boss" };
        var byName = ExposedPropertyHelpers.GetSchema(s).ToDictionary(x => x.Name, x => x);

        byName["Speed"].DefaultValue.Should().Be("42.5");
        byName["MaxHealth"].DefaultValue.Should().Be("7");
        byName["Tag"].DefaultValue.Should().Be("boss");
    }

    [Fact]
    public void GetSchema_UnknownTypeTagsAsOther()
    {
        // A class with an unsupported field type (Guid). The schema entry
        // should still be present so the editor can show a placeholder.
        var instance = new UnsupportedTypeScript();
        var schema = ExposedPropertyHelpers.GetSchema(instance);
        var entry = schema.Single();
        entry.Name.Should().Be("Id");
        entry.TypeTag.Should().Be("other");
    }

    [Fact]
    public void GetSchemaJson_RoundTripsThroughTheSameParserCppUses()
    {
        var s = new SimpleScript();
        var json = ExposedPropertyHelpers.GetSchemaJson(s);

        // Sanity: starts with [ and ends with ].
        json.Should().StartWith("[").And.EndWith("]");

        // Every supported member appears.
        json.Should().Contain("\"name\":\"Speed\"");
        json.Should().Contain("\"name\":\"MaxHealth\"");
        json.Should().Contain("\"displayName\":\"Maximum Health\"");
        json.Should().Contain("\"type\":\"float\"");
        json.Should().Contain("\"type\":\"int\"");
        json.Should().Contain("\"type\":\"bool\"");
        json.Should().Contain("\"type\":\"string\"");
        json.Should().Contain("\"type\":\"double\"");
    }

    [Fact]
    public void GetSchemaJson_EscapesQuotesAndBackslashes()
    {
        // Construct an instance whose default value contains a quote so we can
        // exercise the JSON-string escape path in the schema encoder.
        var s = new SimpleScript { Tag = "name with \"quote\" and \\backslash" };
        var json = ExposedPropertyHelpers.GetSchemaJson(s);
        json.Should().Contain("name with \\\"quote\\\" and \\\\backslash");
    }

    [Fact]
    public void GetSchemaJson_NoMembersReturnsEmptyArray()
    {
        ExposedPropertyHelpers.GetSchemaJson(new object()).Should().Be("[]");
    }

    private class UnsupportedTypeScript
    {
        [ExposedProperty]
        public System.Guid Id = System.Guid.Empty;
    }
}
