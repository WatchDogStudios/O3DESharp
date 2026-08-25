# Zero-Config Gem Bindings (Coral) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enabling a gem is the only thing a developer does — its reflection-backend C# bindings are generated, compiled into one DLL, deployed, and referenceable from game scripts with zero manual `binding_config.json` editing, zero manual `dotnet build`, and zero manual `<Reference>` additions.

**Architecture:** Two entry points (an Editor-startup background sync, and a new CMake/solution target) both drive the same generate→build→stage pipeline. The reflection generator is changed to emit one consolidated project instead of one per gem, gains an opt-out denylist, and the user-script `.csproj` template gets a fixed reference to the resulting DLL.

**Tech Stack:** C# (.NET 9, `O3DESharp.BindingGenerator` CLI, xUnit + FluentAssertions), CMake, Python (Editor tooling, PySide2 `QThread`, pytest).

## Global Constraints

- Reflection backend only — the clang backend's manual/opt-in flow is unchanged (spec §3).
- All gems included by default; a denylist opts specific gems out (never an allowlist to populate) (spec §3).
- The consolidated project's `AssemblyName` is `O3DESharp.GeneratedBindings` — every task that names the output DLL must use exactly this string.
- No new runtime dependency gate in a shipping Release launcher — the Editor-driven trigger only ever runs from Editor-only Python (`Editor/Scripts/`), never from the Clients-module C++ that also runs in a launcher (spec §3, §4).
- `_track_worker(...)` is mandatory on every `QThread` instance this plan creates or reuses — an untracked, GC'd `QThread` still running calls `qFatal` and aborts the whole Editor process (established constraint, `Editor/Scripts/csharp_editor_tools.py:1153-1180`).

---

### Task 1: Denylist config for the reflection backend

**Files:**
- Modify: `Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Configuration/BindingConfig.cs:16-27`
- Modify: `Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Generation/ReflectionBindingGenerator.cs:71,113-133`
- Modify: `Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Program.cs:165-187,259-281,328-345,378-379`
- Test: `Code/Tools/BindingGenerator.Tests/ReflectionGeneratorTests.cs`

**Interfaces:**
- Produces: `BindingConfig.ReflectionBackendExcludedGems` (`List<string>`, JSON key `reflectionBackendExcludedGems`, default empty list). `ReflectionBindingGenerator.Generate(string jsonPath, string outputDir, ISet<string>? includeGems = null, ISet<string>? excludeGems = null)` — new trailing optional parameter, existing callers unaffected.

- [ ] **Step 1: Write the failing test for config parsing**

Add to `Code/Tools/BindingGenerator.Tests/` a new file `BindingConfigExcludedGemsTests.cs`:

```csharp
//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System.IO;
using FluentAssertions;
using O3DESharp.BindingGenerator.Configuration;
using Xunit;

namespace O3DESharp.BindingGenerator.Tests;

public class BindingConfigExcludedGemsTests
{
    [Fact]
    public void Load_ParsesReflectionBackendExcludedGems()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
            {
                "reflectionBackendExcludedGems": ["HugeGem", "IrrelevantGem"]
            }
            """);

            var config = BindingConfigLoader.Load(path);

            config.ReflectionBackendExcludedGems.Should().BeEquivalentTo(new[] { "HugeGem", "IrrelevantGem" });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_DefaultsToEmptyList_WhenKeyAbsent()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{}");

            var config = BindingConfigLoader.Load(path);

            config.ReflectionBackendExcludedGems.Should().BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_DefaultsToEmptyList()
    {
        // BindingConfigLoader.Load gracefully returns CreateDefault() for a
        // missing file (Configuration/BindingConfigLoader.cs:62-67) - the
        // reflection backend must stay usable with zero config file at all.
        var config = BindingConfigLoader.Load(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Path.GetRandomFileName() + ".json"));

        config.ReflectionBackendExcludedGems.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Code/Tools/BindingGenerator.Tests/ --filter BindingConfigExcludedGemsTests`
Expected: FAIL to compile — `BindingConfig` has no `ReflectionBackendExcludedGems` member.

- [ ] **Step 3: Add the property to `BindingConfig`**

In `Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Configuration/BindingConfig.cs`, add a new property to the `BindingConfig` class right after `Gems` (after line 26):

```csharp
        /// <summary>
        /// Reflection backend only. Gem names to exclude from the
        /// zero-config "generate bindings for every gem" default -
        /// an opt-out list, never an opt-in one. Clang-backend gem
        /// enablement (<see cref="Gems"/>'s <c>Enabled</c> flag) is
        /// deliberately a separate property: that flag is an
        /// allowlist with the opposite default semantics, and
        /// reusing it here would give the same JSON key different
        /// meaning depending on which backend read it.
        /// </summary>
        public List<string> ReflectionBackendExcludedGems { get; set; } = new List<string>();
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Code/Tools/BindingGenerator.Tests/ --filter BindingConfigExcludedGemsTests`
Expected: PASS (3 tests)

- [ ] **Step 5: Commit**

```bash
git add Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Configuration/BindingConfig.cs Code/Tools/BindingGenerator.Tests/BindingConfigExcludedGemsTests.cs
git commit -m "Add ReflectionBackendExcludedGems to binding_config.json schema"
```

- [ ] **Step 6: Write the failing test for generator filtering**

Add to `Code/Tools/BindingGenerator.Tests/ReflectionGeneratorTests.cs`, in the same file as the existing `ReflectionGeneratorTests` class (after the `GenerationEmits_OneCsprojPerGemBucket` test, so it sits next to the other csproj-emission-related tests):

```csharp
    [Fact]
    public void Generate_ExcludeGems_OmitsDeniedGemFromOutput()
    {
        var json = """
        {
            "classes": [
                { "name": "ClassA", "type_id": "{aaaa2222-0000-0000-0000-000000000000}", "source_gem_name": "GemA", "methods": [], "properties": [] },
                { "name": "ClassB", "type_id": "{bbbb2222-0000-0000-0000-000000000000}", "source_gem_name": "GemB", "methods": [], "properties": [] }
            ],
            "ebuses": [], "global_methods": [], "global_properties": []
        }
        """;
        var jsonPath = Path.Combine(_outputDir, "reflection_data.json");
        File.WriteAllText(jsonPath, json);

        var gen = new ReflectionBindingGenerator(rootNamespace: "O3DE.Generated", verbose: false);
        var result = gen.Generate(jsonPath, _outputDir, includeGems: null, excludeGems: new HashSet<string> { "GemB" });

        result.Success.Should().BeTrue();
        File.Exists(Path.Combine(_outputDir, "GemA", "Classes", "ClassA.g.cs")).Should().BeTrue();
        Directory.Exists(Path.Combine(_outputDir, "GemB")).Should().BeFalse("GemB is denylisted and must produce no output at all");
    }
```

- [ ] **Step 7: Run test to verify it fails**

Run: `dotnet test Code/Tools/BindingGenerator.Tests/ --filter Generate_ExcludeGems_OmitsDeniedGemFromOutput`
Expected: FAIL to compile — `Generate` has no `excludeGems` parameter.

- [ ] **Step 8: Add `excludeGems` filtering to `ReflectionBindingGenerator.Generate`**

In `Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Generation/ReflectionBindingGenerator.cs`, change the method signature at line 71:

```csharp
        public ReflectionGenerationResult Generate(string jsonPath, string outputDir, ISet<string>? includeGems = null, ISet<string>? excludeGems = null)
```

Add a local helper right after the `GemBucket` local function (after line 111) and apply it to all four `.Where` clauses (lines 113-131). Replace lines 113-131 with:

