//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System.Collections.Generic;
using System.IO;
using O3DESharp.BindingGenerator.Caching;
using O3DESharp.BindingGenerator.Configuration;
using O3DESharp.BindingGenerator.Generation;
using O3DESharp.BindingGenerator.Parsing;

namespace O3DESharp.BindingGenerator.Tests;

/// <summary>
/// Focused unit tests for the generator's pure-code paths. Unlike the
/// integration suite these do not spin up libclang or write files; they
/// exercise the bits of the generator that govern correctness on the C#
/// side: type mapping, overload disambiguation, defines merging, and the
/// new transitive-include cache tracking.
/// </summary>
public class GeneratorUnitTests
{
    // --------------------------------------------------------------------
    // TypeMapper
    // --------------------------------------------------------------------

    [Theory]
    [InlineData("bool", "bool", false, false, false)]
    [InlineData("int", "int", false, false, false)]
    [InlineData("float", "float", false, false, false)]
    [InlineData("AZ::Vector3", "Vector3", false, false, false)]
    [InlineData("AZ::Quaternion", "Quaternion", false, false, false)]
    [InlineData("AZ::EntityId", "ulong", false, false, false)]
    [InlineData("AZ::u32", "uint", false, false, false)]
    [InlineData("AZ::s64", "long", false, false, false)]
    [InlineData("const char*", "IntPtr", true, true, false)] // pointer wins
    [InlineData("AZStd::string", "IntPtr", false, false, false)]
    [InlineData("const AZ::Vector3&", "Vector3", true, false, true)]
    public void TypeMapper_MapsKnownTypes(string cppType, string expectedCsType, bool isConst, bool isPointer, bool isReference)
    {
        var mapper = new TypeMapper();
        var result = mapper.MapType(cppType);

        result.CSharpTypeName.Should().Be(expectedCsType);
        result.IsConst.Should().Be(isConst, $"const-ness for {cppType}");
        result.IsPointer.Should().Be(isPointer, $"pointer-ness for {cppType}");
        result.IsReference.Should().Be(isReference, $"reference-ness for {cppType}");
    }

    [Fact]
    public void TypeMapper_UnknownTypeBecomesIntPtr()
    {
        var mapper = new TypeMapper();
        var result = mapper.MapType("SomeUnknownType");

        result.CSharpTypeName.Should().Be("IntPtr");
        result.RequiresMarshaling.Should().BeTrue();
    }

    [Fact]
    public void TypeMapper_TemplateTypesBecomeIntPtr()
    {
        var mapper = new TypeMapper();
        mapper.MapType("AZStd::vector<int>").CSharpTypeName.Should().Be("IntPtr");
        mapper.MapType("AZStd::shared_ptr<MyClass>").CSharpTypeName.Should().Be("IntPtr");
    }

    // --------------------------------------------------------------------
    // CSharpCodeGenerator.InternalCallFieldName (overload disambiguation)
    // --------------------------------------------------------------------

    [Fact]
    public void InternalCallFieldName_NoOverload_UsesPlainName()
    {
        var foo = new ParsedMethod { Name = "Foo" };
        var name = CSharpCodeGenerator.InternalCallFieldName("MyClass", foo, new[] { foo });
        name.Should().Be("MyClass_Foo");
    }

    [Fact]
    public void InternalCallFieldName_OverloadedSameArity_DisambiguatesByType()
    {
        var setInt = new ParsedMethod
        {
            Name = "Set",
            Parameters = { new ParsedParameter { Name = "v", Type = new ParsedType { CSharpTypeName = "int" } } },
        };
        var setFloat = new ParsedMethod
        {
            Name = "Set",
            Parameters = { new ParsedParameter { Name = "v", Type = new ParsedType { CSharpTypeName = "float" } } },
        };
        var all = new[] { setInt, setFloat };

        var intName = CSharpCodeGenerator.InternalCallFieldName("MyClass", setInt, all);
        var floatName = CSharpCodeGenerator.InternalCallFieldName("MyClass", setFloat, all);

        intName.Should().Be("MyClass_Set_1_int");
        floatName.Should().Be("MyClass_Set_1_float");
        intName.Should().NotBe(floatName, "overloads must have distinct InternalCalls field names");
    }

