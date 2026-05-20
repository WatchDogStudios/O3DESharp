//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using O3DESharp.BindingGenerator.Configuration;
using O3DESharp.BindingGenerator.Generation;

namespace O3DESharp.BindingGenerator.Tests;

/// <summary>
/// Tests for the JSON-driven reflection backend
/// (<see cref="ReflectionBindingGenerator"/>).
///
/// These tests feed synthetic reflection_data.json fragments through
/// the generator and assert structural properties of the emitted C#:
///   - identifier validity (no <c>self. = value</c>, no <c>void</c>
///     parameters, no <c>@</c> mid-identifier)
///   - addressed-vs-broadcast bus wrappers
///   - duplicate-class-name suppression
///   - marshal-type table coverage
///   - csproj emission per gem bucket
///
/// The fixtures here are *handwritten* JSON rather than the full live
/// reflection_data.json - that file is 3MB+ and brittle against engine
/// changes. The handwritten fixtures pin exactly the input shapes that
/// past regressions came from (each fixture is a regression test for a
/// specific generator bug we've shipped a fix for).
/// </summary>
public class ReflectionGeneratorTests : IDisposable
{
    private readonly string _outputDir;

    public ReflectionGeneratorTests()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), "O3DESharpRefTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_outputDir, recursive: true); }
        catch { /* tests can leak temp dirs on Windows when AV holds files */ }
    }

    // ============================================================
    // Helpers
    // ============================================================

    /// <summary>
    /// Write a JSON fixture to a temp file and run the generator against it.
    /// Returns the directory the generator emitted to.
    /// </summary>
    private string GenerateFromJson(string json)
    {
        var jsonPath = Path.Combine(_outputDir, "reflection_data.json");
        File.WriteAllText(jsonPath, json);

        var gen = new ReflectionBindingGenerator(rootNamespace: "O3DE.Generated", verbose: false);
        var result = gen.Generate(jsonPath, _outputDir, includeGems: null);
        result.Success.Should().BeTrue($"generator should succeed; error: {result.ErrorMessage}");
        return _outputDir;
    }

    private static string ReadGeneratedFile(string outputDir, string relativePath)
    {
        var fullPath = Path.Combine(outputDir, relativePath);
        File.Exists(fullPath).Should().BeTrue($"expected generator to emit {relativePath}");
        return File.ReadAllText(fullPath);
    }

    // ============================================================
    // Regression: no "self. = value" or "self.()" garbage
    // ============================================================

    [Fact]
    public void GeneratedCode_HasNoSelfDotEquals_ForNamelessProperty()
    {
        // Regression for the EMotionFX JointSelectionWidget bug: a
        // class with a property whose Name came through as the empty
        // string used to generate "self. = value;" (CS1001).
        var json = """
        {
            "classes": [{
                "name": "TestClass",
                "type_id": "{11111111-1111-1111-1111-111111111111}",
                "source_gem_name": "TestGem",
                "methods": [],
                "properties": [
                    { "name": "", "type": {"marshal_type": "Int32", "type_name": "int"}, "is_readonly": false },
                    { "name": "ValidProp", "type": {"marshal_type": "Int32", "type_name": "int"}, "is_readonly": false }
                ]
            }],
            "ebuses": [], "global_methods": [], "global_properties": []
        }
        """;
        var dir = GenerateFromJson(json);
        var cs = ReadGeneratedFile(dir, "TestGem/Classes/TestClass.g.cs");
        cs.Should().NotContain("self. =", "empty-name property must be skipped");
        cs.Should().NotContain("Set(", "missing identifier in setter is CS1001");
        cs.Should().Contain("SetValidProp", "valid property must still emit");
    }

    [Fact]
    public void GeneratedCode_HasNoAtInsideIdentifier()
    {
        // Regression for the "Get@namespace" / "arg0_@string" bugs.
        // The @ keyword-escape is only valid at the START of an
        // identifier; any @ appearing mid-identifier is a compile error.
        var json = """
        {
            "classes": [{
                "name": "TestClass",
                "type_id": "{22222222-2222-2222-2222-222222222222}",
                "source_gem_name": "TestGem",
                "methods": [{
                    "name": "Method",
                    "is_static": true,
                    "return_type": {"marshal_type": "Void", "type_name": "void"},
                    "parameters": [
                        { "name": "string", "type_name": "string", "marshal_type": "String" },
                        { "name": "namespace", "type_name": "namespace", "marshal_type": "String" }
                    ]
                }],
                "properties": [
                    { "name": "namespace", "type": {"marshal_type": "Int32", "type_name": "int"}, "is_readonly": false }
                ]
            }],
            "ebuses": [], "global_methods": [], "global_properties": []
        }
        """;
        var dir = GenerateFromJson(json);
        var cs = ReadGeneratedFile(dir, "TestGem/Classes/TestClass.g.cs");

        // @ may appear at the start of an identifier only. Find all @
        // characters and verify each is preceded by a non-identifier
        // character (start of token).
        foreach (Match m in Regex.Matches(cs, @"@"))
        {
            var prev = m.Index == 0 ? '\n' : cs[m.Index - 1];
            (char.IsLetterOrDigit(prev) || prev == '_').Should().BeFalse(
                $"'@' at index {m.Index} should not be mid-identifier (preceded by '{prev}')");
        }
    }

    // ============================================================
    // Regression: void parameter types skipped
    // ============================================================

    [Fact]
    public void GeneratedCode_SkipsMethodsWithVoidParameters()
    {
        // C# doesn't allow `void` as a parameter type. The reflection
        // exporter occasionally surfaces this from BehaviorContext
        // metadata generics; the generator must skip the wrapper
        // entirely rather than emit "(void arg0)".
        var json = """
        {
            "classes": [{
                "name": "TestClass",
                "type_id": "{33333333-3333-3333-3333-333333333333}",
                "source_gem_name": "TestGem",
                "methods": [
                    {
                        "name": "BadMethod", "is_static": true,
                        "return_type": {"marshal_type": "Void", "type_name": "void"},
                        "parameters": [
                            { "name": "voidArg", "type_name": "void", "marshal_type": "Void" }
                        ]
                    },
                    {
                        "name": "GoodMethod", "is_static": true,
                        "return_type": {"marshal_type": "Void", "type_name": "void"},
                        "parameters": [
                            { "name": "intArg", "type_name": "int", "marshal_type": "Int32" }
                        ]
                    }
                ],
                "properties": []
            }],
            "ebuses": [], "global_methods": [], "global_properties": []
        }
        """;
        var dir = GenerateFromJson(json);
        var cs = ReadGeneratedFile(dir, "TestGem/Classes/TestClass.g.cs");
        cs.Should().NotContain("void arg", "methods with void parameters must be skipped (CS1536)");
        cs.Should().NotContain("BadMethod", "void-parameter method must be skipped entirely");
        cs.Should().Contain("GoodMethod", "method with valid parameter types must emit");
    }

    // ============================================================
    // Addressed bus emits both addressed and broadcast wrappers
    // ============================================================

    [Fact]
    public void AddressedBus_EmitsBothVariants()
    {
        // TransformBus-shaped fixture - addressed bus with EntityId
        // address type. Should produce SetWorldTranslation(ulong, ...)
        // AND SetWorldTranslationBroadcast(...).
        var json = """
        {
            "classes": [],
            "ebuses": [{
                "name": "TestBus",
                "source_gem_name": "TestGem",
                "address_type": {"name": "EntityId", "type_name": "EntityId", "marshal_type": "EntityId"},
                "events": [{
                    "name": "DoThing",
                    "bus_name": "TestBus",
                    "is_broadcast": false,
                    "return_type": {"marshal_type": "Void", "type_name": "void"},
                    "parameters": [
                        {"name": "value", "type_name": "float", "marshal_type": "Float"}
                    ]
                }]
            }],
            "global_methods": [], "global_properties": []
        }
        """;
        var dir = GenerateFromJson(json);
        var cs = ReadGeneratedFile(dir, "TestGem/EBuses/TestBus.g.cs");
        cs.Should().Contain("DoThing(ulong busId, float", "addressed variant must take busId first");
        cs.Should().Contain("DoThingBroadcast(float", "broadcast variant must be suffixed");
        cs.Should().Contain("SendEBusEvent(BusName, \"DoThing\", busId",
            "addressed variant routes through SendEBusEvent");
        cs.Should().Contain("BroadcastEBusEvent(BusName, \"DoThing\"",
            "broadcast variant routes through BroadcastEBusEvent");
    }

    [Fact]
    public void PureBroadcastBus_EmitsOnlyBroadcastVariant()
    {
        // TickRequestBus-shaped fixture - empty address_type means a
        // pure-broadcast bus. Should NOT emit a *Broadcast suffix
        // (the unsuffixed form already broadcasts).
        var json = """
        {
            "classes": [],
            "ebuses": [{
                "name": "TickBus",
                "source_gem_name": "TestGem",
                "address_type": {"name": "", "type_name": "", "marshal_type": "Unknown"},
                "events": [{
                    "name": "OnTick",
                    "bus_name": "TickBus",
                    "is_broadcast": true,
                    "return_type": {"marshal_type": "Float", "type_name": "float"},
                    "parameters": []
                }]
            }],
            "global_methods": [], "global_properties": []
        }
        """;
        var dir = GenerateFromJson(json);
        var cs = ReadGeneratedFile(dir, "TestGem/EBuses/TickBus.g.cs");
        cs.Should().Contain("OnTick()", "broadcast-only bus emits unsuffixed wrapper");
        cs.Should().NotContain("OnTickBroadcast", "no suffixed variant for pure-broadcast buses");
        cs.Should().NotContain("ulong busId", "no addressed variant for pure-broadcast buses");
    }

    // ============================================================
    // Duplicate-class-name suppression
    // ============================================================

    [Fact]
    public void DuplicateSimpleClassNames_AreDeduped()
    {
        // Two reflected FQNs sharing the same last-namespace-segment
        // (AZ::RenderStates and AZ::RHI::RenderStates) would both try
        // to declare `public static class RenderStates` in the same
        // namespace - CS0101.
        var json = """
        {
            "classes": [
                {
                    "name": "AZ::RenderStates",
                    "type_id": "{44444444-4444-4444-4444-444444444444}",
                    "source_gem_name": "TestGem",
                    "methods": [], "properties": []
                },
                {
                    "name": "AZ::RHI::RenderStates",
                    "type_id": "{55555555-5555-5555-5555-555555555555}",
                    "source_gem_name": "TestGem",
                    "methods": [], "properties": []
                }
            ],
            "ebuses": [], "global_methods": [], "global_properties": []
        }
        """;
        var dir = GenerateFromJson(json);
        var classesDir = Path.Combine(dir, "TestGem", "Classes");
        var files = Directory.GetFiles(classesDir, "*.g.cs");

        // Count how many files declare "public static class RenderStates"
        // (case-sensitive). Should be exactly one - second-occurrence
        // is suppressed.
        int matches = files.Count(f =>
            Regex.IsMatch(File.ReadAllText(f), @"public\s+static\s+class\s+RenderStates\b"));
        matches.Should().Be(1, "duplicate C# class names must be deduped to first occurrence");
    }

    // ============================================================
    // Template specializations skipped
    // ============================================================

    [Fact]
    public void TemplateSpecializations_AreSkipped()
    {
        // Names containing < or > sanitize to unreadable identifiers
        // and the underlying containers route through TypeId marshalers
        // anyway. Skip.
        var json = """
        {
            "classes": [
                {
                    "name": "AZStd::vector<int>",
                    "type_id": "{66666666-6666-6666-6666-666666666666}",
                    "source_gem_name": "TestGem",
                    "methods": [], "properties": []
                },
                {
                    "name": "ValidName",
                    "type_id": "{77777777-7777-7777-7777-777777777777}",
                    "source_gem_name": "TestGem",
                    "methods": [], "properties": []
                }
            ],
            "ebuses": [], "global_methods": [], "global_properties": []
        }
        """;
        var dir = GenerateFromJson(json);
        var classesDir = Path.Combine(dir, "TestGem", "Classes");
        var files = Directory.GetFiles(classesDir, "*.g.cs").Select(Path.GetFileName).ToList();
        files.Should().Contain("ValidName.g.cs");
        files.Should().NotContain(f => f!.Contains("vector"), "template specializations must be skipped");
    }

    // ============================================================
    // csproj emitted per gem bucket
    // ============================================================

    [Fact]
    public void GenerationEmits_OneCsprojPerGemBucket()
    {
        var json = """
        {
            "classes": [
                { "name": "ClassA", "type_id": "{aaaa1111-0000-0000-0000-000000000000}", "source_gem_name": "GemA", "methods": [], "properties": [] },
                { "name": "ClassB", "type_id": "{bbbb1111-0000-0000-0000-000000000000}", "source_gem_name": "GemB", "methods": [], "properties": [] }
            ],
            "ebuses": [], "global_methods": [], "global_properties": []
        }
        """;
        var dir = GenerateFromJson(json);
        File.Exists(Path.Combine(dir, "GemA", "GemA.csproj")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "GemB", "GemB.csproj")).Should().BeTrue();

        var csprojA = File.ReadAllText(Path.Combine(dir, "GemA", "GemA.csproj"));
        csprojA.Should().Contain("<AssemblyName>O3DE.Generated.GemA</AssemblyName>");
        csprojA.Should().Contain("<DebugType>full</DebugType>",
            "Debug config must use full PDB for managed-debugger attach");
        csprojA.Should().Contain("DeployToBinScripts",
            "csproj must wire the deploy-to-Bin/Scripts post-build target");
    }

    [Fact]
    public void EmptySourceGemName_BucketsUnderCore()
    {
        // Items without an explicit source_gem_name fall back to "Core".
        // Same as the live editor's auto-export does when the gem
        // attribution couldn't be resolved.
        var json = """
        {
            "classes": [
                { "name": "Unbucketed", "type_id": "{cccc1111-0000-0000-0000-000000000000}", "source_gem_name": "", "methods": [], "properties": [] }
            ],
            "ebuses": [], "global_methods": [], "global_properties": []
        }
        """;
        var dir = GenerateFromJson(json);
        File.Exists(Path.Combine(dir, "Core", "Classes", "Unbucketed.g.cs")).Should().BeTrue();
    }

    // ============================================================
    // Marshal-type table coverage
    // ============================================================

    [Theory]
    [InlineData("Bool", "bool")]
    [InlineData("Int8", "sbyte")]
    [InlineData("Int16", "short")]
    [InlineData("Int32", "int")]
    [InlineData("Int64", "long")]
    [InlineData("UInt8", "byte")]
    [InlineData("UInt16", "ushort")]
    [InlineData("UInt32", "uint")]
    [InlineData("UInt64", "ulong")]
    [InlineData("Float", "float")]
    [InlineData("Double", "double")]
    [InlineData("String", "string")]
    [InlineData("EntityId", "ulong")]
    [InlineData("Vector2", "O3DE.Vector2")]
    [InlineData("Vector3", "O3DE.Vector3")]
    [InlineData("Vector4", "O3DE.Vector4")]
    [InlineData("Quaternion", "O3DE.Quaternion")]
    [InlineData("Color", "O3DE.Color")]
    [InlineData("Transform", "O3DE.Transform")]
    public void MarshalTypes_MapToExpectedCSharp(string marshalType, string expectedCSharp)
    {
        // Verify each marshal_type tag produces the right C# type in
        // wrapper signatures. Regressions here mean script authors get
        // the wrong C# type for a reflected parameter or return.
        var json = $$"""
        {
            "classes": [{
                "name": "TypeTestClass",
                "type_id": "{eeee1111-0000-0000-0000-000000000000}",
                "source_gem_name": "TypeTest",
                "methods": [{
                    "name": "Method",
                    "is_static": true,
                    "return_type": {"marshal_type": "{{marshalType}}", "type_name": "{{marshalType}}"},
                    "parameters": []
                }],
                "properties": []
            }],
            "ebuses": [], "global_methods": [], "global_properties": []
        }
        """;
        var dir = GenerateFromJson(json);
        var cs = ReadGeneratedFile(dir, "TypeTest/Classes/TypeTestClass.g.cs");
        cs.Should().Contain($"public static {expectedCSharp} Method(",
            $"marshal_type '{marshalType}' should produce C# return type '{expectedCSharp}'");
    }

    [Fact]
    public void UnknownMarshalType_FallsBackToObject()
    {
        // Anything we don't have a specific mapping for boxes as
        // `object` and round-trips through generic dispatch.
        var json = """
        {
            "classes": [{
                "name": "UnknownReturnClass",
                "type_id": "{ffff1111-0000-0000-0000-000000000000}",
                "source_gem_name": "TestGem",
                "methods": [{
                    "name": "Method", "is_static": true,
                    "return_type": {"marshal_type": "SomeFutureType", "type_name": "X"},
                    "parameters": []
                }],
                "properties": []
            }],
            "ebuses": [], "global_methods": [], "global_properties": []
        }
        """;
        var dir = GenerateFromJson(json);
        var cs = ReadGeneratedFile(dir, "TestGem/Classes/UnknownReturnClass.g.cs");
        cs.Should().Contain("public static object Method(",
            "unknown marshal_type falls back to System.Object");
    }
}