```csharp
            bool Included(string sourceGemName)
            {
                var bucket = GemBucket(sourceGemName);
                if (includeGems != null && includeGems.Count > 0 && !includeGems.Contains(bucket))
                {
                    return false;
                }
                if (excludeGems != null && excludeGems.Contains(bucket))
                {
                    return false;
                }
                return true;
            }

            var classesByGem = doc.Classes
                .Where(c => Included(c.SourceGemName))
                .GroupBy(c => GemBucket(c.SourceGemName));

            var busesByGem = doc.EBuses
                .Where(b => Included(b.SourceGemName))
                .GroupBy(b => GemBucket(b.SourceGemName));

            var methodsByGem = doc.GlobalMethods
                .Where(m => Included(m.SourceGemName))
                .GroupBy(m => GemBucket(m.SourceGemName))
                .ToDictionary(g => g.Key, g => g.ToList());
            var propsByGem = doc.GlobalProperties
                .Where(p => Included(p.SourceGemName))
                .GroupBy(p => GemBucket(p.SourceGemName))
                .ToDictionary(g => g.Key, g => g.ToList());
```

- [ ] **Step 9: Run test to verify it passes**

Run: `dotnet test Code/Tools/BindingGenerator.Tests/ --filter ReflectionGeneratorTests`
Expected: PASS (all tests in the file, including the pre-existing ones — this is a refactor of the filter expressions, not a behavior change for the `includeGems`-only path)

- [ ] **Step 10: Wire the config file into the reflection CLI path**

In `Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Program.cs`:

Change the `GenerateBindingsFromReflection` signature (line 328) to accept the config path:
```csharp
        static int GenerateBindingsFromReflection(string projectPath, string[] specificGems, bool verbose, string? csharpOutputDir, string? reflectionDataOverride, string configPath)
```

Inside the method, right after the `gemFilter` block (after line 376, before `var generator = new ReflectionBindingGenerator(...)` at line 378), load the config and build the exclude set:

```csharp
                var bindingConfig = BindingConfigLoader.Load(configPath);
                ISet<string>? excludeGems = bindingConfig.ReflectionBackendExcludedGems.Count > 0
                    ? new HashSet<string>(bindingConfig.ReflectionBackendExcludedGems, StringComparer.OrdinalIgnoreCase)
                    : null;
                if (excludeGems != null && excludeGems.Count > 0)
                {
                    Console.WriteLine($"Excluded gems:  {string.Join(", ", excludeGems)}");
                }
```

Change the `generator.Generate(...)` call (line 379) to pass it through:
```csharp
                var result = generator.Generate(reflectionPath, outputDir, gemFilter, excludeGems);
```

Update both call sites that invoke `GenerateBindingsFromReflection` to pass the already-parsed `config` variable — line 181:
```csharp
                    context.ExitCode = GenerateBindingsFromReflection(project, gems, verbose, csharpOutput, reflectionData, config);
```
and line 275:
```csharp
                    context.ExitCode = GenerateBindingsFromReflection(project, gems, verbose, csharpOutput, reflectionData, config);
```
(Both call sites already have a `config` local in scope from `context.ParseResult.GetValueForOption(configOption) ?? "binding_config.json"` at lines 169 and 263 — no new option parsing needed, `--config`/`-c` already applies to the `generate` command per line 155/248.)

- [ ] **Step 11: Run the full BindingGenerator test suite**

Run: `dotnet test Code/Tools/BindingGenerator.Tests/`
Expected: PASS (no regressions — this step only threads an existing CLI option through to a code path that previously ignored it)

- [ ] **Step 12: Commit**

```bash
git add Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Generation/ReflectionBindingGenerator.cs Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Program.cs Code/Tools/BindingGenerator.Tests/ReflectionGeneratorTests.cs
git commit -m "Reflection backend: honor reflectionBackendExcludedGems from binding_config.json"
```

---

### Task 2: Consolidate per-gem `.csproj` emission into one project

**Files:**
- Modify: `Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Generation/ReflectionBindingGenerator.cs:242-260,275-387`
- Modify: `Code/Tools/BindingGenerator.Tests/ReflectionGeneratorTests.cs:351-373`

**Interfaces:**
- Consumes: `ReflectionBindingGenerator.Generate` from Task 1 (signature unchanged by this task).
- Produces: exactly one `<outputDir>/O3DESharp.GeneratedBindings.csproj` per successful `Generate()` call (when at least one gem produced output), with `AssemblyName` = `O3DESharp.GeneratedBindings`. `ReflectionGenerationResult.CsprojFilesWritten` becomes `0` or `1` (was previously "count of gem buckets").

- [ ] **Step 1: Rewrite the existing per-gem-csproj test to expect one consolidated project**

The current test `GenerationEmits_OneCsprojPerGemBucket` (`Code/Tools/BindingGenerator.Tests/ReflectionGeneratorTests.cs:351-373`) pins the old per-gem behavior and must change with it — leaving it as-is would pin a behavior this task deliberately removes. Replace that test with:

```csharp
    [Fact]
    public void GenerationEmits_OneConsolidatedCsproj_CoveringAllGemBuckets()
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

        var csprojPath = Path.Combine(dir, "O3DESharp.GeneratedBindings.csproj");
        File.Exists(csprojPath).Should().BeTrue("both gems' output must be covered by exactly one project");
        Directory.GetFiles(dir, "*.csproj", SearchOption.AllDirectories).Should().HaveCount(1,
            "there must be no per-gem csproj left over - GemA/GemA.csproj and GemB/GemB.csproj no longer get emitted");

        var csproj = File.ReadAllText(csprojPath);
        csproj.Should().Contain("<AssemblyName>O3DESharp.GeneratedBindings</AssemblyName>");
        csproj.Should().Contain("<DebugType>full</DebugType>",
            "Debug config must still use full PDB for managed-debugger attach");
        csproj.Should().Contain("DeployToBinScripts",
            "csproj must still wire the deploy-to-Bin/Scripts post-build target");

        // Both gems' generated .cs files must still exist as siblings under
        // the output root and be picked up by the SDK-style project's
        // default recursive **/*.cs compile glob - no explicit <Compile>
        // items are needed for this to work.
        File.Exists(Path.Combine(dir, "GemA", "Classes", "ClassA.g.cs")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "GemB", "Classes", "ClassB.g.cs")).Should().BeTrue();
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Code/Tools/BindingGenerator.Tests/ --filter GenerationEmits_OneConsolidatedCsproj_CoveringAllGemBuckets`
Expected: FAIL — `O3DESharp.GeneratedBindings.csproj` doesn't exist yet; `GemA/GemA.csproj` and `GemB/GemB.csproj` do, so the "exactly 1 csproj" count assertion fails too.

- [ ] **Step 3: Replace the per-gem emission loop with a single call**

In `Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Generation/ReflectionBindingGenerator.cs`, replace lines 242-260 (the "Emit a csproj for every gem bucket" block) with:

```csharp
            // Emit exactly one consolidated project covering every gem
            // bucket that produced at least one .g.cs file. A single
            // project (rather than one per gem) means one dotnet build,
            // one output DLL, and one thing for a user script project to
            // reference - see docs/superpowers/specs/2026-08-25-zero-config-
            // gem-bindings-design.md §3 for why. The per-class / per-EBus
            // files are useless on their own; they need to land in a
            // compiled DLL for Coral to load.
            var allGemKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in classesByGem) allGemKeys.Add(g.Key);
            foreach (var g in busesByGem) allGemKeys.Add(g.Key);
            allGemKeys.UnionWith(globalsGemKeys);

            int csprojFiles = 0;
            if (allGemKeys.Count > 0)
            {
                EmitProjectFile(outputDir, allGemKeys);
                csprojFiles = 1;
            }
```

- [ ] **Step 4: Change `EmitProjectFile` to emit one project at the output root**

Replace the entire `EmitProjectFile` method (lines 275-387) with:

```csharp
        /// <summary>
        /// Emit O3DESharp.GeneratedBindings.csproj at the output root,
        /// covering every gem bucket that produced at least one .g.cs
        /// file. One project, not one per gem - see the call site comment
        /// in Generate() for why. Matches the debug-friendly settings from
        /// the user-script template (DebugType=full + loose-on-disk +
        /// Debug constants), references O3DE.Core via a path relative to
        /// project root, and includes a DeployToBinScripts target so the
        /// built DLL + PDB land in &lt;Project&gt;/Bin/Scripts/ where Coral
        /// picks them up at runtime.
        ///
        /// SDK-style projects (Microsoft.NET.Sdk) recursively glob
        /// **/*.cs by default, so every gem's Classes/EBuses/Globals.g.cs
        /// under &lt;outputDir&gt;/&lt;GemName&gt;/ is picked up automatically -
        /// no explicit &lt;Compile&gt; items are needed here.
        ///
        /// The csproj path layout: &lt;output&gt;/O3DESharp.GeneratedBindings.csproj.
        /// Relative-to-project-root: ../../  (2 levels up from
        /// &lt;Project&gt;/Generated/CSharp/ - assuming the caller passed
        /// --output &lt;Project&gt;/Generated/CSharp/).
        /// </summary>
        private void EmitProjectFile(string outputDir, ISet<string> includedGemKeys)
        {
            const string assemblyName = "O3DESharp.GeneratedBindings";
            var sb = new StringBuilder();

            sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
            sb.AppendLine();
            sb.AppendLine("  <!--");
            sb.AppendLine("    Auto-generated by O3DESharp.BindingGenerator (reflection backend).");
            sb.AppendLine("    Compiles the per-gem Classes/EBuses/Globals .g.cs files under this");
            sb.AppendLine("    directory into a single assembly that scripts can reference. The");
            sb.AppendLine("    generated DLL is deployed to <Project>/Bin/Scripts/ alongside");
            sb.AppendLine("    O3DE.Core.dll so Coral picks it up at runtime.");
            sb.AppendLine("  -->");
            sb.AppendLine();
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine("    <TargetFramework>net9.0</TargetFramework>");
            sb.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
            sb.AppendLine("    <Nullable>enable</Nullable>");
            sb.AppendLine("    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>");
            sb.AppendLine("    <LangVersion>latest</LangVersion>");
            sb.AppendLine($"    <AssemblyName>{assemblyName}</AssemblyName>");
            sb.AppendLine($"    <RootNamespace>{_rootNamespace}</RootNamespace>");
            sb.AppendLine("    <Configurations>Debug;Release</Configurations>");
            sb.AppendLine("    <Platforms>AnyCPU</Platforms>");
            sb.AppendLine();
            sb.AppendLine("    <!-- Suppress CS1591 for whole assembly - generated wrappers");
            sb.AppendLine("         intentionally leave many XML doc comments empty when the");
            sb.AppendLine("         reflected source had none. -->");
            sb.AppendLine("    <NoWarn>$(NoWarn);1591</NoWarn>");
            sb.AppendLine();
            sb.AppendLine("    <!-- Deploy target. <Project>/Generated/CSharp/ is 2 directories");
            sb.AppendLine("         below the project root; ../../Bin/Scripts gets us there.");
            sb.AppendLine("         User can override per-machine. -->");
            sb.AppendLine("    <O3DEDeployPath Condition=\"'$(O3DEDeployPath)' == ''\">$(MSBuildProjectDirectory)\\..\\..\\Bin\\Scripts</O3DEDeployPath>");
            sb.AppendLine();
            sb.AppendLine("    <!-- Default PDB format for non-Debug configs. Debug overrides");
            sb.AppendLine("         to full below - external managed-debugger attach against");
            sb.AppendLine("         the embedded CoreCLR (Coral delegate-host mode) needs");
            sb.AppendLine("         full-format PDBs to handshake cleanly. -->");
            sb.AppendLine("    <DebugType>portable</DebugType>");
            sb.AppendLine("    <DebugSymbols>true</DebugSymbols>");
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine();
            sb.AppendLine("  <PropertyGroup Condition=\"'$(Configuration)' == 'Debug'\">");
            sb.AppendLine("    <Optimize>false</Optimize>");
            sb.AppendLine("    <DebugSymbols>true</DebugSymbols>");
            sb.AppendLine("    <DebugType>full</DebugType>");
            sb.AppendLine("    <PublishSingleFile>false</PublishSingleFile>");
            sb.AppendLine("    <EnableCompressionInSingleFile>false</EnableCompressionInSingleFile>");
            sb.AppendLine("    <DefineConstants>DEBUG;TRACE</DefineConstants>");
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine("  <PropertyGroup Condition=\"'$(Configuration)' == 'Release'\">");
            sb.AppendLine("    <Optimize>true</Optimize>");
            sb.AppendLine("    <DefineConstants>TRACE</DefineConstants>");
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine();
            sb.AppendLine("  <!-- Reference O3DE.Core for NativeReflection + the math types");
            sb.AppendLine("       (O3DE.Vector3 etc.) that generated wrappers use. -->");
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <Reference Include=\"O3DE.Core\">");
            sb.AppendLine("      <HintPath>$(MSBuildProjectDirectory)\\..\\..\\Bin\\Scripts\\O3DE.Core.dll</HintPath>");
            sb.AppendLine("      <Private>false</Private>");
            sb.AppendLine("    </Reference>");
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine();
            sb.AppendLine("  <!-- Auto-deploy after every Build. ContinueOnError prevents a");
            sb.AppendLine("       locked Bin/Scripts/*.dll (engine running) from failing the");
            sb.AppendLine("       IDE build entirely. -->");
            sb.AppendLine("  <Target Name=\"DeployToBinScripts\" AfterTargets=\"Build\">");
            sb.AppendLine($"    <Message Text=\"O3DESharp: deploying {assemblyName}.dll -&gt; $(O3DEDeployPath)\" Importance=\"high\"/>");
            sb.AppendLine("    <MakeDir Directories=\"$(O3DEDeployPath)\"/>");
            sb.AppendLine("    <Copy SourceFiles=\"$(TargetPath)\"");
            sb.AppendLine("          DestinationFolder=\"$(O3DEDeployPath)\"");
            sb.AppendLine("          SkipUnchangedFiles=\"true\"");
            sb.AppendLine("          ContinueOnError=\"true\"/>");
            sb.AppendLine("    <Copy SourceFiles=\"$(TargetDir)$(AssemblyName).pdb\"");
            sb.AppendLine("          DestinationFolder=\"$(O3DEDeployPath)\"");
            sb.AppendLine("          SkipUnchangedFiles=\"true\"");
            sb.AppendLine("          ContinueOnError=\"true\"");
            sb.AppendLine("          Condition=\"Exists('$(TargetDir)$(AssemblyName).pdb')\"/>");
            sb.AppendLine("  </Target>");
            sb.AppendLine();
            sb.AppendLine("</Project>");

            var csprojPath = Path.Combine(outputDir, $"{assemblyName}.csproj");
            File.WriteAllText(csprojPath, sb.ToString());
            if (_verbose)
            {
                Console.WriteLine($"  Wrote project: {Path.GetFileName(csprojPath)} (covering {includedGemKeys.Count} gem bucket(s): {string.Join(", ", includedGemKeys.OrderBy(k => k))})");
            }
        }
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Code/Tools/BindingGenerator.Tests/ --filter ReflectionGeneratorTests`
Expected: PASS (all tests in the file)

- [ ] **Step 6: Run the full BindingGenerator test suite for regressions**

