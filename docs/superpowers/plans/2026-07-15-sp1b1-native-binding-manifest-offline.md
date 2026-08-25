# SP-1b-1 Native Binding Manifest — Offline Half — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce the build-time artifact that AOT's static dispatch needs — a manifest of every BehaviorContext method that can be called as a direct native trampoline, joined to its real C++ symbol, plus the generated trampolines themselves.

**Architecture:** `BehaviorContext` cannot yield a native C++ symbol at runtime (its functor type-erases the pointer inside an `AZStd::function`), so the manifest is built by two independent passes joined offline: a C++ runtime pass emits the runtime-observable half as JSON, and a libclang pass over each gem's reflection `.cpp` recovers `&C::Method` from `->Method("name", &C::Method)`. This plan implements the **offline (C#) half only** — schema, parser, join, classifier, emission — all of which is fully testable without an engine.

**Tech Stack:** C# / .NET 9, `System.Text.Json`, ClangSharp (win-x64), xUnit + FluentAssertions.

## Global Constraints

- **This is the offline half only.** The runtime half — `BindingRegistry`, load-time manifest validation, the dispatch hook, differential testing — is **SP-1b-2** and must not start here. Per the SP-1 spec it is additionally gated on SP-1a's C++ being proven in a real engine build, which has not happened.
- **Everything in this plan is verifiable in the development environment.** Unlike the SP-1a and M2 C++, there is no "not compile-verified" escape hatch — if a task cannot be demonstrated green, it is not done.
- **Source material is unfinished.** The rescued files under `Rescued/1b-native-trampoline/` (branch `feat/1b-native-trampoline-rescue`, `8a00627`) have **never been compiled**. Treat them as a strong starting point for *analysis and structure*, not as code to paste unchanged. Expect to fix them.
- **The classifier stays conservative.** v1 refuses `Overloaded`, `ReflectedViaLambda`, `OnDemandTemplateType`, `EBusAddressedById`, `UnresolvedNativeSymbol`, `UnsupportedArgStorage`, `NoNativeSideCounterpart`. Widening the bound set is a later, evidence-driven change — never a convenience during implementation.
- **A wrong binding is worse than no binding.** Every ambiguity resolves toward *not* binding. An unbound method falls back to `BehaviorMethod::Call` and costs speed; a mis-bound one calls a function pointer with the wrong signature and corrupts memory.
- Artifacts are generated on Windows and committed (SP-1 spec §6.3), so Linux/CI consume them without libclang.
- Commit messages must contain **no** Claude/Anthropic co-author or attribution trailers.

## The contract (already written, verified)

`NativeBindingManifestSchema.cs` in the rescued tree defines the C++↔C# interface and is complete. Key fields, and which side owns them:

| Field | Emitted by C++ runtime pass | Filled by this plan's offline join |
|---|---|---|
| `reflected_name`, `owning_class_*`, `is_static`, `is_const` | ✅ | — |
| `return`, `arguments` (with `storage_class`, `type_id`, `size_bytes`, `align_bytes`) | ✅ | — |
| `native_qualified_symbol` | ❌ always empty — **cannot** be recovered at runtime | ✅ from the libclang pass |
| `bindable`, `non_bindable_reason` | ❌ always `false` / `NoNativeSideCounterpart` | ✅ from the classifier |
| `binding_id` | ✅ | — (stable key used by the runtime registry) |

## File structure

| File | Responsibility |
|---|---|
| `Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Configuration/NativeBindingManifestSchema.cs` | DTOs for the manifest JSON. Pure data; no logic. |
| `.../Parsing/ReflectionCallSiteParser.cs` | libclang pass: recover `(className, reflectedName) -> &C::Method` from reflection `.cpp` files. |
| `.../Generation/NativeBindingJoin.cs` (new) | The join + classifier. Pure, no I/O — this is where correctness lives, so it must be trivially testable. |
| `.../Generation/NativeBindingGenerator.cs` | Orchestration + trampoline emission. |
| `Code/Tools/BindingGenerator.Tests/NativeBinding*.cs` | Tests for each of the above. |

---

### Task 1: Manifest schema + round-trip tests

