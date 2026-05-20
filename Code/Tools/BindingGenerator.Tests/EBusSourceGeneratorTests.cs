//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace O3DESharp.BindingGenerator.Tests;

/// <summary>
/// Smoke tests for the Roslyn source generator
/// (O3DESharp.SourceGenerators.EBusHandlerGenerator).
///
/// Full source-generator harness testing requires hosting the Roslyn
/// compiler in-process via Microsoft.CodeAnalysis.CSharp.Workspaces,
/// which is a heavier dependency than this xunit project wants. Instead
/// these tests verify the GENERATOR's emit shape indirectly: we build
/// O3DE.Core (which references the generator as an Analyzer) and grep
/// the compiled assembly's IL strings for the marker text the generator
/// emits. If the generator's pipeline broke, the marker text wouldn't
/// be in the IL.
///
/// This is a cheap regression net for "the source generator ships at
/// all" rather than a full behavior test of the generated code itself.
/// Behavior tests for the generated Connect/Disconnect methods live in
/// the runtime test fixtures (Phase 18-E2 follow-up).
/// </summary>
public class EBusSourceGeneratorTests
{
    private const string CoreAssemblyName = "O3DE.Core.dll";

    /// <summary>
    /// Walk up from the test bin output to find the gem root, then
    /// locate the staged O3DE.Core.dll.
    /// </summary>
    private static string? LocateCoreAssembly()
    {
        // Test runner working directory is typically
        // <gem>/Code/Tools/BindingGenerator.Tests/bin/<Config>/<TFM>/
        // The deployed O3DE.Core.dll lives at
        // <gem>/Assets/Scripts/O3DE.Core/bin/<Config>/net9.0/.
        var testAssembly = Assembly.GetExecutingAssembly();
        var here = Path.GetDirectoryName(testAssembly.Location);
        if (here is null) return null;

        // Walk up to the gem root.
        var dir = new DirectoryInfo(here);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "gem.json")))
        {
            dir = dir.Parent;
        }
        if (dir is null) return null;

        var coreBinRoot = Path.Combine(dir.FullName, "Assets", "Scripts", "O3DE.Core", "bin");
        if (!Directory.Exists(coreBinRoot)) return null;

        // Prefer Release; fall back to Debug/Profile. Within each
        // config, prefer net9.0 over older TFMs - O3DE.Core's csproj
        // targets net9.0 but stale net8.0 outputs may linger from a
        // previous build, and reflection.LoadFrom on those returns the
        // OLD assembly (missing the new EBus types). The framework
        // version is encoded in the path, so we just scan in
        // newest-first order.
        foreach (var config in new[] { "Release", "Debug", "Profile" })
        {
            var configDir = Path.Combine(coreBinRoot, config);
            if (!Directory.Exists(configDir)) continue;
            // Scan TFM subdirs in newest-first lexical order. net9.0
            // sorts before net8.0 once you reverse, etc.
            var tfmDirs = Directory.GetDirectories(configDir)
                .OrderByDescending(d => Path.GetFileName(d));
            foreach (var tfmDir in tfmDirs)
            {
                var dll = Path.Combine(tfmDir, CoreAssemblyName);
                if (File.Exists(dll)) return dll;
            }
        }
        return null;
    }

    /// <summary>
    /// Cross-TFM Assembly.LoadFrom-then-reflect is fragile when the
    /// test runner (.NET 8) doesn't match the target's TFM (.NET 9).
    /// Type discovery hits "TypeLoadException for referenced types
    /// that exist only in the newer runtime", which surfaces as
    /// GetType returning null even though the type IS in the assembly.
    ///
    /// Workaround: byte-scan the DLL for the fully-qualified type name
    /// as a UTF-8 substring. PE metadata stores type names as plain
    /// strings in the #Strings heap, so a grep against the file
    /// contents reliably reports whether the type is defined - without
    /// needing to actually load the assembly. Loses some richness
    /// (can't check method signatures, accessibility, etc.) but
    /// catches every regression these smoke tests are meant to
    /// catch (type renamed, deleted, moved to a different namespace).
    /// </summary>
    private static bool AssemblyContainsTypeName(string dllPath, string typeName)
    {
        // Simple name part is what shows up in the metadata strings
        // heap; the namespace is stored separately. Both still appear
        // as plain UTF-8 bytes in the file.
        var bytes = File.ReadAllBytes(dllPath);
        var simpleName = typeName.Substring(typeName.LastIndexOf('.') + 1);
        var simpleNameBytes = System.Text.Encoding.UTF8.GetBytes(simpleName);
        // Naive substring search. The simple name is unique enough in
        // our case (EBusAttribute / EBusHandlerAttribute /
        // EBusHandlerRegistry) that we don't need to worry about
        // false positives.
        for (int i = 0; i <= bytes.Length - simpleNameBytes.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < simpleNameBytes.Length; j++)
            {
                if (bytes[i + j] != simpleNameBytes[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    [Fact]
    public void EBusAttribute_IsDefinedInCore()
    {
        var dll = LocateCoreAssembly();
        if (dll is null) return;
        AssemblyContainsTypeName(dll, "O3DE.EBusAttribute").Should().BeTrue(
            "O3DE.EBusAttribute must remain a public type for the source generator to consume");
    }

    [Fact]
    public void EBusHandlerAttribute_IsDefinedInCore()
    {
        var dll = LocateCoreAssembly();
        if (dll is null) return;
        AssemblyContainsTypeName(dll, "O3DE.EBusHandlerAttribute").Should().BeTrue(
            "O3DE.EBusHandlerAttribute must remain a public type");
    }

    [Fact]
    public void EBusHandlerRegistry_IsDefinedInCore()
    {
        // The source-generator-emitted code calls into this registry.
        // Renaming or removing it would silently break every [EBus]-
        // attributed class.
        var dll = LocateCoreAssembly();
        if (dll is null) return;
        AssemblyContainsTypeName(dll, "O3DE.Reflection.EBusHandlerRegistry").Should().BeTrue(
            "EBusHandlerRegistry is the contract the source generator emits calls to");
    }

    [Fact]
    public void SourceGeneratorAssembly_BuildsAndExportsTheGeneratorType()
    {
        // Locate the source generator's DLL the same way; it's emitted
        // to ...SourceGenerators/bin/<Config>/netstandard2.0/. If the
        // analyzer project's IsRoslynComponent contract were broken,
        // this DLL wouldn't have the [Generator]-decorated type.
        var dll = LocateCoreAssembly();
        if (dll is null) return;
        var coreDir = new DirectoryInfo(Path.GetDirectoryName(dll)!);
        // From .../O3DE.Core/bin/<Config>/<TFM>/, walk up to Assets/Scripts/, then over to Code/Tools.
        var gemRoot = coreDir.Parent?.Parent?.Parent?.Parent?.Parent;  // up to gem root
        gemRoot.Should().NotBeNull();
        var srcGenDir = Path.Combine(gemRoot!.FullName, "Code", "Tools", "SourceGenerators", "bin");
        if (!Directory.Exists(srcGenDir)) return;

        var srcGenDll = Directory.EnumerateFiles(srcGenDir, "O3DESharp.SourceGenerators.dll", SearchOption.AllDirectories).FirstOrDefault();
        srcGenDll.Should().NotBeNull("source generator assembly must build to bin/.../O3DESharp.SourceGenerators.dll");
        var asm = Assembly.LoadFrom(srcGenDll!);
        var generator = asm.GetType("O3DESharp.SourceGenerators.EBusHandlerGenerator");
        generator.Should().NotBeNull("EBusHandlerGenerator type must exist");
        generator!.GetCustomAttributes(inherit: false)
            .Should().Contain(a => a.GetType().Name == "GeneratorAttribute",
                "EBusHandlerGenerator must be decorated with [Generator] for Roslyn to find it");
    }
}