Run: `dotnet test Code/Tools/BindingGenerator.Tests/`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Generation/ReflectionBindingGenerator.cs Code/Tools/BindingGenerator.Tests/ReflectionGeneratorTests.cs
git commit -m "Reflection backend: emit one consolidated GeneratedBindings.csproj instead of one per gem"
```

---

### Task 3: CMake `BuildGeneratedBindings` target

**Files:**
- Modify: `Code/CMakeLists.txt` (the existing `${gem_name}.GenerateBindings` target block, and the deploy section)
- Test: `Editor/Tests/test_build_generated_bindings_target.py`

**Interfaces:**
- Consumes: the consolidated `O3DESharp.GeneratedBindings.csproj` from Task 2, always emitted at `${BINDINGS_OUTPUT_DIR}/O3DESharp.GeneratedBindings.csproj` (existing `BINDINGS_OUTPUT_DIR` CMake variable in `Code/CMakeLists.txt`).
- Produces: `${gem_name}.BuildGeneratedBindings` CMake target; stages `bin/GeneratedBindings/O3DESharp.GeneratedBindings.{dll,pdb,deps.json}` and deploys them to `Bin/Scripts/` via `ly_add_target_files`, matching how `O3DE.Core.dll` is already deployed.

- [ ] **Step 1: Write the failing regression test**

Following the exact pattern of `Editor/Tests/test_stage_targets_ninja_safe.py` (paren-counting extraction of one `add_custom_target(...)` block), create `Editor/Tests/test_build_generated_bindings_target.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Regression guard for the BuildGeneratedBindings CMake target
(zero-config gem bindings, sub-project 1 - see
docs/superpowers/specs/2026-08-25-zero-config-gem-bindings-design.md).