**Files:**
- Create: `Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Configuration/NativeBindingManifestSchema.cs`
- Test: `Code/Tools/BindingGenerator.Tests/NativeBindingManifestSchemaTests.cs`

**Interfaces:**
- Produces: `NativeBindingManifestDocument` { `Methods: List<NativeBindingManifestMethod>` }, `NativeBindingManifestMethod`, `NativeBindingManifestArgument` — all with `[JsonPropertyName]` snake_case names exactly as in the rescued file.

- [ ] **Step 1: Write the failing tests**

Create `Code/Tools/BindingGenerator.Tests/NativeBindingManifestSchemaTests.cs`:

```csharp
//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System.Text.Json;
using O3DESharp.BindingGenerator.Configuration;

namespace O3DESharp.BindingGenerator.Tests;

/// <summary>
/// The manifest JSON is the contract between the C++ exporter
/// (NativeBindingManifestExporter) and this generator. The property names are
/// the wire format: renaming one silently produces a manifest where every
/// method looks unbindable, which degrades to the slow path rather than
/// failing loudly. These pin the names.
/// </summary>
public class NativeBindingManifestSchemaTests
{
    private const string SampleJson = """
    {
      "methods": [
        {
          "reflected_name": "GetLength",
          "owning_class_name": "Vector3",
          "owning_class_type_id": "{8379EB7D-01FA-4538-B64B-A6543B4BE73D}",
          "owning_class_size_bytes": 16,
          "owning_class_align_bytes": 16,
          "is_static": false,
          "is_const": true,
          "native_qualified_symbol": "",
          "return": {
            "name": "", "cpp_type_name": "float", "type_id": "{EA2C3E90-AFBE-44D4-A90D-FAAF79BAF93D}",
            "storage_class": "Value", "size_bytes": 4, "align_bytes": 4
          },
          "arguments": [],
          "bindable": false,
          "non_bindable_reason": "NoNativeSideCounterpart",
          "binding_id": "Vector3::GetLength"
        }
      ]
    }
    """;

    [Fact]
    public void Deserializes_TheCppExporterWireFormat()
    {
        var doc = JsonSerializer.Deserialize<NativeBindingManifestDocument>(SampleJson);

        doc.Should().NotBeNull();
        doc!.Methods.Should().ContainSingle();

        var m = doc.Methods[0];
        m.ReflectedName.Should().Be("GetLength");
        m.OwningClassName.Should().Be("Vector3");
        m.OwningClassSizeBytes.Should().Be(16);
        m.IsConst.Should().BeTrue();
        m.IsStatic.Should().BeFalse();
        m.BindingId.Should().Be("Vector3::GetLength");
        m.Return.CppTypeName.Should().Be("float");
        m.Return.StorageClass.Should().Be("Value");
    }

    [Fact]
    public void CppExporterLeavesTheJoinedFieldsUnset()
    {
        // The runtime BehaviorContext pass cannot recover a native symbol and
        // does not classify. Both are this generator's job; if the exporter
        // ever starts filling them, the join must be revisited.
        var doc = JsonSerializer.Deserialize<NativeBindingManifestDocument>(SampleJson)!;
        var m = doc.Methods[0];

        m.NativeQualifiedSymbol.Should().BeEmpty();
        m.Bindable.Should().BeFalse();
        m.NonBindableReason.Should().Be("NoNativeSideCounterpart");
    }

    [Fact]
    public void RoundTripsWithoutLosingFields()
    {
        var doc = JsonSerializer.Deserialize<NativeBindingManifestDocument>(SampleJson)!;
        doc.Methods[0].NativeQualifiedSymbol = "AZ::Vector3::GetLength";
        doc.Methods[0].Bindable = true;
        doc.Methods[0].NonBindableReason = "None";

        var json = JsonSerializer.Serialize(doc);
        var again = JsonSerializer.Deserialize<NativeBindingManifestDocument>(json)!;

        again.Methods[0].NativeQualifiedSymbol.Should().Be("AZ::Vector3::GetLength");
        again.Methods[0].Bindable.Should().BeTrue();
        again.Methods[0].BindingId.Should().Be("Vector3::GetLength");
    }

    [Fact]
    public void EmptyManifestIsValid()
    {
        var doc = JsonSerializer.Deserialize<NativeBindingManifestDocument>("""{"methods":[]}""");
        doc!.Methods.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Code/Tools/BindingGenerator.Tests/BindingGenerator.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~NativeBindingManifestSchemaTests"`