    [Fact]
    public void InternalCallFieldName_OverloadedDifferentArity_DisambiguatesByArity()
    {
        var zero = new ParsedMethod { Name = "Foo" };
        var one = new ParsedMethod
        {
            Name = "Foo",
            Parameters = { new ParsedParameter { Name = "x", Type = new ParsedType { CSharpTypeName = "int" } } },
        };
        var all = new[] { zero, one };

        CSharpCodeGenerator.InternalCallFieldName("MyClass", zero, all).Should().Be("MyClass_Foo_0");
        CSharpCodeGenerator.InternalCallFieldName("MyClass", one, all).Should().Be("MyClass_Foo_1_int");
    }

    [Fact]
    public void InternalCallFieldName_TypeNameWithIllegalChars_SanitizesToUnderscores()
    {
        var a = new ParsedMethod
        {
            Name = "Foo",
            Parameters = { new ParsedParameter { Name = "x", Type = new ParsedType { CSharpTypeName = "MyNs.Vector3" } } },
        };
        var b = new ParsedMethod
        {
            Name = "Foo",
            Parameters = { new ParsedParameter { Name = "x", Type = new ParsedType { CSharpTypeName = "int" } } },
        };

        var name = CSharpCodeGenerator.InternalCallFieldName("MyClass", a, new[] { a, b });
        name.Should().Be("MyClass_Foo_1_MyNs_Vector3");
    }

    // --------------------------------------------------------------------
    // GetMarshalType: ABI surface for the InternalCalls delegate field
    // (Phase 8 fix - any reference becomes IntPtr so by-ref semantics are
    // honored on the C++ side instead of silently passing by value)
    // --------------------------------------------------------------------

    // Use a tiny adapter to reach the private GetMarshalType without making
    // it public on CSharpCodeGenerator. The behavior we care about is the
    // composition of TypeMapper.MapType + GetMarshalType, so we drive it
    // end-to-end through MapType and inspect the resulting ParsedType.

    [Theory]
    [InlineData("AZ::Vector3", "Vector3", false, false, false)]      // value -> value-type kept
    [InlineData("AZ::Vector3*", "IntPtr", false, true, false)]       // pointer -> IntPtr
    [InlineData("AZ::Vector3&", "Vector3", false, false, true)]      // blittable ref -> typename + IsReference
    [InlineData("const AZ::Vector3&", "Vector3", true, false, true)] // const blittable ref -> typename + IsConst + IsReference
    [InlineData("AZ::Foo&", "IntPtr", false, false, true)]            // non-blittable ref -> IntPtr (degrades)
    public void TypeMapper_ReferenceFlagsSurviveForBlittableTypes(
        string cppType,
        string expectedCsType,
        bool isConst,
        bool isPointer,
        bool isReference)
    {
        var mapper = new TypeMapper();
        var pt = mapper.MapType(cppType);
        pt.CSharpTypeName.Should().Be(expectedCsType);
        pt.IsConst.Should().Be(isConst);
        pt.IsPointer.Should().Be(isPointer);
        pt.IsReference.Should().Be(isReference);
    }

    // --------------------------------------------------------------------
    // CppRegistrationGenerator.BuildCppSignature
    // (Phase 11 - emit real C++ forward decls instead of /* parameters */)
    // --------------------------------------------------------------------

    [Fact]
    public void BuildCppSignature_InstanceVoidMethod_EmitsThisPointerOnly()
    {
        var method = new ParsedMethod
        {
            Name = "DoThing",
            IsStatic = false,
            ReturnType = new ParsedType { CSharpTypeName = "void" },
        };
        CppRegistrationGenerator.BuildCppSignature("MyClass_DoThing", method)
            .Should().Be("void MyClass_DoThing(void* thisPtr)");
    }