Same BYPRODUCTS lesson as StageCoral/StageO3DECore
(WatchDogStudios/O3DESharp#3): add_custom_target()'s COMMAND lines have no
declared outputs of their own, so the staged DLL/PDB/deps.json need
BYPRODUCTS or a fresh-clone Ninja build fails with "no known rule to make
it" at the point ly_add_target_files() asks for them by exact path.
"""

from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
CMAKELISTS = GEM_ROOT / "Code" / "CMakeLists.txt"


def _read():
    return CMAKELISTS.read_text(encoding="utf-8")


def _custom_target_block(text, target_name):
    marker = f"add_custom_target(${{gem_name}}.{target_name}"
    start = text.index(marker)
    open_paren = text.index("(", start)
    depth = 0
    for i in range(open_paren, len(text)):
        if text[i] == "(":
            depth += 1
        elif text[i] == ")":
            depth -= 1
            if depth == 0:
                return text[open_paren + 1 : i]
    raise AssertionError(f"unterminated add_custom_target({marker}...)")


@pytest.mark.unit
def test_build_generated_bindings_target_exists_and_depends_on_generate():
    block = _custom_target_block(_read(), "BuildGeneratedBindings")
    assert "GenerateBindings" in block, (
        "BuildGeneratedBindings must DEPENDS on ${gem_name}.GenerateBindings "
        "so the consolidated csproj exists before dotnet build runs against it."
    )


@pytest.mark.unit
def test_build_generated_bindings_target_declares_byproducts():
    block = _custom_target_block(_read(), "BuildGeneratedBindings")
    assert "BYPRODUCTS" in block, (
        "Without BYPRODUCTS, Ninja has no rule for the staged DLL/PDB/deps.json "
        "on a fresh clone - see WatchDogStudios/O3DESharp#3 for the exact failure mode."
    )
    byproducts_start = block.index("BYPRODUCTS")
    byproducts_text = block[byproducts_start:]
    for filename in ("O3DESharp.GeneratedBindings.dll", "O3DESharp.GeneratedBindings.pdb", "O3DESharp.GeneratedBindings.deps.json"):
        assert filename in byproducts_text, f"BYPRODUCTS is missing {filename}"


@pytest.mark.unit
def test_build_generated_bindings_deployed_to_bin_scripts():
    text = _read()
    assert "GENERATED_BINDINGS_STAGING_DIR" in text
    ly_add_target_files_calls = text.count("ly_add_target_files")
    assert ly_add_target_files_calls >= 3, (
        "Expected at least 3 ly_add_target_files calls (Coral, O3DE.Core, "
        "and the new GeneratedBindings) - found fewer, deploy block may be missing."
    )
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest Editor/Tests/test_build_generated_bindings_target.py -v`
Expected: FAIL — `add_custom_target(${gem_name}.BuildGeneratedBindings` not found in `Code/CMakeLists.txt` (raises the `ValueError` from `text.index(marker)`).

- [ ] **Step 3: Add the `BuildGeneratedBindings` CMake target**

In `Code/CMakeLists.txt`, immediately after the closing `endif()` of the existing `if(EXISTS "${BINDING_CONFIG_FILE}") ... else() ... endif()` block that defines `${gem_name}.GenerateBindings` (the block ending around where `O3DESharp: Automatic binding generation DISABLED` messages are), add:

```cmake
    # ============================================================
    # Build + Stage the Consolidated Generated Bindings DLL
    # ============================================================
    # Zero-config gem bindings (sub-project 1 of the "zero-legwork C# for
    # gems" effort - see docs/superpowers/specs/2026-08-25-zero-config-
    # gem-bindings-design.md). GenerateBindings only emits .g.cs + one
    # consolidated csproj; this target actually builds it and stages the
    # output where the rest of the gem's deploy machinery expects it,
    # mirroring StageCoral/StageO3DECore below.
    set(GENERATED_BINDINGS_CSPROJ "${BINDINGS_OUTPUT_DIR}/O3DESharp.GeneratedBindings.csproj")
    set(GENERATED_BINDINGS_BUILD_OUTPUT "${BINDINGS_OUTPUT_DIR}/bin/Release/net9.0")
    get_property(gem_root_for_generated_bindings GLOBAL PROPERTY "@GEMROOT:${gem_name}@")
    set(GENERATED_BINDINGS_STAGING_DIR "${gem_root_for_generated_bindings}/bin/GeneratedBindings")

    add_custom_target(${gem_name}.BuildGeneratedBindings
        COMMENT "O3DESharp: Building consolidated generated-bindings DLL"
        COMMAND ${CMAKE_COMMAND} -E make_directory "${GENERATED_BINDINGS_STAGING_DIR}"
        # ContinueOnError-equivalent: if GenerateBindings produced no
        # gems (fresh clone, reflection_data.json not yet written), the
        # csproj won't exist yet. Don't fail configure/build over an
        # empty-by-construction state - just skip the build this pass.
        COMMAND ${CMAKE_COMMAND} -E echo "Checking for ${GENERATED_BINDINGS_CSPROJ}..."
        COMMAND ${CMAKE_COMMAND} -DCSPROJ=${GENERATED_BINDINGS_CSPROJ} -DDOTNET=${DOTNET_EXECUTABLE}
            -P "${CMAKE_CURRENT_LIST_DIR}/o3desharp_build_if_exists.cmake"
        COMMAND ${CMAKE_COMMAND} -DSRC=${GENERATED_BINDINGS_BUILD_OUTPUT}/O3DESharp.GeneratedBindings.dll -DDST=${GENERATED_BINDINGS_STAGING_DIR}/O3DESharp.GeneratedBindings.dll -P "${CMAKE_CURRENT_LIST_DIR}/o3desharp_copy_if_exists.cmake"
        COMMAND ${CMAKE_COMMAND} -DSRC=${GENERATED_BINDINGS_BUILD_OUTPUT}/O3DESharp.GeneratedBindings.pdb -DDST=${GENERATED_BINDINGS_STAGING_DIR}/O3DESharp.GeneratedBindings.pdb -P "${CMAKE_CURRENT_LIST_DIR}/o3desharp_copy_if_exists.cmake"
        COMMAND ${CMAKE_COMMAND} -DSRC=${GENERATED_BINDINGS_BUILD_OUTPUT}/O3DESharp.GeneratedBindings.deps.json -DDST=${GENERATED_BINDINGS_STAGING_DIR}/O3DESharp.GeneratedBindings.deps.json -P "${CMAKE_CURRENT_LIST_DIR}/o3desharp_copy_if_exists.cmake"
        # BYPRODUCTS is load-bearing on Ninja (WatchDogStudios/O3DESharp#3) -
        # same reasoning as StageCoral/StageO3DECore below.
        BYPRODUCTS
            "${GENERATED_BINDINGS_STAGING_DIR}/O3DESharp.GeneratedBindings.dll"
            "${GENERATED_BINDINGS_STAGING_DIR}/O3DESharp.GeneratedBindings.pdb"
            "${GENERATED_BINDINGS_STAGING_DIR}/O3DESharp.GeneratedBindings.deps.json"
        DEPENDS ${gem_name}.GenerateBindings
    )
    set_property(TARGET ${gem_name}.BuildGeneratedBindings PROPERTY FOLDER "${relative_o3desharp_gem_root}/Bindings")
```

Create the small helper script `Code/o3desharp_build_if_exists.cmake` (parallel to the existing `o3desharp_copy_if_exists.cmake` referenced above and already used by `StageO3DECore`):

```cmake
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
# Runs `dotnet build -c Release` on CSPROJ if it exists, using the DOTNET
# executable path passed in. No-ops (exit 0) if CSPROJ doesn't exist yet -
# same graceful-degradation shape as o3desharp_copy_if_exists.cmake, for
# the same reason: a fresh clone with no reflection_data.json yet hasn't
# produced anything for BuildGeneratedBindings to build, and that must not
# fail configure/build over an experimental zero-config feature.
if(EXISTS "${CSPROJ}")
    execute_process(
        COMMAND "${DOTNET}" build "${CSPROJ}" -c Release --nologo
        RESULT_VARIABLE _build_result
    )
    if(NOT _build_result EQUAL 0)
        message(WARNING "O3DESharp: dotnet build failed for ${CSPROJ} (exit ${_build_result})")
    endif()
else()
    message(STATUS "O3DESharp: ${CSPROJ} not found yet - launch the Editor once to produce reflection_data.json, then rebuild.")
endif()
```

- [ ] **Step 4: Wire the deploy block**

In `Code/CMakeLists.txt`, in the "Deploy Coral and O3DE.Core files to launcher targets" section (right after the existing `if(TARGET ${gem_name}.StageO3DECore) ... ly_add_target_files(...) ... endif()` block for O3DE.Core), add:

```cmake
# Deploy the consolidated generated-bindings DLL if the build target
# exists. Same first-configure caveat as StageRuntimeBundle/PublishNativeAot:
# the DLL doesn't exist until BuildGeneratedBindings has actually run once,
# so this glob can be empty on the very first configure.
if(TARGET ${gem_name}.BuildGeneratedBindings)
    file(GLOB O3DESHARP_GENERATED_BINDINGS_FILES "${GENERATED_BINDINGS_STAGING_DIR}/*")
    if(O3DESHARP_GENERATED_BINDINGS_FILES)
        ly_add_target_files(
            TARGETS ${gem_name}.Clients ${gem_name}.Servers ${gem_name}.Unified
            FILES ${O3DESHARP_GENERATED_BINDINGS_FILES}
            OUTPUT_SUBDIRECTORY "Bin/Scripts"
        )
        message(STATUS "O3DESharp: generated gem bindings will be deployed to Bin/Scripts/")
    else()
        message(STATUS "O3DESharp: generated bindings DLL not yet built at ${GENERATED_BINDINGS_STAGING_DIR} - build ${gem_name}.BuildGeneratedBindings then re-run CMake configure to deploy it")
    endif()
endif()
```

- [ ] **Step 5: Run test to verify it passes**

Run: `python -m pytest Editor/Tests/test_build_generated_bindings_target.py -v`
Expected: PASS (3 tests)

- [ ] **Step 6: Run the full Python unit suite for regressions**

Run: `python -m pytest Editor/Tests/ -q -m unit`
Expected: PASS, no regressions in existing CMake-parsing tests (`test_stage_targets_ninja_safe.py`, `test_platform_cmake_parity.py`, etc.)

- [ ] **Step 7: Commit**

```bash
git add Code/CMakeLists.txt Code/o3desharp_build_if_exists.cmake Editor/Tests/test_build_generated_bindings_target.py
git commit -m "CMake: add BuildGeneratedBindings target, deploy consolidated bindings DLL to Bin/Scripts"
```

---

### Task 4: Implicit reference in the user script `.csproj` template

**Files:**
- Modify: `Editor/Scripts/csharp_project_manager.py:201-276,646-652,1194-1235`
- Test: `Editor/Tests/test_csproj_template_generated_bindings_ref.py`

**Interfaces:**
- Consumes: `O3DESharp.GeneratedBindings.dll` at `<project>/Bin/Scripts/O3DESharp.GeneratedBindings.dll` (Task 2's `AssemblyName`, Task 3's deploy location).
- Produces: `ProjectManager._get_generated_bindings_path() -> str`; `CSPROJ_TEMPLATE` gains a second `<Reference>` item and a `{generated_bindings_path}` format placeholder.

- [ ] **Step 1: Write the failing test**

Create `Editor/Tests/test_csproj_template_generated_bindings_ref.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Regression guard: new C# script projects must implicitly reference
O3DESharp.GeneratedBindings.dll with no manual csproj edit - see
docs/superpowers/specs/2026-08-25-zero-config-gem-bindings-design.md §3.
"""

import sys
from pathlib import Path

import pytest

EDITOR_SCRIPTS = Path(__file__).resolve().parents[1] / "Scripts"
sys.path.insert(0, str(EDITOR_SCRIPTS))

import csharp_project_manager  # noqa: E402


@pytest.mark.unit
def test_csproj_template_references_generated_bindings():
    rendered = csharp_project_manager.CSPROJ_TEMPLATE.format(
        o3de_core_path=r"C:\fake\Bin\Scripts\O3DE.Core.dll",
        generated_bindings_path=r"C:\fake\Bin\Scripts\O3DESharp.GeneratedBindings.dll",
    )
    assert '<Reference Include="O3DESharp.GeneratedBindings">' in rendered
    assert r"C:\fake\Bin\Scripts\O3DESharp.GeneratedBindings.dll" in rendered
    # The reference must not be marked Private=false the way O3DE.Core's
    # HintPath reference in the *generated-bindings* csproj itself is
    # (Task 2) - user game scripts are the actual consumer and must copy
    # the DLL's dependency graph normally.


@pytest.mark.unit
def test_get_generated_bindings_path_matches_deploy_location():
    # _get_generated_bindings_path mirrors _get_o3de_core_path's shape -
    # both point at <project>/Bin/Scripts/<name>.
    manager_cls = csharp_project_manager.O3DEProjectManager
    assert hasattr(manager_cls, "_get_generated_bindings_path"), (
        "O3DEProjectManager must define _get_generated_bindings_path, "
        "the same way it defines _get_o3de_core_path"
    )
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest Editor/Tests/test_csproj_template_generated_bindings_ref.py -v`
Expected: FAIL — `CSPROJ_TEMPLATE.format(...)` raises `KeyError: 'generated_bindings_path'` (the template has no such placeholder yet), and `_get_generated_bindings_path` doesn't exist.

(Confirm the actual class name first: `grep -n "^class.*ProjectManager" Editor/Scripts/csharp_project_manager.py` — if it isn't `O3DEProjectManager`, use the exact name found and adjust the test above to match before running it.)

- [ ] **Step 3: Add the reference to the template**

In `Editor/Scripts/csharp_project_manager.py`, in the `<ItemGroup>` block of `CSPROJ_TEMPLATE` (lines 251-255), add a second `<Reference>`:

```xml
  <ItemGroup>
    <Reference Include="O3DE.Core">
      <HintPath>{o3de_core_path}</HintPath>
    </Reference>
    <Reference Include="O3DESharp.GeneratedBindings">
      <HintPath>{generated_bindings_path}</HintPath>
    </Reference>
  </ItemGroup>
```

- [ ] **Step 4: Add the path helper and thread it through `create_project`**

Add a new method right after `_get_o3de_core_path` (after line 652):

```python
    def _get_generated_bindings_path(self) -> str:
        """
        Get the path to O3DESharp.GeneratedBindings.dll for project
        references.

        Returns the deployed location in the project's Bin/Scripts folder -
        same layout as O3DE.Core.dll. The DLL may not exist yet on a fresh
        checkout (it's produced by the BuildGeneratedBindings CMake target
        or the Editor-startup auto-sync, both of which need
        reflection_data.json to exist first); referencing a path that
        doesn't exist yet is fine for MSBuild (compiles once it appears)
        and matches how O3DE.Core.dll is already referenced before it's
        necessarily been built.
        """
        return str(self.project_path / "Bin" / "Scripts" / "O3DESharp.GeneratedBindings.dll")
```

Change the `create_project` call site (lines 1231-1233):

```python
            csproj_content = CSPROJ_TEMPLATE.format(
                o3de_core_path=self.o3de_core_path,
                generated_bindings_path=self._get_generated_bindings_path(),
            )
```

- [ ] **Step 5: Run test to verify it passes**

Run: `python -m pytest Editor/Tests/test_csproj_template_generated_bindings_ref.py -v`
Expected: PASS (2 tests)

- [ ] **Step 6: Run the full Python unit suite for regressions**

Run: `python -m pytest Editor/Tests/ -q -m unit`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add Editor/Scripts/csharp_project_manager.py Editor/Tests/test_csproj_template_generated_bindings_ref.py
git commit -m "New C# script projects implicitly reference O3DESharp.GeneratedBindings.dll"
```

---

### Task 5: Extract a headless-callable generate+build sync function

**Files:**
- Modify: `Editor/Scripts/csharp_editor_tools.py` (new module-level function; refactor `_generate_bindings`, `_on_binding_generation_finished`, `_start_auto_build_after_generation`, `_on_binding_build_finished` to call it)
- Test: `Editor/Tests/test_sync_generated_bindings.py`

**Interfaces:**
- Consumes: `_BindingGenerationWorker`, `_BindingBuildWorker`, `_track_worker` (all pre-existing in this file, unchanged signatures).
- Produces: module-level `sync_generated_bindings(invoker, project_path, config, output_dir, on_log=None, on_finished=None) -> _BindingGenerationWorker` in `Editor/Scripts/csharp_editor_tools.py`. `on_log(line: str, level: str)` fires per log line. `on_finished(result: dict)` fires exactly once at the end of the whole chain with keys `{"success": bool, "stage": "generate"|"build", "message": str, "classes_generated": int, "ebuses_generated": int, "files_written": int, "failed_csprojs": list}`.

This is a refactor of existing, working code — the goal is to make the exact same generate→build chain callable without a live dialog instance (`self`), so Task 6's Editor-startup hook can trigger it. No behavior change for the existing "Generate Bindings" button.

- [ ] **Step 1: Write the failing test**

Create `Editor/Tests/test_sync_generated_bindings.py`. This test exercises the module-level function's *callback wiring and worker lifecycle*, not a real `dotnet build` (that needs a live Editor + SDK, out of scope for a unit test) — it substitutes a fake invoker/worker-free path by driving the real `_BindingGenerationWorker`/`_BindingBuildWorker` against a `subprocess`-free fake:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""sync_generated_bindings must be callable with no live Project Manager
dialog instance, so the Editor-startup auto-sync hook (Task 6) can drive
the same generate->build chain the "Generate Bindings" button already
does. See docs/superpowers/specs/2026-08-25-zero-config-gem-bindings-design.md §4.
"""

import sys
from pathlib import Path
from unittest.mock import MagicMock

import pytest

EDITOR_SCRIPTS = Path(__file__).resolve().parents[1] / "Scripts"
sys.path.insert(0, str(EDITOR_SCRIPTS))


@pytest.mark.unit
def test_sync_generated_bindings_is_a_free_function_not_a_bound_method():
    import csharp_editor_tools

    assert hasattr(csharp_editor_tools, "sync_generated_bindings"), (
        "sync_generated_bindings must exist at module scope in "
        "csharp_editor_tools.py, not as a method on the Project Manager "
        "dialog class - a headless startup hook has no dialog instance to "
        "call it on."
    )


@pytest.mark.unit
def test_sync_generated_bindings_calls_invoker_with_reflection_source():
    import csharp_editor_tools

    invoker = MagicMock()
    config = MagicMock()
    worker = csharp_editor_tools.sync_generated_bindings(
        invoker=invoker,
        project_path="/fake/project",
        config=config,
        output_dir="/fake/project/Generated/CSharp",
        on_log=lambda line, level: None,
        on_finished=lambda result: None,
    )

    # sync_generated_bindings must set config.source before handing it to
    # the worker - same requirement _generate_bindings already enforces
    # (csharp_editor_tools.py:2390) so the editor flow stays on the
    # reflection backend by default.
    assert config.source == "reflection"
    worker.wait(5000)  # let the background thread finish before test teardown
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest Editor/Tests/test_sync_generated_bindings.py -v`
Expected: FAIL — `csharp_editor_tools` has no `sync_generated_bindings` attribute.

- [ ] **Step 3: Add the module-level function**

In `Editor/Scripts/csharp_editor_tools.py`, add a new module-level function near the `_BindingGenerationWorker`/`_BindingBuildWorker` class definitions (after `_BindingBuildWorker`'s class body, before the dialog class that currently owns `_generate_bindings`):

```python
def sync_generated_bindings(invoker, project_path, config, output_dir, on_log=None, on_finished=None):
    """
    Runs the reflection-backend generate -> build chain in the background
    on a QThread, with no dependency on a live Project Manager dialog.
    Used by both the dialog's "Generate Bindings" button
    (_generate_bindings) and the Editor-startup auto-sync hook
    (csharp_editor_bootstrap.auto_sync_generated_bindings).

    Args:
        invoker: a ClangSharpInvoker (or compatible) instance whose
            generate_bindings(project_path, config, output_callback) is
            used for the generation phase.
        project_path: str, the O3DE project path.
        config: a BindingGeneratorConfig-like object; config.source is
            forced to "reflection" here regardless of its current value.
        output_dir: str, where generated .g.cs + the consolidated
            O3DESharp.GeneratedBindings.csproj land.
        on_log(line: str, level: str): called for every log line from
            either phase. Optional - defaults to a no-op.
        on_finished(result: dict): called exactly once when the whole
            chain finishes or fails, with keys:
                success: bool
                stage: "generate" or "build" (which phase the result is from)
                message: str, human-readable summary
                classes_generated: int
                ebuses_generated: int
                files_written: int
                failed_csprojs: list[str] (empty on success)
            Optional - defaults to a no-op.

    Returns the started, _track_worker()'ed _BindingGenerationWorker. Most
    callers can ignore the return value; it's exposed so a caller that
    wants to keep something disabled until the whole chain completes (like
    the dialog's Generate button) can hold a reference without needing a
    second module-level registry.
    """
    log = on_log or (lambda line, level: None)
    finished = on_finished or (lambda result: None)

    config.source = "reflection"

    def _on_generation_finished(result_obj, out_dir):
        if result_obj is None:
            log("Binding worker returned no result.", "ERROR")
            finished({
                "success": False, "stage": "generate", "message": "worker returned no result",
                "classes_generated": 0, "ebuses_generated": 0, "files_written": 0, "failed_csprojs": [],
            })
            return

        if not result_obj.success:
            log(f"Binding generation failed: {result_obj.error_message}", "ERROR")
            finished({
                "success": False, "stage": "generate", "message": result_obj.error_message,
                "classes_generated": 0, "ebuses_generated": 0, "files_written": 0, "failed_csprojs": [],
            })
            return

        log("========== Binding Generation Complete ==========", "SUCCESS")
        log(f"Classes generated: {result_obj.classes_generated}", "SUCCESS")
        log(f"EBuses generated: {result_obj.ebuses_generated}", "SUCCESS")
        for w in (result_obj.warnings or []):
            log(f"Warning: {w}", "WARNING")

        import glob
        csprojs = sorted(glob.glob(str(out_dir) + "/**/*.csproj", recursive=True))
        if not csprojs:
            log("No .csproj emitted; skipping auto-build.", "WARNING")
            finished({
                "success": True, "stage": "generate",
                "message": "generated, no csproj to build",
                "classes_generated": result_obj.classes_generated,
                "ebuses_generated": result_obj.ebuses_generated,
                "files_written": result_obj.files_written,
                "failed_csprojs": [],
            })
            return

        log(f"Auto-building {len(csprojs)} binding csproj(s)...", "INFO")
        build_worker = _BindingBuildWorker(csprojs)
        _track_worker(build_worker)
        build_worker.log_line.connect(lambda line: log(line, "INFO"))

        def _on_build_finished(success, failed_csprojs):
            if success:
                log("========== Binding Auto-Build Complete ==========", "SUCCESS")
            else:
                log(f"{len(failed_csprojs)} binding csproj(s) failed to build", "ERROR")
            finished({
                "success": success, "stage": "build",
                "message": "build complete" if success else f"{len(failed_csprojs)} csproj(s) failed",
                "classes_generated": result_obj.classes_generated,
                "ebuses_generated": result_obj.ebuses_generated,
                "files_written": result_obj.files_written,
                "failed_csprojs": failed_csprojs,
            })

        build_worker.finished_signal.connect(_on_build_finished)
        build_worker.start()

    worker = _BindingGenerationWorker(
        invoker=invoker,
        project_path=project_path,
        config=config,
        output_dir=output_dir,
    )
    _track_worker(worker)
    worker.log_line.connect(lambda line: log(line, "INFO"))
    worker.finished_signal.connect(_on_generation_finished)
    worker.start()
    return worker
```

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m pytest Editor/Tests/test_sync_generated_bindings.py -v`
Expected: PASS (2 tests). Note: `test_sync_generated_bindings_calls_invoker_with_reflection_source` starts a real `QThread` whose `run()` calls the mocked `invoker.generate_bindings(...)` — a `MagicMock()` call returns another `MagicMock`, so `_on_generation_finished` will hit the `if not result_obj.success:` branch (a `MagicMock` is truthy but `.success` is also a `MagicMock`, which is truthy) and proceed into the glob/csproj branch, finding no real csprojs under `/fake/project/...` (path doesn't exist) and calling `on_finished` with `stage: "generate"`. This is fine for this test, which only asserts `config.source` and worker liveness — it doesn't assert on `on_finished`'s payload.

- [ ] **Step 5: Refactor the dialog's existing methods to call the shared function**

In the dialog class in `Editor/Scripts/csharp_editor_tools.py`, replace the body of `_generate_bindings` from the `self._binding_worker = _BindingGenerationWorker(...)` block through `self._binding_worker.start()` (the block currently at lines 2404-2414) with a call to the shared function, keeping every UI-specific concern (button disable/enable, `self._log`, status label) as closures passed in:

```python
            self._binding_worker = sync_generated_bindings(
                invoker=ClangSharpInvoker(),
                project_path=project_path,
                config=config,
                output_dir=output_dir,
                on_log=lambda line, level: self._log(line, level),
                on_finished=lambda result: self._on_sync_generated_bindings_finished(result, output_dir),
            )
```

Delete `_on_binding_generation_finished`, `_start_auto_build_after_generation`, and `_on_binding_build_finished` (their logic now lives inside `sync_generated_bindings`) and replace them with one new UI-thread handler:

```python
    def _on_sync_generated_bindings_finished(self, result, output_dir):
        """
        UI-thread handler for sync_generated_bindings' on_finished callback,
        used by the "Generate Bindings" button. Renders the same
        success/failure dialogs the old two-stage handler chain used to,
        then re-enables the Generate button.
        """
        if hasattr(self, "generate_btn") and self.generate_btn is not None:
            self.generate_btn.setEnabled(True)
        self._binding_worker = None

        if result["success"]:
            self.binding_status_label.setText(
                f"Generated {result['classes_generated']} classes, {result['ebuses_generated']} EBuses")
            QMessageBox.information(
                self,
                "Binding Generation Complete" if result["stage"] == "generate" else "Binding Generation + Build Complete",
                f"Successfully processed C# bindings:\n\n"
                f"• Classes: {result['classes_generated']}\n"
                f"• EBuses: {result['ebuses_generated']}\n"
                f"• Files: {result['files_written']}\n\n"
                f"Output: {output_dir}\n\n{result['message']}",
            )
        else:
            self.binding_status_label.setText(f"Error: {result['message']}")
            failed_list = "\n".join(f"  • {Path(p).name}" for p in result["failed_csprojs"])
            QMessageBox.warning(
                self,
                "Binding Generation Failed" if result["stage"] == "generate" else "Binding Auto-Build Failed",
                f"{result['message']}" + (f"\n\n{failed_list}" if failed_list else ""),
            )
```

Also disable the Generate button before starting, at the same point `_generate_bindings` already does (`if hasattr(self, "generate_btn")...: self.generate_btn.setEnabled(False)`) — this line is unchanged, it stays where it already is in `_generate_bindings`, only the worker-construction block after it changes.

- [ ] **Step 6: Run the full Python unit suite for regressions**

Run: `python -m pytest Editor/Tests/ -q -m unit`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add Editor/Scripts/csharp_editor_tools.py Editor/Tests/test_sync_generated_bindings.py
git commit -m "Extract sync_generated_bindings as a headless-callable module function"
```

---

### Task 6: Auto-sync on Editor startup

**Files:**
- Modify: `Editor/Scripts/csharp_editor_bootstrap.py:1369-1399`
- Test: `Editor/Tests/test_auto_sync_startup_hook.py`

**Interfaces:**
- Consumes: `csharp_editor_tools.sync_generated_bindings` from Task 5.
- Produces: `csharp_editor_bootstrap.auto_sync_generated_bindings()`, called from `initialize_ebus_handler()` (which already runs on Editor startup per `Code/Source/Tools/O3DESharpEditorSystemComponent.cpp:284`).

- [ ] **Step 1: Write the failing test**

Create `Editor/Tests/test_auto_sync_startup_hook.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Editor-startup auto-sync must fire silently (log-only, no modal popup)
and must not raise if reflection_data.json doesn't exist yet (fresh
checkout, Editor never launched before). See
docs/superpowers/specs/2026-08-25-zero-config-gem-bindings-design.md §4.
"""

import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest

EDITOR_SCRIPTS = Path(__file__).resolve().parents[1] / "Scripts"
sys.path.insert(0, str(EDITOR_SCRIPTS))


@pytest.mark.unit
def test_auto_sync_generated_bindings_exists():
    import csharp_editor_bootstrap

    assert hasattr(csharp_editor_bootstrap, "auto_sync_generated_bindings")


@pytest.mark.unit
def test_auto_sync_generated_bindings_never_shows_a_dialog():
    """
    A QMessageBox popup on every Editor startup (as opposed to only when
    the user explicitly clicks "Generate Bindings") would be a major UX
    regression - the source of this call must be sync_generated_bindings
    with log-only callbacks, never the dialog-driven
    _on_sync_generated_bindings_finished handler.
    """
    import csharp_editor_bootstrap
    import inspect

    source = inspect.getsource(csharp_editor_bootstrap.auto_sync_generated_bindings)
    assert "QMessageBox" not in source, (
        "auto_sync_generated_bindings must not show a QMessageBox - it "
        "runs unattended on every Editor startup, not on a user-initiated click."
    )


@pytest.mark.unit
def test_auto_sync_generated_bindings_is_non_fatal_on_exception():
    import csharp_editor_bootstrap

    with patch.object(csharp_editor_bootstrap, "_import_csharp_editor_tools", side_effect=RuntimeError("boom")):
        # Must not raise - this runs inside initialize_ebus_handler()'s
        # startup path, and an uncaught exception there would break Editor
        # startup for an experimental, opt-in-by-default convenience feature.
        csharp_editor_bootstrap.auto_sync_generated_bindings()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest Editor/Tests/test_auto_sync_startup_hook.py -v`
Expected: FAIL — `csharp_editor_bootstrap` has no `auto_sync_generated_bindings` attribute.

- [ ] **Step 3: Add the function and wire it into startup**

In `Editor/Scripts/csharp_editor_bootstrap.py`, add a new function right before `initialize_ebus_handler` (before line 1369):

```python
def auto_sync_generated_bindings():
    """
    Zero-config gem bindings: on Editor startup, kick off the same
    generate -> build -> deploy chain the "Generate Bindings" button
    triggers, silently (log-only, no dialog) and in the background. See
    docs/superpowers/specs/2026-08-25-zero-config-gem-bindings-design.md.

    Non-fatal by design (matches warn_unmigrated_csharp_projects' pattern
    right below this function's only call site): an exception here must
    never break Editor startup over an experimental convenience feature.
    """
    try:
        csharp_editor_tools = _import_csharp_editor_tools()
        try:
            from csharp_binding_generator import BindingGeneratorConfig, ClangSharpInvoker
        except ImportError:
            from .csharp_binding_generator import BindingGeneratorConfig, ClangSharpInvoker
        import azlmbr.paths as _paths

        # Same azlmbr.paths.projectroot resolution already used elsewhere
        # in this file (e.g. lines 552, 917, 1060) - not a new helper.
        project_path = str(Path(_paths.projectroot))
        output_dir = str(Path(project_path) / "Generated" / "CSharp")
        config = BindingGeneratorConfig(incremental_build=True, verbose=False)

        def _on_log(line, level):
            if level in ("ERROR", "WARNING"):
                general.log(f"O3DESharp: [auto-sync bindings] {line}")

        def _on_finished(result):
            if result["success"]:
                general.log(
                    f"O3DESharp: auto-synced gem bindings "
                    f"({result['classes_generated']} classes, {result['ebuses_generated']} EBuses)")
            else:
                general.log(f"O3DESharp: gem-binding auto-sync failed: {result['message']}")

        csharp_editor_tools.sync_generated_bindings(
            invoker=ClangSharpInvoker(),
            project_path=project_path,
            config=config,
            output_dir=output_dir,
            on_log=_on_log,
            on_finished=_on_finished,
        )
    except Exception as e:  # noqa: BLE001 - non-fatal background check, same pattern as warn_unmigrated_csharp_projects
        general.log(f"O3DESharp: gem-binding auto-sync did not start: {e}")
```

Wire it into the existing startup path — in `initialize_ebus_handler()`, right after the existing `warn_unmigrated_csharp_projects()` non-fatal call (after line 1393):

```python
        try:
            auto_sync_generated_bindings()
        except Exception:  # noqa: BLE001 - non-fatal background check
            pass
```

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m pytest Editor/Tests/test_auto_sync_startup_hook.py -v`
Expected: PASS (3 tests)

- [ ] **Step 5: Run the full Python unit suite for regressions**

Run: `python -m pytest Editor/Tests/ -q -m unit`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add Editor/Scripts/csharp_editor_bootstrap.py Editor/Tests/test_auto_sync_startup_hook.py
git commit -m "Auto-sync generated gem bindings on Editor startup (silent, background)"
```

---

### Task 7: Documentation

**Files:**
- Modify: `README.md` (the "C# Binding Generation Workflow" section)
- Modify: `GENERATED_BINDINGS_GUIDE.md`

**Interfaces:** None — documentation only.

- [ ] **Step 1: Update `GENERATED_BINDINGS_GUIDE.md`**

Add a new subsection right after "1. Overview — What Gets Generated" (or the equivalent current section) explaining:
- Under the reflection backend (the default), bindings for every enabled gem are generated, built into one `O3DESharp.GeneratedBindings.dll`, deployed to `Bin/Scripts/`, and referenced by new C# script projects automatically — no manual steps.
- To exclude a gem, add its name to `reflectionBackendExcludedGems` in `binding_config.json` (create the file with just that one key if it doesn't exist yet — the reflection backend needs no other config).
- `${gem_name}.BuildGeneratedBindings` (CMake) does the same generate+build+stage from whatever `reflection_data.json` is already on disk, for CI/non-Editor builds.
- Existing per-gem-DLL instructions in this guide (multiple `.csproj` files, one per gem) described the clang backend, which is unchanged — keep those, but add a callout at the top of that section clarifying it no longer applies to the reflection backend's default output shape.

- [ ] **Step 2: Update `README.md`**

In the "C# Binding Generation Workflow" section, add a short paragraph near the top (before the "Quick start" subsection) stating that, as of this change, the reflection backend's output is automatic end-to-end (generate, build, deploy, reference) for every enabled gem, and link to `GENERATED_BINDINGS_GUIDE.md` for the exclusion-list and CMake-target details.

- [ ] **Step 3: Commit**

```bash
git add README.md GENERATED_BINDINGS_GUIDE.md
git commit -m "Docs: describe zero-config gem bindings (automatic generate/build/deploy)"
```

**Note:** the GitHub wiki mirrors of these two files (`README`, `Generated-Bindings-Guide`) are separate pages maintained outside this repo (via browser automation, not a git-tracked file) — re-syncing them is a follow-up action for whoever runs this plan, not a step in it.

---

## Plan Self-Review Notes

- **Spec coverage:** §3 trigger mechanism → Tasks 3, 6. §3 gem scope/denylist → Task 1. §3 output shape (one project) → Task 2. §3 referencing → Task 4. §4 architecture (shared step, two entry points) → Task 5 (extraction) + Tasks 3/6 (the two callers). §5 components 1-4 → Tasks 1/2 (generator), 3 (CMake), 4 (template). §6 error handling (generation/build failure non-fatal, first-run graceful degrade) → Task 3 Step 3's `o3desharp_build_if_exists.cmake`, Task 6's non-fatal wrapping. §8 testing → a test in every task.
- **Deferred/out of scope, explicitly**: retrofitting the reference into already-migrated existing user projects (`migrate_csproj_to_deploy_target` is a separate, narrower tool - spec doesn't require this); live wiki page re-sync (external to this repo's git history).
- **Type/name consistency check**: `O3DESharp.GeneratedBindings` (assembly name) is used identically in Task 2 (C# `AssemblyName`), Task 3 (CMake `BYPRODUCTS`/staging paths), and Task 4 (Python `<Reference Include=...>` + `_get_generated_bindings_path`). `sync_generated_bindings`'s `on_finished` result-dict shape (keys: `success`, `stage`, `message`, `classes_generated`, `ebuses_generated`, `files_written`, `failed_csprojs`) is defined once in Task 5 and consumed identically by Task 5's own dialog handler and Task 6's startup hook.