Expected: build failure — `NativeBindingManifestDocument` does not exist.

- [ ] **Step 3: Restore the schema**

Copy `Rescued/1b-native-trampoline/Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Configuration/NativeBindingManifestSchema.cs` (from branch `feat/1b-native-trampoline-rescue`) to `Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Configuration/NativeBindingManifestSchema.cs`.

It is pure DTOs and should need no changes — but read it and confirm the `[JsonPropertyName]` values match the test's expectations exactly before assuming so. If any differ, **the rescued file wins** (it is the side that matches the C++ exporter); update the test instead.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test Code/Tools/BindingGenerator.Tests/BindingGenerator.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~NativeBindingManifestSchemaTests"`
Expected: `Passed! - Failed: 0, Passed: 4`

- [ ] **Step 5: Full suite**

Run: `dotnet test Code/Tools/BindingGenerator.Tests/BindingGenerator.Tests.csproj -c Release --nologo`
Expected: all pass (prior count + 4).

- [ ] **Step 6: Commit**

```bash
git add Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Configuration/NativeBindingManifestSchema.cs Code/Tools/BindingGenerator.Tests/NativeBindingManifestSchemaTests.cs
git commit -m "SP-1b-1: restore the native-binding manifest schema with wire-format tests"
```

---

### Task 2: The join + classifier (pure, no I/O)

**Files:**
- Create: `Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Generation/NativeBindingJoin.cs`
- Test: `Code/Tools/BindingGenerator.Tests/NativeBindingJoinTests.cs`

**Interfaces:**
- Produces (consumed by Tasks 3, 4):
  - `record CallSiteSymbol(string ClassName, string ReflectedName, string NativeQualifiedSymbol, bool ViaLambda);`
  - `static JoinReport NativeBindingJoin.Apply(NativeBindingManifestDocument manifest, IReadOnlyCollection<CallSiteSymbol> callSites);`
  - `record JoinReport(int Total, int Bound, int Unbound, IReadOnlyDictionary<string,int> ReasonCounts);`

> This task is deliberately separated from the libclang parsing and from file I/O. The join is where a mistake silently produces a *wrong* binding, so it must be testable with plain in-memory data and no external dependencies.

- [ ] **Step 1: Write the failing tests**

Create `Code/Tools/BindingGenerator.Tests/NativeBindingJoinTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Code/Tools/BindingGenerator.Tests/BindingGenerator.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~NativeBindingJoinTests"`
Expected: build failure — `NativeBindingJoin` / `CallSiteSymbol` do not exist.

- [ ] **Step 3: Implement**

Create `Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Generation/NativeBindingJoin.cs`:

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.Collections.Generic;
using System.Linq;
using O3DESharp.BindingGenerator.Configuration;

namespace O3DESharp.BindingGenerator.Generation
{
    /// <summary>One recovered `->Method("name", &amp;C::Method)` reflection call site.</summary>
    /// <param name="ClassName">Reflected class name, matching the manifest's owning_class_name.</param>
    /// <param name="ReflectedName">The script-visible name passed to ->Method(...).</param>
    /// <param name="NativeQualifiedSymbol">e.g. "AZ::Vector3::GetLength". Empty when ViaLambda.</param>
    /// <param name="ViaLambda">True when the call site passed a lambda/wrapper rather than &amp;C::Method.</param>
    public sealed record CallSiteSymbol(
        string ClassName,
        string ReflectedName,
        string NativeQualifiedSymbol,
        bool ViaLambda);

    /// <summary>Aggregate outcome of a join, for logging and coverage assertions.</summary>
    public sealed record JoinReport(
        int Total,
        int Bound,
        int Unbound,
        IReadOnlyDictionary<string, int> ReasonCounts);

