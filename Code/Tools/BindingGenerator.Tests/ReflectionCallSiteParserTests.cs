//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System.IO;
using System.Linq;
using O3DESharp.BindingGenerator.Parsing;

namespace O3DESharp.BindingGenerator.Tests;

/// <summary>
/// Recovering `&amp;C::Method` from a reflection .cpp is the only way to learn a
/// native symbol - BehaviorContext type-erases it behind an AZStd::function, so
/// no runtime pass can supply it. These use small real C++ fixtures rather than
/// mocks, because what is being tested is precisely whether libclang sees what
/// we think it sees.
/// </summary>
public class ReflectionCallSiteParserTests
{
    private static string WriteFixture(string dir, string name, string source)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, source, System.Text.Encoding.UTF8);
        return path;
    }

    [Fact]
    public void RecoversPlainMemberFunctionPointer()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var path = WriteFixture(dir, "Reflect.cpp", """
            struct BehaviorContext {
                template<class T> BehaviorContext* Class(const char*) { return this; }
                template<class F> BehaviorContext* Method(const char*, F) { return this; }
            };
            struct Vector3 { float GetLength() const { return 0.0f; } };
            void Reflect(BehaviorContext* c) {
                c->Class<Vector3>("Vector3")->Method("GetLength", &Vector3::GetLength);
            }
            """);

            var parser = new ReflectionCallSiteParser(verbose: false);
            var result = parser.ParseFile(path, new[] { dir }, System.Array.Empty<string>());

            var site = result.CallSites.Should().ContainSingle().Subject;
            site.ReflectedName.Should().Be("GetLength");
            site.NativeQualifiedSymbol.Should().Contain("Vector3::GetLength");
            site.ViaLambda.Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void FlagsLambdaReflectedMethodRatherThanGuessing()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var path = WriteFixture(dir, "Reflect.cpp", """
            struct BehaviorContext {
                template<class T> BehaviorContext* Class(const char*) { return this; }
                template<class F> BehaviorContext* Method(const char*, F) { return this; }
            };
            struct Vector3 { float GetLength() const { return 0.0f; } };
            void Reflect(BehaviorContext* c) {
                c->Class<Vector3>("Vector3")->Method("Weird", [](Vector3* v) { return 1.0f; });
            }
            """);

            var parser = new ReflectionCallSiteParser(verbose: false);
            var result = parser.ParseFile(path, new[] { dir }, System.Array.Empty<string>());

            var site = result.CallSites.Should().ContainSingle().Subject;
            site.ReflectedName.Should().Be("Weird");
            site.ViaLambda.Should().BeTrue("a lambda has no &C::Method symbol to bind");
            site.NativeQualifiedSymbol.Should().BeEmpty();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void FileWithNoReflection_YieldsNoCallSites()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var path = WriteFixture(dir, "Plain.cpp", "int main() { return 0; }\n");

            var parser = new ReflectionCallSiteParser(verbose: false);
            var result = parser.ParseFile(path, new[] { dir }, System.Array.Empty<string>());

            result.CallSites.Should().BeEmpty();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }
}
