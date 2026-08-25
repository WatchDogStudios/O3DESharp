//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using Xunit;
using FluentAssertions;

namespace O3DESharp.SourceGenerators.Tests;

public class StaticDispatchGeneratorTests
{
    private const string SampleJson = """
    {
        "classes": [
            { "name": "TestClass", "type_id": "{11111111-1111-1111-1111-111111111111}", "source_gem_name": "TestGem",
              "methods": [{ "name": "DoThing", "parameters": [], "is_static": false }],
              "properties": [{ "name": "Health", "type": {"marshal_type": "Int32", "type_name": "int"}, "is_readonly": false }] }
        ],
        "ebuses": [
            { "name": "TestBus", "events": [{ "name": "OnTick", "parameters": [{"marshal_type":"Single","type_name":"float"}], "is_broadcast": true }] }
        ],
        "global_methods": [
            { "name": "GlobalDoThing", "parameters": [] }
        ],
        "global_properties": [
            { "name": "GlobalFlag", "type": {"marshal_type": "Boolean", "type_name": "bool"}, "is_readonly": false }
        ]
    }
    """;

    [Fact]
    public void ParseReflectionData_FindsDeclaredClassMethodAndGlobalNames()
    {
        var parsed = StaticDispatchGenerator.ParseReflectionData(SampleJson);

        parsed.ClassExists("TestClass").Should().BeTrue();
        parsed.ClassExists("NoSuchClass").Should().BeFalse();

        parsed.MethodExists("TestClass", "DoThing").Should().BeTrue();
        parsed.MethodExists("TestClass", "NoSuchMethod").Should().BeFalse();
        parsed.MethodExists("NoSuchClass", "DoThing").Should().BeFalse();

        parsed.PropertyExists("TestClass", "Health").Should().BeTrue();
        parsed.PropertyExists("TestClass", "NoSuchProperty").Should().BeFalse();

        parsed.GlobalMethodExists("GlobalDoThing").Should().BeTrue();
        parsed.GlobalMethodExists("NoSuchGlobal").Should().BeFalse();

        parsed.GlobalPropertyExists("GlobalFlag").Should().BeTrue();
        parsed.GlobalPropertyExists("NoSuchGlobalProperty").Should().BeFalse();

        parsed.BusExists("TestBus").Should().BeTrue();
        parsed.BusExists("NoSuchBus").Should().BeFalse();
    }

    [Fact]
    public void ParseReflectionData_EmptyOrMalformedJson_ReturnsEmptySetsNotAnException()
    {
        var parsed = StaticDispatchGenerator.ParseReflectionData("{ not valid json");

        parsed.ClassExists("Anything").Should().BeFalse();
        parsed.BusExists("Anything").Should().BeFalse();
    }
}