    /// <summary>
    /// Joins the C++ runtime manifest against libclang-recovered call sites and
    /// classifies what may be bound as a native trampoline.
    ///
    /// Deliberately pure: no file, no libclang, no engine. The join is the one
    /// place an error yields a WRONG binding rather than a missing one - calling
    /// a function pointer with a mismatched signature - so it has to be provable
    /// with plain in-memory data.
    ///
    /// Every ambiguity resolves toward NOT binding. An unbound method falls back
    /// to BehaviorMethod::Call and merely costs speed.
    /// </summary>
    public static class NativeBindingJoin
    {
        private const string StorageUnknown = "Unknown";

        public static JoinReport Apply(
            NativeBindingManifestDocument manifest,
            IReadOnlyCollection<CallSiteSymbol> callSites)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            ArgumentNullException.ThrowIfNull(callSites);

            // Key on (className, reflectedName). Never on reflectedName alone:
            // Transform::GetLength and Vector3::GetLength are different methods
            // that share a script name, and cross-joining them would bind a
            // plausible-looking but wrong pointer.
            var byKey = new Dictionary<(string, string), List<CallSiteSymbol>>();
            foreach (var site in callSites)
            {
                var key = (site.ClassName, site.ReflectedName);
                if (!byKey.TryGetValue(key, out var list))
                {
                    list = new List<CallSiteSymbol>();
                    byKey[key] = list;
                }
                list.Add(site);
            }

            var reasons = new Dictionary<string, int>();
            int bound = 0;

            foreach (var method in manifest.Methods)
            {
                var reason = Classify(method, byKey, out string symbol);

                if (reason == "None")
                {
                    method.NativeQualifiedSymbol = symbol;
                    method.Bindable = true;
                    method.NonBindableReason = "None";
                    bound++;
                }
                else
                {
                    method.NativeQualifiedSymbol = string.Empty;
                    method.Bindable = false;
                    method.NonBindableReason = reason;
                    reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                }
            }

            return new JoinReport(
                manifest.Methods.Count, bound, manifest.Methods.Count - bound, reasons);
        }