    [Fact]
    public void BuildCppSignature_StaticMethodNoArgs_OmitsThisPointer()
    {
        var method = new ParsedMethod
        {
            Name = "CreateDefault",
            IsStatic = true,
            ReturnType = new ParsedType { CSharpTypeName = "IntPtr" },
        };
        CppRegistrationGenerator.BuildCppSignature("MyClass_CreateDefault", method)
            .Should().Be("void* MyClass_CreateDefault()");
    }

    [Fact]
    public void BuildCppSignature_InstanceMethodWithMixedArgs_MapsEachType()
    {
        var method = new ParsedMethod
        {
            Name = "SetValues",
            IsStatic = false,
            ReturnType = new ParsedType { CSharpTypeName = "bool" },
            Parameters =
            {
                new ParsedParameter { Name = "speed",    Type = new ParsedType { CSharpTypeName = "float" } },
                new ParsedParameter { Name = "maxHits",  Type = new ParsedType { CSharpTypeName = "int" } },
                new ParsedParameter { Name = "position", Type = new ParsedType { CSharpTypeName = "Vector3" } },
                new ParsedParameter { Name = "owner",    Type = new ParsedType { CSharpTypeName = "IntPtr" } },
            },
        };
        CppRegistrationGenerator.BuildCppSignature("MyClass_SetValues", method)
            .Should()
            .Be("bool MyClass_SetValues(void* thisPtr, float speed, AZ::s32 maxHits, O3DESharp::InteropVector3 position, void* owner)");
    }

    [Fact]
    public void BuildCppSignature_ReferenceParameter_DegradesToVoidPointer()
    {
        // Phase 8 made GetMarshalType return IntPtr for any reference, so
        // the C++ side sees a void* and is responsible for dereferencing.
        var method = new ParsedMethod
        {
            Name = "Apply",
            IsStatic = false,
            ReturnType = new ParsedType { CSharpTypeName = "void" },
            Parameters =
            {
                new ParsedParameter
                {
                    Name = "vec",
                    Type = new ParsedType { CSharpTypeName = "Vector3", IsReference = true, IsConst = true },
                },
            },
        };
        CppRegistrationGenerator.BuildCppSignature("MyClass_Apply", method)
            .Should().Be("void MyClass_Apply(void* thisPtr, void* vec)");
    }

    [Fact]
    public void BuildCppSignature_StandaloneFunction_HandlesParameters()
    {
        var fn = new ParsedFunction
        {
            Name = "GlobalDoThing",
            ReturnType = new ParsedType { CSharpTypeName = "Quaternion" },
            Parameters =
            {
                new ParsedParameter { Name = "x", Type = new ParsedType { CSharpTypeName = "double" } },
            },
        };
        CppRegistrationGenerator.BuildCppSignature("GlobalDoThing", fn)
            .Should().Be("O3DESharp::InteropQuaternion GlobalDoThing(double x)");
    }

    [Fact]
    public void BuildCppSignature_EmptyParameterName_SubstitutesArgN()
    {
        var method = new ParsedMethod
        {
            Name = "Take",
            IsStatic = true,
            ReturnType = new ParsedType { CSharpTypeName = "void" },
            Parameters =
            {
                new ParsedParameter { Name = "", Type = new ParsedType { CSharpTypeName = "int" } },
            },
        };
        CppRegistrationGenerator.BuildCppSignature("Util_Take", method)
            .Should().Be("void Util_Take(AZ::s32 arg0)");
    }

    [Fact]
    public void BuildCppSignature_UnknownMarshalType_FallsBackToCommentedVoidStar()
    {
        // Defensive: an unmapped C# type should produce a forward decl that
        // still parses as C++ (void*) but flags itself with a comment so a
        // human reviewer notices the gap.
        var method = new ParsedMethod
        {
            Name = "Mystery",
            IsStatic = true,
            ReturnType = new ParsedType { CSharpTypeName = "MysteryType" },
        };
        CppRegistrationGenerator.BuildCppSignature("Util_Mystery", method)
            .Should().Contain("/* MysteryType */");
    }

    // --------------------------------------------------------------------
    // BindingConfig: engine-required defines never disappear behind user JSON
    // --------------------------------------------------------------------

    [Fact]
    public void EngineRequiredDefines_StaysNonEmptyEvenWithEmptyUserConfig()
    {
        // Phase 2 moved the AZ_RTTI / AZ_CLASS_ALLOCATOR macro suppressions out
        // of GlobalSettings.Defines (which can be clobbered by user JSON) into
        // BindingConfig.EngineRequiredDefines (which cannot).
        BindingConfig.EngineRequiredDefines.Should().NotBeEmpty();
        BindingConfig.EngineRequiredDefines.Should().Contain("AZ_RTTI(...)=");
        BindingConfig.EngineRequiredDefines.Should().Contain("AZ_CLASS_ALLOCATOR(...)=");
    }

    [Fact]
    public void GlobalSettings_DefinesDefaultsToEmpty_SoUserJsonIsAdditive()
    {
        // A fresh BindingConfig has no user-defined macros - everything is in
        // EngineRequiredDefines. This is what enables the additive merge.
        var config = new BindingConfig();
        config.Global.Defines.Should().BeEmpty();
    }

    // --------------------------------------------------------------------
    // BuildCache: transitive headers
    // --------------------------------------------------------------------

    [Fact]
    public void BuildCache_InvalidatesWhenTransitiveHeaderChanges()
    {
        // Set up two real files - one "direct" (the gem's header) and one
        // "transitive" (what the gem #includes). Cache the pair, then mutate
        // only the transitive file and verify NeedsRegeneration returns true.
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        var direct = Path.Combine(tempDir, "Direct.h");
        var transitive = Path.Combine(tempDir, "AzCoreMathVector3.h");
        var cacheFile = Path.Combine(tempDir, ".binding_cache.json");

        try
        {
            File.WriteAllText(direct, "// direct v1");
            File.WriteAllText(transitive, "// transitive v1");

            // The cache's "output files exist" check is independent of direct/
            // transitive tracking but trips on a non-existent path. Point it at a
            // real on-disk file so the test isolates the behavior under test.
            var outputArtifact = Path.Combine(tempDir, "out.g.cs");
            File.WriteAllText(outputArtifact, "// generated");

            var cache = new BuildCache(cacheFile);

            // First run: no entry exists => miss.
            cache.NeedsRegeneration("TestGem", new[] { direct }, "cfg-hash").Should().BeTrue();

            // Record the cache as if generation just finished, with the
            // transitive file reported by libclang.
            cache.UpdateEntry("TestGem", new[] { direct }, new[] { transitive }, "cfg-hash", new[] { outputArtifact });

            // Same inputs - cache hit.
            cache.NeedsRegeneration("TestGem", new[] { direct }, "cfg-hash").Should().BeFalse();

            // Mutate ONLY the transitive header. The direct header is byte-
            // identical. Old code would have hit the cache; new code must miss.
            File.WriteAllText(transitive, "// transitive v2");
            cache.NeedsRegeneration("TestGem", new[] { direct }, "cfg-hash").Should().BeTrue(
                "BuildCache must invalidate when a transitive #include changes, even if the gem's own header is unchanged");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void BuildCache_InvalidatesWhenDirectHeaderSetChanges()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        var h1 = Path.Combine(tempDir, "H1.h");
        var h2 = Path.Combine(tempDir, "H2.h");
        var cacheFile = Path.Combine(tempDir, ".binding_cache.json");

        try
        {
            File.WriteAllText(h1, "// h1");
            File.WriteAllText(h2, "// h2");
            var outputArtifact = Path.Combine(tempDir, "out.g.cs");
            File.WriteAllText(outputArtifact, "// generated");

            var cache = new BuildCache(cacheFile);
            cache.UpdateEntry("TestGem", new[] { h1 }, System.Array.Empty<string>(), "cfg", new[] { outputArtifact });
            cache.NeedsRegeneration("TestGem", new[] { h1 }, "cfg").Should().BeFalse();

            // Same content, but a new direct header was added to the input set.
            cache.NeedsRegeneration("TestGem", new[] { h1, h2 }, "cfg").Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