        private static string Classify(
            NativeBindingManifestMethod method,
            Dictionary<(string, string), List<CallSiteSymbol>> byKey,
            out string symbol)
        {
            symbol = string.Empty;

            if (!byKey.TryGetValue((method.OwningClassName, method.ReflectedName), out var sites)
                || sites.Count == 0)
            {
                return "UnresolvedNativeSymbol";
            }

            // More than one distinct &C::Method under a single script name is an
            // overload set. Choosing either is a coin flip; bind neither.
            var distinct = sites
                .Select(s => s.NativeQualifiedSymbol)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (sites.Count > 1 && distinct.Count > 1)
            {
                return "Overloaded";
            }

            if (sites.Any(s => s.ViaLambda))
            {
                return "ReflectedViaLambda";
            }

            if (string.IsNullOrEmpty(distinct[0]))
            {
                return "UnresolvedNativeSymbol";
            }

            // "void" is spelled with StorageClass Value but is not a value the
            // emitter has to unpack, so it must not trip the Unknown check.
            bool returnIsVoid = string.Equals(method.Return?.CppTypeName, "void", StringComparison.Ordinal);
            if (!returnIsVoid && method.Return?.StorageClass == StorageUnknown)
            {
                return "UnsupportedArgStorage";
            }

            if (method.Arguments.Any(a => a.StorageClass == StorageUnknown))
            {
                return "UnsupportedArgStorage";
            }

            symbol = distinct[0];
            return "None";
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test Code/Tools/BindingGenerator.Tests/BindingGenerator.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~NativeBindingJoinTests"`
Expected: `Passed! - Failed: 0, Passed: 9`

- [ ] **Step 5: Full suite**

Run: `dotnet test Code/Tools/BindingGenerator.Tests/BindingGenerator.Tests.csproj -c Release --nologo`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Generation/NativeBindingJoin.cs Code/Tools/BindingGenerator.Tests/NativeBindingJoinTests.cs
git commit -m "SP-1b-1: add the manifest/call-site join and conservative classifier

Keyed on (className, reflectedName) - never reflectedName alone, which would
cross-join Transform::GetLength onto AZ::Vector3::GetLength and produce a
plausible-looking wrong pointer. Every ambiguity resolves to not-bindable,
because an unbound method merely falls back to BehaviorMethod::Call while a
mis-bound one calls a function pointer with a mismatched signature.

Pure and I/O-free so the correctness-critical logic is provable without
libclang or an engine."
```

---

### Task 3: Restore and verify the libclang call-site parser

**Files:**
- Create: `Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Parsing/ReflectionCallSiteParser.cs`
- Test: `Code/Tools/BindingGenerator.Tests/ReflectionCallSiteParserTests.cs`

**Interfaces:**
- Consumes: `CallSiteSymbol` (Task 2).
- Produces: `ReflectionCallSiteResult ReflectionCallSiteParser.ParseFile(string path, IEnumerable<string> includePaths, IEnumerable<string> defines)`, exposing the recovered sites as `IReadOnlyList<CallSiteSymbol>`.

> The rescued `ReflectionCallSiteParser.cs` is 756 lines and has **never compiled**. Restore it incrementally: get it building first, then make the fixture tests pass. Expect real defects.

- [ ] **Step 1: Write the failing fixture tests**

Create `Code/Tools/BindingGenerator.Tests/ReflectionCallSiteParserTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Code/Tools/BindingGenerator.Tests/BindingGenerator.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~ReflectionCallSiteParserTests"`
Expected: build failure — `ReflectionCallSiteParser` does not exist.

- [ ] **Step 3: Restore the parser and get it compiling**

Copy `Rescued/1b-native-trampoline/Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Parsing/ReflectionCallSiteParser.cs` into `Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Parsing/`.

Then run `dotnet build Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/O3DESharp.BindingGenerator.csproj -c Release --nologo` and fix compile errors until clean. It has never been compiled, so errors are expected — likely candidates are ClangSharp API drift and references to types that were never written. **Do not stub out logic to make it compile**; if something references a genuinely missing type, report it rather than inventing behaviour.

- [ ] **Step 4: Adapt its output to `CallSiteSymbol`**

The parser's own `ReflectionCallSite` type may not match Task 2's `CallSiteSymbol`. Add a projection so `ReflectionCallSiteResult` exposes `IReadOnlyList<CallSiteSymbol> CallSites` — do not change `CallSiteSymbol`, which Task 2's tests pin.

- [ ] **Step 5: Make the fixture tests pass**

Run: `dotnet test Code/Tools/BindingGenerator.Tests/BindingGenerator.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~ReflectionCallSiteParserTests"`
Expected: `Passed! - Failed: 0, Passed: 3`

If the parser cannot recover the symbol from the minimal fixture, the fixture may be too unlike real O3DE reflection code (which uses `AZ::BehaviorContext` and macros). In that case make the fixture more realistic **rather than weakening the assertion** — and say so in the commit.

- [ ] **Step 6: Full suite + commit**

```bash
dotnet test Code/Tools/BindingGenerator.Tests/BindingGenerator.Tests.csproj -c Release --nologo
git add Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Parsing/ReflectionCallSiteParser.cs Code/Tools/BindingGenerator.Tests/ReflectionCallSiteParserTests.cs
git commit -m "SP-1b-1: restore the libclang reflection call-site parser with fixture tests"
```

---

### Task 4: CLI wiring — emit the joined manifest

**Files:**
- Modify: `Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Program.cs`
- Test: `Code/Tools/BindingGenerator.Tests/NativeBindingCliTests.cs`

**Interfaces:**
- Consumes: `NativeBindingJoin.Apply` (Task 2), `ReflectionCallSiteParser` (Task 3).

- [ ] **Step 1: Write the failing test**

Create `Code/Tools/BindingGenerator.Tests/NativeBindingCliTests.cs`:

```csharp
//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System.IO;
using System.Text.Json;
using O3DESharp.BindingGenerator.Configuration;
using O3DESharp.BindingGenerator.Generation;

namespace O3DESharp.BindingGenerator.Tests;

/// <summary>
/// End-to-end over the offline half: a manifest emitted by the C++ pass plus a
/// set of recovered call sites produces a joined manifest whose bound entries
/// carry real symbols. The runtime half (registry, load-time validation,
/// dispatch) is SP-1b-2 and not exercised here.
/// </summary>
public class NativeBindingCliTests
{
    [Fact]
    public void JoinedManifest_IsWrittenWithSymbolsFilledIn()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var manifestPath = Path.Combine(dir, "native_bindings.json");
            File.WriteAllText(manifestPath, """
            {"methods":[{
              "reflected_name":"GetLength","owning_class_name":"Vector3",
              "owning_class_type_id":"{0}","owning_class_size_bytes":16,"owning_class_align_bytes":16,
              "is_static":false,"is_const":true,"native_qualified_symbol":"",
              "return":{"name":"","cpp_type_name":"float","type_id":"{1}","storage_class":"Value","size_bytes":4,"align_bytes":4},
              "arguments":[],"bindable":false,"non_bindable_reason":"NoNativeSideCounterpart",
              "binding_id":"Vector3::GetLength"}]}
            """);

            var doc = JsonSerializer.Deserialize<NativeBindingManifestDocument>(
                File.ReadAllText(manifestPath))!;

            var report = NativeBindingJoin.Apply(
                doc,
                new[] { new CallSiteSymbol("Vector3", "GetLength", "AZ::Vector3::GetLength", false) });

            var outPath = Path.Combine(dir, "native_bindings.joined.json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(doc));

            var reloaded = JsonSerializer.Deserialize<NativeBindingManifestDocument>(
                File.ReadAllText(outPath))!;

            report.Bound.Should().Be(1);
            reloaded.Methods[0].Bindable.Should().BeTrue();
            reloaded.Methods[0].NativeQualifiedSymbol.Should().Be("AZ::Vector3::GetLength");
            reloaded.Methods[0].BindingId.Should().Be("Vector3::GetLength");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test Code/Tools/BindingGenerator.Tests/BindingGenerator.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~NativeBindingCliTests"`
Expected: `Passed! - Failed: 0, Passed: 1` (it exercises Tasks 1-2 only; no new production code needed yet).

- [ ] **Step 3: Add the CLI verb**

Read `Program.cs` and follow its existing `System.CommandLine` idiom exactly (see how `generate` declares options). Add a `native-bindings` command taking:
- `--manifest <path>` — the JSON emitted by the C++ runtime pass (required)
- `--reflection-sources <glob-or-dir>` — reflection `.cpp` files to parse (required)
- `--output <path>` — where to write the joined manifest (required)
- `--verbose`

It loads the manifest, parses call sites, calls `NativeBindingJoin.Apply`, writes the joined manifest, and prints the `JoinReport` — bound/unbound totals and a per-reason breakdown. **Print the report unconditionally, not behind `--verbose`:** a run that binds nothing is exactly the case a user most needs told about, and it is otherwise silent and indistinguishable from success.

- [ ] **Step 4: Verify the CLI parses**

Run: `dotnet run --project Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/O3DESharp.BindingGenerator.csproj -c Release -- native-bindings --help`
Expected: the options above are listed, no parse error.

- [ ] **Step 5: Full suite + commit**

```bash
dotnet test Code/Tools/BindingGenerator.Tests/BindingGenerator.Tests.csproj -c Release --nologo
git add Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Program.cs Code/Tools/BindingGenerator.Tests/NativeBindingCliTests.cs
git commit -m "SP-1b-1: add the native-bindings CLI verb that emits the joined manifest

Reports bound/unbound counts and a per-reason breakdown unconditionally rather
than behind --verbose: a run that binds nothing is the case most needing a
diagnostic, and is otherwise silent and indistinguishable from success."
```

---

## What is NOT in this plan

**SP-1b-2 (the runtime half)** — `BindingRegistry`, trampoline emission into generated C++, load-time validation of every manifest entry against the live `BehaviorContext`, the dispatch hook with fallback, differential testing, and binding-coverage telemetry.

It is deliberately deferred for two reasons: none of it is compile-verifiable in this environment, and the SP-1 spec gates it on SP-1a's thunk path being proven in a real engine build. Building it on an unverified foundation risks doing it twice.

Trampoline *emission* sits in SP-1b-2 rather than here because the emitted code is C++ that must compile against the real `BehaviorArgument` API — writing an emitter with no way to compile its output would be generating unverifiable text.
