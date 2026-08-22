# M3/M4 — Frozen ABI Seam + Desktop NativeAOT Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Freeze a versioned C ABI between the C++ gem and the managed scripting layer, refactor today's Coral host to sit behind it without changing editor behaviour, and then ship a second build artifact from the same codebase where the managed side is a NativeAOT native library with no CoreCLR, no hostfxr and no Coral.

**Architecture:** Two Coral-free structs of function pointers — `NativeImports` (C++ → managed, mirroring today's `O3DE.InternalCalls` field-for-field, in order) and `ManagedExports` (managed → C++: create / lifecycle / EBus dispatch / destroy / hot-reload-swap) — are exchanged once at init and carry a version field pinned by a cross-language golden contract test. A C++ `IManagedHost` interface has two implementations that differ only in *how* the structs are exchanged: `CoralHost` (editor; wraps the existing `CoralHostManager`, uploads imports via Coral's `AddInternalCall`, resolves exports through the existing `CoralNativeThunkHost`) and `NativeAotHost` (shipping; `LoadLibrary`/`dlopen` plus one exported `O3DESharp_GetManagedExports` symbol). One MSBuild property, `O3DESharpHostMode` (`Coral` default / `NativeAot`), selects the managed build config and is the only thing the source generators branch on.

**Tech Stack:** C++ (AzCore, Coral, CMake), C# / .NET 9 (`[UnmanagedCallersOnly]`, `System.Text.Json` source generation, `DynamicallyAccessedMembers`), Roslyn incremental source generators + a `DiagnosticAnalyzer` (netstandard2.0), NativeAOT (`dotnet publish -p:PublishAot=true -p:NativeLib=Shared`), xUnit + FluentAssertions, pytest.

## Global Constraints

- **Coral is desktop-only.** NativeAOT and CoreCLR-hosting are mutually exclusive **per build artifact** — two build/artifact configs from one codebase, never a runtime switch.
- **M3 is behaviour-preserving for the editor.** `CoralHostManager` is *wrapped*, never rewritten; `AddInternalCall` / `UploadInternalCalls` / the ALC hot-reload swap keep working exactly as today.
- **No redesign of BehaviorContext / EBus dispatch internals** — reserved for the separate v1.3 refactor. M4's static dispatch only covers the closed-world (compile-time-constant call site) subset.
- **No hot-reload on shipping AOT builds** — editor-only by design. `SupportsHotReload()` returns false on `NativeAotHost`; `HotReloadSwap` returns 0.
- **No trimming work in this plan.** The CoreCLR artifacts (M2's self-contained bundle, the editor build) stay untrimmed — `PublishTrimmed` is never set. **Reality check:** NativeAOT necessarily performs whole-program dead-code elimination, so M4's *shipping* artifact is trimmed by construction; there is no "untrimmed NativeAOT". The constraint therefore means: (a) the editor/Coral path is not made trimmable, (b) M4 does not add trim roots, descriptors or a trim-tuning pass, and (c) the AOT-readiness groundwork annotates what can be annotated cleanly rather than fighting the analyzer with blanket suppressions.
- **Console/mobile Mono-AOT (M5) and the v1.3 open-world dispatch coordination are out of scope.**
- **SP-1b Half B (native binding manifest / trampolines) is an orthogonal track.** M4's static dispatch tables consume the **existing `reflection_data.json`**, never SP-1b's `native_bindings.json`. Do not conflate them.
- **No C++ in this repository can be compile-verified in the development environment** (no O3DE engine SDK present). C++ tasks are authored against real, existing signatures — read the file before editing it — and every C++ commit message must state that it is not compile-verified. The maintainer verifies via a real engine build. This mirrors the SP-1a and M2 plans.
- Commit messages must contain **no** Claude/Anthropic co-author or attribution trailers.

## Verified premises (checked 2026-08-21 against `development` @ `6d5b94c`)

1. **NativeAOT publish works in this development environment.** `dotnet publish Code/Tools/RuntimeBundle/probe/probe.csproj -c Release -r win-x64 -p:PublishAot=true` produced a 1.1 MB `probe.exe`. It requires `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on `PATH` — the ILCompiler targets shell out to `vswhere.exe` and without it the native link step fails with `MSB3073 ... exited with code 123`. ILCompiler `9.0.19` restores from nuget.org. **linux-x64 NativeAOT cannot be verified here** (cross-OS native linking needs a Linux toolchain); those steps are flagged NOT PUBLISH-VERIFIED for the maintainer.
2. **TFM drift is gone.** Every project is `net9.0` except `O3DESharp.SourceGenerators.csproj` and `O3DESharp.BindingGenerator.Tasks.csproj`, which are correctly `netstandard2.0` (Roslyn component / MSBuild task). The prior audit finding no longer holds; **no TFM-fix task is needed**.
3. **The hardcoded `F:\o3de\...` absolute paths are gone.** `Assets/Scripts/O3DESharp/Metadata.g.cs` now emits engine-relative `SourceFile` values (`Gems/O3DESharp/Code/...`); `MetadataGenerator.cs:139` documents this as "Defect #1 from the 2026-05-15 audit". `git grep -n 'F:[/\\]'` over `Assets/` and `Code/` returns only two explanatory comments. **No path-fix task is needed** — but re-run `git grep -in "F:[/\\\\]" Assets Code` before Task 18 and stop if it regresses, because a NativeAOT publish would otherwise bake a maintainer-machine-only path into the shipping artifact.
4. **Baselines:** `O3DE.Core.Tests` = 61 passing. `O3DE.Core.csproj` builds clean (0 errors, 19 pre-existing CS warnings). With `-p:IsAotCompatible=true` it builds with **exactly 14 IL warnings** in 3 files: `NativeReflection.cs:542,583` (IL2026 + IL3050 ×2), `ExposedProperty.cs:91,99` (IL2075 ×4), `HotReloadManager.cs:180,220,230,240` (IL2075, IL2026, IL2072, IL2075). Tasks 7-10 close exactly those.
5. `pytest` is **not installed** in this environment. Install it (`python -m pip install pytest`) before the Python tasks; the repo already documents this in `Editor/Tests/README.md`.

## The frozen ABI (defined once here; every task below refers back to this)

`NativeImports` v1 carries **exactly** the 47 pointers of `O3DE.InternalCalls` (`Assets/Scripts/O3DE.Core/InternalCalls.cs:30-106`), in declaration order. `O3DE.Reflection.ReflectionInternalCalls` is registered separately by `GenericDispatcher`, is **not** part of ABI v1, and is deferred to v2 — that is what the version field is for.

`ManagedExports` v1 carries exactly five pointers, in this order and with these signatures (UTF-8 `byte*` strings, caller-provided output buffers — no allocation ownership crosses the seam, so no `Free` export is needed):

| Field | C# thunk signature | Meaning |
|---|---|---|
| `CreateInstance` | `delegate* unmanaged<byte*, int>` | UTF-8 type name → handle; 0 = failure |
| `InvokeLifecycle` | `delegate* unmanaged<int, int, float, int>` | handle, `LifecycleId`, arg → 1 dispatched / 0 not |
| `DispatchEBusEvent` | `delegate* unmanaged<long, byte*, byte*, byte*, int, int>` | token, eventName, argsJson, outBuf, outCap → bytes needed, or -1 on error |
| `DestroyInstance` | `delegate* unmanaged<int, void>` | handle |
| `HotReloadSwap` | `delegate* unmanaged<int>` | 1 if the managed side prepared for an ALC swap, 0 if unsupported (shipping AOT) |

---

## Milestone M3 — Frozen ABI seam + build-mode split

### Task 1: Managed ABI structs and version

**Files:**
- Create: `Assets/Scripts/O3DE.Core/Interop/HostAbi.cs`
- Modify: `Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj:36-47`
- Test: `Assets/Scripts/O3DE.Core.Tests/Interop/HostAbiLayoutTests.cs`

**Interfaces:**
- Produces (consumed by Tasks 2, 5, 6, 11, 12, 14):
  - `const uint O3DE.Interop.HostAbi.Version = 1`
  - `struct O3DE.Interop.NativeImports` — `uint Version` followed by 47 `IntPtr` fields named exactly as `O3DE.InternalCalls`' fields, in the same order
  - `struct O3DE.Interop.ManagedExports` — `uint Version` followed by `CreateInstance`, `InvokeLifecycle`, `DispatchEBusEvent`, `DestroyInstance`, `HotReloadSwap`

> **Why `IntPtr` and not `delegate* unmanaged<...>`:** the struct has to be Coral-free (it must compile in `O3DE.Core.Tests`, which stubs out `Coral.Managed.Interop`) and it has to be describable in a plain C++ header as `void*`. The typed signatures live on the generated thunks (Task 6), not in the struct.

- [ ] **Step 1: Write the failing test**

Create `Assets/Scripts/O3DE.Core.Tests/Interop/HostAbiLayoutTests.cs`:

```csharp
//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using System.Runtime.InteropServices;
using O3DE.Interop;

namespace O3DE.Core.Tests.Interop;

/// <summary>
/// NativeImports / ManagedExports are the whole C++ <-> managed boundary.
/// Three artifacts must agree on their layout: this C# declaration, the C++
/// mirror in Code/Source/Scripting/HostAbi.h, and whatever the shipping
/// NativeAOT image was built from. A field inserted on one side and not the
/// other silently reinterprets every pointer after it - which is memory
/// corruption, not a compile error. These pin the managed side; the
/// cross-language field-order check is Editor/Tests/test_host_abi_contract.py.
/// </summary>
public class HostAbiLayoutTests
{
    // 47 = the number of delegate* unmanaged fields on O3DE.InternalCalls
    // (InternalCalls.cs:30-106). ReflectionInternalCalls is registered
    // separately by GenericDispatcher and is deliberately NOT part of ABI v1.
    private const int NativeImportCount = 47;
    private const int ManagedExportCount = 5;

    [Fact]
    public void Version_IsOne()
    {
        HostAbi.Version.Should().Be(1u,
            "the version field is what lets ABI v2 add ReflectionInternalCalls without silent misreads");
    }

    [Fact]
    public void NativeImports_IsBlittableAndPointerSized()
    {
        // uint Version is followed by pointers, so it is padded up to
        // pointer alignment: total == (1 + N) * IntPtr.Size on both 32- and
        // 64-bit. Stating it that way keeps the assertion arch-agnostic.
        Marshal.SizeOf<NativeImports>().Should().Be((1 + NativeImportCount) * IntPtr.Size);
    }

    [Fact]
    public void ManagedExports_IsBlittableAndPointerSized()
    {
        Marshal.SizeOf<ManagedExports>().Should().Be((1 + ManagedExportCount) * IntPtr.Size);
    }

    [Fact]
    public void NativeImports_FirstAndLastFieldsAreAtTheExpectedOffsets()
    {
        // Pins both ends of the struct: Log_Info is the first pointer after
        // the version word, Component_HasComponent is the last one.
        Marshal.OffsetOf<NativeImports>(nameof(NativeImports.Log_Info))
            .Should().Be(new IntPtr(IntPtr.Size));
        Marshal.OffsetOf<NativeImports>(nameof(NativeImports.Component_HasComponent))
            .Should().Be(new IntPtr(NativeImportCount * IntPtr.Size));
    }

    [Fact]
    public void ManagedExports_FieldsAreInTheFrozenOrder()
    {
        Marshal.OffsetOf<ManagedExports>(nameof(ManagedExports.CreateInstance))
            .Should().Be(new IntPtr(1 * IntPtr.Size));
        Marshal.OffsetOf<ManagedExports>(nameof(ManagedExports.InvokeLifecycle))
            .Should().Be(new IntPtr(2 * IntPtr.Size));
        Marshal.OffsetOf<ManagedExports>(nameof(ManagedExports.DispatchEBusEvent))
            .Should().Be(new IntPtr(3 * IntPtr.Size));
        Marshal.OffsetOf<ManagedExports>(nameof(ManagedExports.DestroyInstance))
            .Should().Be(new IntPtr(4 * IntPtr.Size));
        Marshal.OffsetOf<ManagedExports>(nameof(ManagedExports.HotReloadSwap))
            .Should().Be(new IntPtr(5 * IntPtr.Size));
    }

    [Fact]
    public void DefaultConstructedStructs_CarryNoVersion()
    {
        // A zero-initialised struct must NOT look like a valid v1 struct -
        // the host checks Version before trusting any pointer in it.
        default(NativeImports).Version.Should().Be(0u);
        default(ManagedExports).Version.Should().Be(0u);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~HostAbiLayoutTests"`
Expected: build failure — `error CS0246: The type or namespace name 'HostAbi' could not be found` (and the same for `NativeImports` / `ManagedExports`).

- [ ] **Step 3: Write the ABI structs**

Create `Assets/Scripts/O3DE.Core/Interop/HostAbi.cs`:

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.Runtime.InteropServices;

namespace O3DE.Interop
{
    /// <summary>
    /// Version and shape of the frozen C++ <-> managed ABI.
    ///
    /// The editor (CoreCLR + Coral) and the shipping desktop build (NativeAOT)
    /// are two artifacts built from one codebase. They differ only in HOW the
    /// two structs below are exchanged - Coral uploads NativeImports by name
    /// and the host resolves exports through CoralNativeThunkHost; the
    /// NativeAOT image hands both across in one exported call. The struct
    /// SHAPES are identical in every build, which is what makes one C#
    /// codebase and one C++ integration serve both.
    ///
    /// The C++ mirror is Code/Source/Scripting/HostAbi.h. Editor/Tests/
    /// test_host_abi_contract.py fails the build if the two drift.
    /// </summary>
    public static class HostAbi
    {
        /// <summary>
        /// Bumped whenever a field is added, removed or reordered in either
        /// struct. Host, editor build and shipping build must agree: a host
        /// that reads a version it does not recognise must refuse to run
        /// rather than reinterpret pointers.
        /// </summary>
        public const uint Version = 1;
    }

    /// <summary>
    /// Function pointers C++ exposes to managed code. v1 mirrors
    /// O3DE.InternalCalls (InternalCalls.cs) field-for-field, in declaration
    /// order - that ordering IS the ABI.
    ///
    /// O3DE.Reflection.ReflectionInternalCalls is registered separately by
    /// GenericDispatcher and is deliberately NOT part of v1. Adding it is an
    /// ABI v2 change, which is exactly what the Version field exists for.
    ///
    /// Fields are IntPtr rather than delegate* unmanaged<...> so this
    /// file stays free of Coral.Managed.Interop types (NativeString, Bool32)
    /// and can be described in plain C++ as void*. The typed signatures live
    /// on the call sites, not in the transport struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NativeImports
    {
        /// <summary>Must equal <see cref="HostAbi.Version"/>. 0 means "not populated".</summary>
        public uint Version;

        // ============================================================
        // Logging
        // ============================================================
        public IntPtr Log_Info;
        public IntPtr Log_Warning;
        public IntPtr Log_Error;

        // ============================================================
        // Entity
        // ============================================================
        public IntPtr Entity_IsValid;
        public IntPtr Entity_GetName;
        public IntPtr Entity_SetName;
        public IntPtr Entity_IsActive;
        public IntPtr Entity_Activate;
        public IntPtr Entity_Deactivate;
        public IntPtr Entity_Destroy;
        public IntPtr Entity_FindByName;
        public IntPtr Entity_GetChildCount;
        public IntPtr Entity_GetChildAtIndex;
        public IntPtr Entity_GetChildren;

        // ============================================================
        // Transform
        // ============================================================
        public IntPtr Transform_GetWorldPosition;
        public IntPtr Transform_SetWorldPosition;
        public IntPtr Transform_GetLocalPosition;
        public IntPtr Transform_SetLocalPosition;
        public IntPtr Transform_GetWorldRotation;
        public IntPtr Transform_SetWorldRotation;
        public IntPtr Transform_GetWorldRotationEuler;
        public IntPtr Transform_SetWorldRotationEuler;
        public IntPtr Transform_GetLocalScale;
        public IntPtr Transform_SetLocalScale;
        public IntPtr Transform_GetLocalUniformScale;
        public IntPtr Transform_SetLocalUniformScale;
        public IntPtr Transform_GetForward;
        public IntPtr Transform_GetRight;
        public IntPtr Transform_GetUp;
        public IntPtr Transform_GetParentId;
        public IntPtr Transform_SetParent;

        // ============================================================
        // Input
        // ============================================================
        public IntPtr Input_IsKeyDown;
        public IntPtr Input_IsKeyPressed;
        public IntPtr Input_IsKeyReleased;
        public IntPtr Input_IsMouseButtonDown;
        public IntPtr Input_IsMouseButtonPressed;
        public IntPtr Input_IsMouseButtonReleased;
        public IntPtr Input_GetMousePosition;
        public IntPtr Input_GetMouseDelta;
        public IntPtr Input_GetAxis;

        // ============================================================
        // Time
        // ============================================================
        public IntPtr Time_GetDeltaTime;
        public IntPtr Time_GetTotalTime;
        public IntPtr Time_GetTimeScale;
        public IntPtr Time_SetTimeScale;
        public IntPtr Time_GetFrameCount;

        // ============================================================
        // Physics
        // ============================================================
        public IntPtr Physics_Raycast;

        // ============================================================
        // Component
        // ============================================================
        public IntPtr Component_HasComponent;
    }

    /// <summary>
    /// Function pointers managed code exposes to C++. Every field is an
    /// [UnmanagedCallersOnly] static emitted by HostExportsGenerator into
    /// ManagedExports.g.cs.
    ///
    /// Signatures (frozen - the generator and the C++ host both hard-code them):
    ///   CreateInstance    delegate* unmanaged<byte*, int>
    ///   InvokeLifecycle   delegate* unmanaged<int, int, float, int>
    ///   DispatchEBusEvent delegate* unmanaged<long, byte*, byte*, byte*, int, int>
    ///   DestroyInstance   delegate* unmanaged<int, void>
    ///   HotReloadSwap     delegate* unmanaged<int>
    ///
    /// Strings are UTF-8 and results are written into a caller-supplied
    /// buffer (snprintf-style: the return value is the number of bytes the
    /// result needs, so a short buffer is a retry rather than a truncation).
    /// That deliberately keeps allocation ownership from crossing the seam,
    /// so no Free export is needed and the struct stays at five fields.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ManagedExports
    {
        /// <summary>Must equal <see cref="HostAbi.Version"/>. 0 means "not populated".</summary>
        public uint Version;

        public IntPtr CreateInstance;
        public IntPtr InvokeLifecycle;
        public IntPtr DispatchEBusEvent;
        public IntPtr DestroyInstance;

        /// <summary>
        /// Editor-only. Shipping AOT thunks return 0 and the host reports
        /// SupportsHotReload() == false; there is no ALC to swap.
        /// </summary>
        public IntPtr HotReloadSwap;
    }
}
```

- [ ] **Step 4: Link the new file into the test project**

`O3DE.Core.Tests` cannot reference `O3DE.Core.dll` (see the comment at `O3DE.Core.Tests.csproj:29-35`); it compiles the pure-managed files under test directly. Add to the `<ItemGroup>` at `Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj:36-47`, after the `ScriptComponentBridge.cs` line:

```xml
    <Compile Include="..\O3DE.Core\Interop\HostAbi.cs" Link="O3DE.Core\Interop\HostAbi.cs" />
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~HostAbiLayoutTests"`
Expected: `Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6`

- [ ] **Step 6: Run the full managed suite**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo`
Expected: `Passed!  - Failed:     0, Passed:    67, Skipped:     0, Total:    67` (61 baseline + 6)

- [ ] **Step 7: Commit**

```bash
git add Assets/Scripts/O3DE.Core/Interop/HostAbi.cs Assets/Scripts/O3DE.Core.Tests/Interop/HostAbiLayoutTests.cs Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj
git commit -m "M3: define the versioned NativeImports/ManagedExports ABI structs

The editor and the shipping desktop build are two artifacts from one
codebase; they differ only in how these two structs are exchanged, never in
the structs themselves. NativeImports v1 mirrors O3DE.InternalCalls
field-for-field in declaration order - that order IS the ABI, so a field
inserted on one side reinterprets every pointer after it rather than failing
to compile.

Fields are IntPtr, not delegate* unmanaged<...>, so the file stays free of
Coral.Managed.Interop types and can be mirrored in plain C++ as void*.
ReflectionInternalCalls is registered separately by GenericDispatcher and is
deliberately left out of v1 - that is what the version field is for."
```

---

### Task 2: C++ ABI mirror and the golden cross-language contract test

**Files:**
- Create: `Code/Source/Scripting/HostAbi.h`
- Modify: `Code/o3desharp_private_files.cmake:20-28`
- Test: `Editor/Tests/test_host_abi_contract.py`

**Interfaces:**
- Consumes: `O3DE.Interop.HostAbi.Version`, `NativeImports`, `ManagedExports` (Task 1).
- Produces (consumed by Tasks 11, 12, 13): `O3DESharp::Abi::HostAbiVersion`, `O3DESharp::Abi::NativeImports`, `O3DESharp::Abi::ManagedExports`.

> The C++ side cannot be compiled here, so the contract test is a **source-level** parity check: it parses the field lists out of `InternalCalls.cs`, `HostAbi.cs` and `HostAbi.h` and asserts all three agree in name and order, plus that both version constants read `1`. That catches exactly the drift class that has no compile-time signal, and it runs anywhere Python does.

- [ ] **Step 1: Write the failing test**

Create `Editor/Tests/test_host_abi_contract.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Golden contract test for the frozen C++ <-> managed ABI.

NativeImports and ManagedExports are declared three times: in C#
(Assets/Scripts/O3DE.Core/Interop/HostAbi.cs), in C++
(Code/Source/Scripting/HostAbi.h), and implicitly by O3DE.InternalCalls,
whose field order NativeImports v1 mirrors. Nothing in either toolchain
fails when they drift - a field inserted on one side just reinterprets
every pointer after it, which is memory corruption at runtime.

This test parses all three and asserts they agree in name AND order.
"""

import re
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
INTERNAL_CALLS_CS = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "InternalCalls.cs"
HOST_ABI_CS = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "Interop" / "HostAbi.cs"
HOST_ABI_H = GEM_ROOT / "Code" / "Source" / "Scripting" / "HostAbi.h"

MANAGED_EXPORT_ORDER = [
    "CreateInstance",
    "InvokeLifecycle",
    "DispatchEBusEvent",
    "DestroyInstance",
    "HotReloadSwap",
]


def _read(path):
    assert path.is_file(), f"{path} is missing; the ABI is declared in three places and all three must exist."
    return path.read_text(encoding="utf-8")


def _extract_block(text, header_pattern, source_name):
    """Return the text between the first '{' after header_pattern and its
    matching '}'. Brace-counting rather than a regex because the structs
    contain no nested braces but do contain doc-comments with braces."""
    m = re.search(header_pattern, text)
    assert m, f"could not find {header_pattern!r} in {source_name}"
    start = text.index("{", m.end())
    depth = 0
    for i in range(start, len(text)):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return text[start + 1 : i]
    raise AssertionError(f"unterminated struct body for {header_pattern!r} in {source_name}")


def _csharp_internal_call_fields():
    text = _read(INTERNAL_CALLS_CS)
    return re.findall(r"internal\s+static\s+delegate\*\s+unmanaged<[^>]*>\s+(\w+)\s*;", text)


def _csharp_struct_fields(struct_name):
    body = _extract_block(_read(HOST_ABI_CS), rf"struct\s+{struct_name}\b", str(HOST_ABI_CS))
    return re.findall(r"public\s+IntPtr\s+(\w+)\s*;", body)


def _cpp_struct_fields(struct_name):
    body = _extract_block(_read(HOST_ABI_H), rf"struct\s+{struct_name}\b", str(HOST_ABI_H))
    return re.findall(r"void\*\s+(\w+)\s*;", body)


@pytest.mark.unit
def test_native_imports_mirrors_internal_calls_exactly():
    internal_calls = _csharp_internal_call_fields()
    assert len(internal_calls) == 47, (
        f"O3DE.InternalCalls declares {len(internal_calls)} function pointers, not 47. "
        "Adding one is an ABI change: mirror it in BOTH HostAbi.cs and HostAbi.h and "
        "bump HostAbi.Version / HostAbiVersion together."
    )
    assert _csharp_struct_fields("NativeImports") == internal_calls, (
        "NativeImports (C#) must mirror O3DE.InternalCalls field-for-field, in order."
    )


@pytest.mark.unit
def test_native_imports_cpp_matches_csharp():
    assert _cpp_struct_fields("NativeImports") == _csharp_struct_fields("NativeImports"), (
        "Code/Source/Scripting/HostAbi.h and Interop/HostAbi.cs disagree on NativeImports. "
        "Field ORDER is the ABI - a mismatch reinterprets every pointer after the first "
        "divergence, and neither compiler can see it."
    )


@pytest.mark.unit
def test_managed_exports_is_the_frozen_five_in_both_languages():
    assert _csharp_struct_fields("ManagedExports") == MANAGED_EXPORT_ORDER
    assert _cpp_struct_fields("ManagedExports") == MANAGED_EXPORT_ORDER


@pytest.mark.unit
def test_version_constants_agree():
    cs = re.search(r"public\s+const\s+uint\s+Version\s*=\s*(\d+)\s*;", _read(HOST_ABI_CS))
    cpp = re.search(r"HostAbiVersion\s*=\s*(\d+)\s*;", _read(HOST_ABI_H))
    assert cs, "HostAbi.cs must declare 'public const uint Version = N;'"
    assert cpp, "HostAbi.h must declare 'HostAbiVersion = N;'"
    assert cs.group(1) == cpp.group(1), (
        f"ABI version mismatch: C# says {cs.group(1)}, C++ says {cpp.group(1)}. "
        "Host, editor build and shipping build must agree on one number."
    )


@pytest.mark.unit
def test_cpp_header_is_in_the_build_file_list():
    files_cmake = GEM_ROOT / "Code" / "o3desharp_private_files.cmake"
    assert "Source/Scripting/HostAbi.h" in files_cmake.read_text(encoding="utf-8"), (
        "HostAbi.h must be listed in o3desharp_private_files.cmake or it is invisible "
        "to the IDE and to any generated-file tooling that walks that list."
    )
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest Editor/Tests/test_host_abi_contract.py -q`
Expected: 4 failures with `AssertionError: .../Code/Source/Scripting/HostAbi.h is missing; the ABI is declared in three places and all three must exist.` plus `test_cpp_header_is_in_the_build_file_list` failing. (`test_native_imports_mirrors_internal_calls_exactly` passes already — it only reads the two C# files Task 1 produced.)
If `pytest` is not installed: `python -m pip install pytest` first (see `Editor/Tests/README.md`).

- [ ] **Step 3: Write the C++ mirror**

Create `Code/Source/Scripting/HostAbi.h`:

```cpp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/base.h>

namespace O3DESharp::Abi
{
    //! Version of the frozen C++ <-> managed ABI. MUST equal
    //! O3DE.Interop.HostAbi.Version in Assets/Scripts/O3DE.Core/Interop/HostAbi.cs.
    //! Editor/Tests/test_host_abi_contract.py fails if the two drift.
    //!
    //! Bump only when a field is added, removed or reordered in either struct
    //! below. A host that reads an unrecognised version must refuse to run
    //! rather than reinterpret pointers.
    inline constexpr AZ::u32 HostAbiVersion = 1;

    //! Function pointers C++ exposes to managed code.
    //!
    //! v1 mirrors O3DE.InternalCalls (Assets/Scripts/O3DE.Core/InternalCalls.cs)
    //! field-for-field, in declaration order. That ordering IS the ABI: insert a
    //! field here without inserting it there and every pointer after it is
    //! reinterpreted, with no diagnostic from either compiler.
    //!
    //! O3DE.Reflection.ReflectionInternalCalls is registered separately by
    //! GenericDispatcher and is deliberately NOT part of v1.
    //!
    //! Under Coral (editor) this struct is populated by
    //! ScriptBindings::MakeNativeImports and is descriptive - the actual
    //! transport is still assembly->AddInternalCall / UploadInternalCalls, and
    //! nothing about that path changes. Under NativeAOT it is the sole
    //! transport, handed to the managed side in one exported call.
    struct NativeImports
    {
        //! Must equal HostAbiVersion. 0 means "not populated".
        AZ::u32 version;

        // ============================================================
        // Logging
        // ============================================================
        void* Log_Info;
        void* Log_Warning;
        void* Log_Error;

        // ============================================================
        // Entity
        // ============================================================
        void* Entity_IsValid;
        void* Entity_GetName;
        void* Entity_SetName;
        void* Entity_IsActive;
        void* Entity_Activate;
        void* Entity_Deactivate;
        void* Entity_Destroy;
        void* Entity_FindByName;
        void* Entity_GetChildCount;
        void* Entity_GetChildAtIndex;
        void* Entity_GetChildren;

        // ============================================================
        // Transform
        // ============================================================
        void* Transform_GetWorldPosition;
        void* Transform_SetWorldPosition;
        void* Transform_GetLocalPosition;
        void* Transform_SetLocalPosition;
        void* Transform_GetWorldRotation;
        void* Transform_SetWorldRotation;
        void* Transform_GetWorldRotationEuler;
        void* Transform_SetWorldRotationEuler;
        void* Transform_GetLocalScale;
        void* Transform_SetLocalScale;
        void* Transform_GetLocalUniformScale;
        void* Transform_SetLocalUniformScale;
        void* Transform_GetForward;
        void* Transform_GetRight;
        void* Transform_GetUp;
        void* Transform_GetParentId;
        void* Transform_SetParent;

        // ============================================================
        // Input
        // ============================================================
        void* Input_IsKeyDown;
        void* Input_IsKeyPressed;
        void* Input_IsKeyReleased;
        void* Input_IsMouseButtonDown;
        void* Input_IsMouseButtonPressed;
        void* Input_IsMouseButtonReleased;
        void* Input_GetMousePosition;
        void* Input_GetMouseDelta;
        void* Input_GetAxis;

        // ============================================================
        // Time
        // ============================================================
        void* Time_GetDeltaTime;
        void* Time_GetTotalTime;
        void* Time_GetTimeScale;
        void* Time_SetTimeScale;
        void* Time_GetFrameCount;

        // ============================================================
        // Physics
        // ============================================================
        void* Physics_Raycast;

        // ============================================================
        // Component
        // ============================================================
        void* Component_HasComponent;
    };

    //! Function pointers managed code exposes to C++. Frozen signatures:
    //!
    //!   CreateInstance    int  (*)(const char* utf8TypeName)
    //!   InvokeLifecycle   int  (*)(int handle, int lifecycleId, float arg)
    //!   DispatchEBusEvent int  (*)(AZ::s64 token, const char* utf8EventName,
    //!                             const char* utf8ArgsJson,
    //!                             char* outBuffer, int outCapacity)
    //!   DestroyInstance   void (*)(int handle)
    //!   HotReloadSwap     int  (*)()
    //!
    //! Strings are UTF-8; DispatchEBusEvent writes into a caller-supplied
    //! buffer and returns the number of bytes the result needs (snprintf
    //! semantics), or -1 on error. No allocation ownership crosses the seam,
    //! so there is no Free export and the struct stays at five fields.
    struct ManagedExports
    {
        //! Must equal HostAbiVersion. 0 means "not populated".
        AZ::u32 version;

        void* CreateInstance;
        void* InvokeLifecycle;
        void* DispatchEBusEvent;
        void* DestroyInstance;

        //! Editor-only. The shipping NativeAOT thunk returns 0 and
        //! NativeAotHost::SupportsHotReload() reports false - there is no
        //! AssemblyLoadContext to swap.
        void* HotReloadSwap;
    };

    // The C# side asserts the same two identities in
    // HostAbiLayoutTests.NativeImports_IsBlittableAndPointerSized. A uint
    // followed by pointers pads up to pointer alignment on both 32- and
    // 64-bit, so (1 + N) * sizeof(void*) holds on both.
    static_assert(
        sizeof(NativeImports) == (1 + 47) * sizeof(void*),
        "NativeImports layout drifted from O3DE.Interop.NativeImports - see HostAbi.cs");
    static_assert(
        sizeof(ManagedExports) == (1 + 5) * sizeof(void*),
        "ManagedExports layout drifted from O3DE.Interop.ManagedExports - see HostAbi.cs");
} // namespace O3DESharp::Abi
```

- [ ] **Step 4: Add the header to the build file list**

In `Code/o3desharp_private_files.cmake`, in the `# C# Scripting Support via Coral` block (lines 20-28), add the header alongside its neighbours:

```cmake
    Source/Scripting/HostAbi.h
```

- [ ] **Step 5: Run test to verify it passes**

Run: `python -m pytest Editor/Tests/test_host_abi_contract.py -q`
Expected: `5 passed`

- [ ] **Step 6: Commit**

```bash
git add Code/Source/Scripting/HostAbi.h Code/o3desharp_private_files.cmake Editor/Tests/test_host_abi_contract.py
git commit -m "M3: mirror the ABI structs in C++ and pin them with a golden contract test

The two structs are declared three times - InternalCalls.cs, HostAbi.cs and
HostAbi.h - and nothing in either toolchain fails when they drift: a field
inserted on one side silently reinterprets every pointer after it. The new
pytest parses all three and asserts they agree in name and order, plus that
both version constants read the same number.

The C++ static_asserts cover size on the maintainer's real build; the source
parity check covers ordering, which sizeof cannot see.

NOT COMPILE-VERIFIED: no O3DE engine SDK in the authoring environment."
```

---

### Task 3: The build-mode switch

**Files:**
- Modify: `Assets/Scripts/O3DE.Core/O3DE.Core.csproj:47` (new `PropertyGroup` after the main one) and `:101-105` (new `ItemGroup`)
- Test: `Editor/Tests/test_host_build_modes.py`

**Interfaces:**
- Produces (consumed by Tasks 6, 8, 9, 10, 14, 15, 17, 18):
  - MSBuild property `O3DESharpHostMode`, values `Coral` (default) and `NativeAot`
  - preprocessor symbols `O3DE_HOST_CORAL` / `O3DE_HOST_NATIVEAOT` (exactly one defined)
  - MSBuild property `O3DESharpEmitHostExports` (default `true` in `O3DE.Core.csproj` only)
  - both exposed to Roslyn generators via `CompilerVisibleProperty` as `build_property.O3DESharpHostMode` / `build_property.O3DESharpEmitHostExports`

> **Why a property and not a fourth Configuration.** `Debug;Profile;Release` are wired into the engine slnx, CMake's `include_external_msproject`, and `O3DE.Core.csproj:19`. Adding `Shipping` would ripple through all of them for no gain: the host mode is orthogonal to optimisation level (you want a `Release` NativeAOT build *and* a `Release` Coral build). One property, one define, no new configuration.

- [ ] **Step 1: Write the failing test**

Create `Editor/Tests/test_host_build_modes.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Guards the O3DESharpHostMode build-mode switch.

Coral (CoreCLR hosting) and NativeAOT are mutually exclusive per build
artifact - a NativeAOT image has no JIT and no hostfxr consumer. The two
artifacts come from one codebase, selected by one MSBuild property. These
tests pin that property's contract so a later edit cannot quietly define
both symbols, neither, or default to the shipping mode.
"""

import re
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
CORE_CSPROJ = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "O3DE.Core.csproj"


def _text():
    return CORE_CSPROJ.read_text(encoding="utf-8")


@pytest.mark.unit
def test_host_mode_defaults_to_coral():
    text = _text()
    m = re.search(
        r"<O3DESharpHostMode\s+Condition=\"'\$\(O3DESharpHostMode\)'\s*==\s*''\">(\w+)</O3DESharpHostMode>",
        text,
    )
    assert m, "O3DE.Core.csproj must default O3DESharpHostMode when it is unset."
    assert m.group(1) == "Coral", (
        "The default MUST be Coral. Defaulting to NativeAot would silently strip "
        "hot-reload out of every editor build."
    )


@pytest.mark.unit
def test_exactly_one_host_symbol_is_defined_per_mode():
    text = _text()
    assert "O3DE_HOST_CORAL" in text and "O3DE_HOST_NATIVEAOT" in text
    coral = re.search(
        r"Condition=\"'\$\(O3DESharpHostMode\)'\s*==\s*'NativeAot'\"[^>]*>\s*"
        r"<DefineConstants>\$\(DefineConstants\);O3DE_HOST_NATIVEAOT</DefineConstants>",
        text,
    )
    other = re.search(
        r"Condition=\"'\$\(O3DESharpHostMode\)'\s*!=\s*'NativeAot'\"[^>]*>\s*"
        r"<DefineConstants>\$\(DefineConstants\);O3DE_HOST_CORAL</DefineConstants>",
        text,
    )
    assert coral, "NativeAot mode must define O3DE_HOST_NATIVEAOT (and only it)."
    assert other, "Every non-NativeAot mode must define O3DE_HOST_CORAL (and only it)."


@pytest.mark.unit
def test_generator_can_see_both_properties():
    text = _text()
    for prop in ("O3DESharpHostMode", "O3DESharpEmitHostExports"):
        assert f'<CompilerVisibleProperty Include="{prop}" />' in text, (
            f"{prop} must be a CompilerVisibleProperty or the source generators "
            f"cannot read it from AnalyzerConfigOptions."
        )


@pytest.mark.unit
def test_only_o3de_core_emits_the_host_exports():
    # ManagedExports is a single well-known type. If every consumer assembly
    # emitted its own copy, the name would resolve ambiguously the moment two
    # of them were referenced together.
    assert "<O3DESharpEmitHostExports>true</O3DESharpEmitHostExports>" in _text()
    for other in (
        GEM_ROOT / "Assets" / "Scripts" / "O3DESharp" / "O3DESharp.csproj",
        GEM_ROOT / "Code" / "Tools" / "SourceGenerators.Tests" / "SourceGenerators.Smoke.csproj",
    ):
        assert "O3DESharpEmitHostExports" not in other.read_text(encoding="utf-8"), (
            f"{other.name} must not opt into emitting host exports; only O3DE.Core does."
        )
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest Editor/Tests/test_host_build_modes.py -q`
Expected: `4 failed` — `AssertionError: O3DE.Core.csproj must default O3DESharpHostMode when it is unset.` and the three following assertions.

- [ ] **Step 3: Add the switch to `O3DE.Core.csproj`**

Insert immediately after the closing `</PropertyGroup>` at `Assets/Scripts/O3DE.Core/O3DE.Core.csproj:47`:

```xml
  <!--
    M3 build-mode split. Coral (CoreCLR hosting, hot-reload, reflection) and
    NativeAOT (static, no JIT, no hostfxr) are mutually exclusive PER BUILD
    ARTIFACT - a NativeAOT image has nothing for nethost -> hostfxr to attach
    to. They are two artifacts from one codebase, never a runtime switch.

    This is a property rather than a fourth Configuration because the engine
    slnx, CMake's include_external_msproject and the Configurations list above
    are all wired for Debug/Profile/Release, and host mode is orthogonal to
    optimisation level anyway (a Release Coral build and a Release NativeAOT
    build are both wanted).

    Default is Coral: everything that exists today keeps building exactly as
    it did. Only the M4 publish passes -p:O3DESharpHostMode=NativeAot.
  -->
  <PropertyGroup>
    <O3DESharpHostMode Condition="'$(O3DESharpHostMode)' == ''">Coral</O3DESharpHostMode>

    <!--
      O3DE.Core is the ONE assembly that carries the ManagedExports thunks.
      ManagedExports is a single well-known type; if every consumer assembly
      emitted its own copy the name would resolve ambiguously as soon as two
      of them were referenced together.
    -->
    <O3DESharpEmitHostExports>true</O3DESharpEmitHostExports>
  </PropertyGroup>

  <PropertyGroup Condition="'$(O3DESharpHostMode)' == 'NativeAot'">
    <DefineConstants>$(DefineConstants);O3DE_HOST_NATIVEAOT</DefineConstants>
  </PropertyGroup>

  <PropertyGroup Condition="'$(O3DESharpHostMode)' != 'NativeAot'">
    <DefineConstants>$(DefineConstants);O3DE_HOST_CORAL</DefineConstants>
  </PropertyGroup>
```

- [ ] **Step 4: Expose both properties to the source generators**

Insert a new `ItemGroup` immediately before the source-generator `ItemGroup` at `Assets/Scripts/O3DE.Core/O3DE.Core.csproj:101`:

```xml
  <!--
    Roslyn generators read MSBuild properties through AnalyzerConfigOptions,
    but only ones explicitly published as CompilerVisibleProperty. Without
    these two lines HostExportsGenerator sees neither the host mode nor the
    emit flag and silently emits nothing.
  -->
  <ItemGroup>
    <CompilerVisibleProperty Include="O3DESharpHostMode" />
    <CompilerVisibleProperty Include="O3DESharpEmitHostExports" />
  </ItemGroup>
```

- [ ] **Step 5: Run test to verify it passes**

Run: `python -m pytest Editor/Tests/test_host_build_modes.py -q`
Expected: `4 passed`

- [ ] **Step 6: Verify both modes still build**

Run: `dotnet build Assets/Scripts/O3DE.Core/O3DE.Core.csproj -c Release --nologo && dotnet build Assets/Scripts/O3DE.Core/O3DE.Core.csproj -c Release --nologo -p:O3DESharpHostMode=NativeAot`
Expected: `0 Error(s)` for both. Warning count is unchanged from the 19-warning baseline in both modes (nothing is conditionally compiled yet).

- [ ] **Step 7: Commit**

```bash
git add Assets/Scripts/O3DE.Core/O3DE.Core.csproj Editor/Tests/test_host_build_modes.py
git commit -m "M3: add the O3DESharpHostMode build-mode switch

Coral and NativeAOT are mutually exclusive per build artifact, so the split
has to be a build-time choice. One property, defaulting to Coral, defining
exactly one of O3DE_HOST_CORAL / O3DE_HOST_NATIVEAOT, and published to the
Roslyn generators as a CompilerVisibleProperty.

A property rather than a fourth Configuration: the engine slnx and CMake's
include_external_msproject are wired for Debug/Profile/Release, and host mode
is orthogonal to optimisation level anyway.

The default keeps every existing build byte-for-byte unchanged."
```

---

### Task 4: `ScriptTypeRegistry` and reload-safe handle teardown

**Files:**
- Create: `Assets/Scripts/O3DE.Core/Interop/ScriptTypeRegistry.cs`
- Modify: `Assets/Scripts/O3DE.Core/Interop/ScriptComponentBridge.cs:41-75`
- Modify: `Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj:36-47`
- Test: `Assets/Scripts/O3DE.Core.Tests/Interop/ScriptTypeRegistryTests.cs`

**Interfaces:**
- Produces (consumed by Tasks 5, 6, 15):
  - `static void ScriptTypeRegistry.Register(string typeName, Func<object> factory)`
  - `static object? ScriptTypeRegistry.Create(string typeName)`
  - `static bool ScriptTypeRegistry.Contains(string typeName)`
  - `static void ScriptTypeRegistry.Clear()`
  - `static int ScriptTypeRegistry.Count`
  - `static int ScriptComponentBridge.ClearAll()` — drops every live handle, returns how many were dropped

> **Why a registry rather than `Activator.CreateInstance`.** `ManagedExports.CreateInstance` takes a type *name*, and the reflective way to turn that into an object is exactly the `Assembly.GetType` + `Activator.CreateInstance` pair that NativeAOT cannot see through (it is the same pattern flagged in `HotReloadManager.cs:220,230`). A registry of generated `static () => new T()` factories is AOT-safe, works identically in the editor, and is what Task 6's generator populates.

- [ ] **Step 1: Write the failing test**

Create `Assets/Scripts/O3DE.Core.Tests/Interop/ScriptTypeRegistryTests.cs`:

```csharp
//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using O3DE.Interop;

namespace O3DE.Core.Tests.Interop;

/// <summary>
/// ManagedExports.CreateInstance turns a type NAME into an object. The
/// reflective way to do that - Assembly.GetType + Activator.CreateInstance -
/// is exactly what NativeAOT cannot see through. A registry of generated
/// `static () => new T()` factories is AOT-safe and behaves identically in
/// the editor, so both artifacts share one code path.
/// </summary>
public class ScriptTypeRegistryTests : IDisposable
{
    public ScriptTypeRegistryTests() => ScriptTypeRegistry.Clear();
    public void Dispose() => ScriptTypeRegistry.Clear();

    private sealed class Probe { public int Serial; }

    [Fact]
    public void Create_UnknownType_ReturnsNull()
    {
        // Native code calls this with a name that came out of a component's
        // serialized config; an old/renamed class must not throw across the
        // [UnmanagedCallersOnly] boundary.
        ScriptTypeRegistry.Create("Nope.NotRegistered").Should().BeNull();
    }

    [Fact]
    public void Register_ThenCreate_UsesTheFactory()
    {
        int calls = 0;
        ScriptTypeRegistry.Register("Probe", () => { calls++; return new Probe { Serial = calls }; });

        ScriptTypeRegistry.Create("Probe").Should().BeOfType<Probe>();
        calls.Should().Be(1);
    }

    [Fact]
    public void Create_ReturnsAFreshInstanceEachTime()
    {
        ScriptTypeRegistry.Register("Probe", () => new Probe());

        var a = ScriptTypeRegistry.Create("Probe");
        var b = ScriptTypeRegistry.Create("Probe");

        a.Should().NotBeSameAs(b, "each component gets its own script instance");
    }

    [Fact]
    public void Register_SameNameTwice_LastOneWins()
    {
        // A hot-reload re-runs the generated registrations against the new
        // assembly. Re-registering must replace, not throw or duplicate.
        ScriptTypeRegistry.Register("Probe", () => new Probe { Serial = 1 });
        ScriptTypeRegistry.Register("Probe", () => new Probe { Serial = 2 });

        ((Probe)ScriptTypeRegistry.Create("Probe")!).Serial.Should().Be(2);
        ScriptTypeRegistry.Count.Should().Be(1);
    }

    [Fact]
    public void Register_RejectsNullsLoudly()
    {
        var nullName = () => ScriptTypeRegistry.Register(null!, () => new Probe());
        nullName.Should().Throw<ArgumentNullException>();

        var nullFactory = () => ScriptTypeRegistry.Register("Probe", null!);
        nullFactory.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Create_FactoryThatThrows_ReturnsNullRatherThanPropagating()
    {
        // The caller is an [UnmanagedCallersOnly] thunk - an escaping
        // exception terminates the process rather than being catchable.
        ScriptTypeRegistry.Register("Bad", () => throw new InvalidOperationException("boom"));

        ScriptTypeRegistry.Create("Bad").Should().BeNull();
    }

    [Fact]
    public void Contains_AndCount_TrackRegistrations()
    {
        ScriptTypeRegistry.Contains("Probe").Should().BeFalse();
        ScriptTypeRegistry.Register("Probe", () => new Probe());
        ScriptTypeRegistry.Contains("Probe").Should().BeTrue();
        ScriptTypeRegistry.Count.Should().Be(1);
    }

    [Fact]
    public void ClearAllHandles_DropsEveryLiveHandleAndReportsHowMany()
    {
        // Called from HotReloadSwap: every handle points at an instance in the
        // ALC about to be unloaded, so all of them must go before the swap.
        var a = ScriptComponentBridge.Register(new object());
        var b = ScriptComponentBridge.Register(new object());

        ScriptComponentBridge.ClearAll().Should().BeGreaterThanOrEqualTo(2);

        ScriptComponentBridge.Resolve(a).Should().BeNull();
        ScriptComponentBridge.Resolve(b).Should().BeNull();
        ScriptComponentBridge.ClearAll().Should().Be(0);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~ScriptTypeRegistryTests"`
Expected: build failure — `error CS0246: The type or namespace name 'ScriptTypeRegistry' could not be found` and `error CS0117: 'ScriptComponentBridge' does not contain a definition for 'ClearAll'`.

- [ ] **Step 3: Write the registry**

Create `Assets/Scripts/O3DE.Core/Interop/ScriptTypeRegistry.cs`:

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

namespace O3DE.Interop
{
    /// <summary>
    /// Maps a script type name to a factory that constructs it.
    ///
    /// ManagedExports.CreateInstance receives a type NAME (it comes out of a
    /// component's serialized config). The reflective way to turn that into an
    /// object - Assembly.GetType + Activator.CreateInstance - is precisely what
    /// NativeAOT cannot statically see, so the shipping image would fail to
    /// construct any script at all.
    ///
    /// Instead, HostExportsGenerator emits one
    ///     Register("Ns.Type", static () => new Ns.Type());
    /// per ScriptComponent subclass at compile time. That is a direct `new`,
    /// visible to the AOT compiler, and it behaves identically under CoreCLR -
    /// so the editor and the shipping build share one code path rather than
    /// diverging at the one point most likely to break silently.
    /// </summary>
    public static class ScriptTypeRegistry
    {
        private static readonly object s_lock = new object();
        private static readonly Dictionary<string, Func<object>> s_factories =
            new Dictionary<string, Func<object>>(StringComparer.Ordinal);

        /// <summary>Number of registered script types.</summary>
        public static int Count
        {
            get { lock (s_lock) { return s_factories.Count; } }
        }

        /// <summary>
        /// Register (or replace) the factory for a script type. Replacing is a
        /// normal outcome: a hot-reload re-runs the generated registrations
        /// against the newly loaded assembly.
        /// </summary>
        public static void Register(string typeName, Func<object> factory)
        {
            if (typeName is null) throw new ArgumentNullException(nameof(typeName));
            if (factory is null) throw new ArgumentNullException(nameof(factory));

            lock (s_lock)
            {
                s_factories[typeName] = factory;
            }
        }

        /// <summary>True if a factory is registered for this type name.</summary>
        public static bool Contains(string typeName)
        {
            if (typeName is null) return false;
            lock (s_lock) { return s_factories.ContainsKey(typeName); }
        }

        /// <summary>
        /// Construct an instance, or null if the name is unknown or the factory
        /// threw. Never throws: the only caller is an [UnmanagedCallersOnly]
        /// thunk, and an exception crossing that boundary terminates the
        /// process instead of being catchable.
        /// </summary>
        public static object? Create(string typeName)
        {
            if (typeName is null) return null;

            Func<object>? factory;
            lock (s_lock)
            {
                if (!s_factories.TryGetValue(typeName, out factory))
                {
                    return null;
                }
            }

            try
            {
                // Deliberately outside the lock: a user constructor can run
                // arbitrary code, including registering more types.
                return factory();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ScriptTypeRegistry] Constructing '{typeName}' threw: {ex}");
                return null;
            }
        }

        /// <summary>Drop every registration. Called before a hot-reload swap.</summary>
        public static void Clear()
        {
            lock (s_lock) { s_factories.Clear(); }
        }
    }
}
```

- [ ] **Step 4: Add `ClearAll` to the bridge**

In `Assets/Scripts/O3DE.Core/Interop/ScriptComponentBridge.cs`, after `Unregister` (which ends at line 75):

```csharp
        /// <summary>
        /// Drop every live handle and return how many were dropped.
        ///
        /// Called from HotReloadSwap: every registered instance lives in the
        /// AssemblyLoadContext that is about to be unloaded, so a handle that
        /// survives the swap resolves to an object in a dead ALC. Returning the
        /// count makes "the swap actually released something" assertable
        /// instead of assumed.
        /// </summary>
        public static int ClearAll()
        {
            lock (s_lock)
            {
                int dropped = s_instances.Count;
                s_instances.Clear();
                return dropped;
            }
        }
```

- [ ] **Step 5: Link the registry into the test project**

Add to the `<ItemGroup>` at `Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj:36-47`:

```xml
    <Compile Include="..\O3DE.Core\Interop\ScriptTypeRegistry.cs" Link="O3DE.Core\Interop\ScriptTypeRegistry.cs" />
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~ScriptTypeRegistryTests"`
Expected: `Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8`

- [ ] **Step 7: Run the full managed suite**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo`
Expected: `Passed!  - Failed:     0, Passed:    75, Skipped:     0, Total:    75`

- [ ] **Step 8: Commit**

```bash
git add Assets/Scripts/O3DE.Core/Interop/ScriptTypeRegistry.cs Assets/Scripts/O3DE.Core/Interop/ScriptComponentBridge.cs Assets/Scripts/O3DE.Core.Tests/Interop/ScriptTypeRegistryTests.cs Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj
git commit -m "M3: registry-based script construction instead of Activator

ManagedExports.CreateInstance takes a type name, and the reflective way to
turn that into an object - Assembly.GetType + Activator.CreateInstance - is
exactly what NativeAOT cannot see through. A registry of generated
'static () => new T()' factories is AOT-safe and behaves identically under
CoreCLR, so the editor and the shipping build share one construction path
rather than diverging at the point most likely to fail silently.

Create() never throws: its only caller is an [UnmanagedCallersOnly] thunk,
where an escaping exception terminates the process.

ClearAll() lands with it because HotReloadSwap must drop every handle before
the ALC unload, and returning the count makes that assertable."
```

---

### Task 5: `ManagedExportsImpl` — the export bodies

**Files:**
- Create: `Assets/Scripts/O3DE.Core/Interop/ManagedExportsImpl.cs`
- Modify: `Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj:36-47`
- Test: `Assets/Scripts/O3DE.Core.Tests/Interop/ManagedExportsImplTests.cs`

**Interfaces:**
- Consumes: `ScriptTypeRegistry.Create`, `ScriptComponentBridge.Register/Resolve/Unregister/Dispatch/ClearAll` (Task 4), `LifecycleId` (existing, SP-1a), `EBusHandlerRegistry.DispatchEvent` (existing).
- Produces (consumed by Task 6):
  - `static int ManagedExportsImpl.CreateInstance(string typeName)`
  - `static int ManagedExportsImpl.InvokeLifecycle(int handle, int lifecycleId, float arg)`
  - `static string? ManagedExportsImpl.DispatchEBusEvent(long token, string eventName, string argsJson)`
  - `static void ManagedExportsImpl.DestroyInstance(int handle)`
  - `static int ManagedExportsImpl.HotReloadSwap()`

> The bodies are ordinary managed methods so they are directly unit-testable; the generated `[UnmanagedCallersOnly]` thunks (Task 6) are thin UTF-8 marshaling wrappers around them. This is the same "separate `Dispatch` from `Invoke` so tests can reach it" split SP-1a already used in `ScriptComponentBridge`.

- [ ] **Step 1: Write the failing test**

Create `Assets/Scripts/O3DE.Core.Tests/Interop/ManagedExportsImplTests.cs`:

```csharp
//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using System.Collections.Generic;
using O3DE.Interop;

namespace O3DE.Core.Tests.Interop;

/// <summary>
/// The five ManagedExports bodies. They are plain managed methods precisely so
/// they can be tested here; the generated [UnmanagedCallersOnly] thunks are
/// thin UTF-8 marshaling wrappers with no logic of their own.
///
/// The invariant running through all of these: an export must never throw. Its
/// caller is native code across an [UnmanagedCallersOnly] boundary, where an
/// escaping exception terminates the process rather than being catchable.
/// </summary>
public class ManagedExportsImplTests : IDisposable
{
    public ManagedExportsImplTests()
    {
        ScriptTypeRegistry.Clear();
        ScriptComponentBridge.ClearAll();
    }

    public void Dispose()
    {
        ScriptTypeRegistry.Clear();
        ScriptComponentBridge.ClearAll();
    }

    private sealed class Probe : ScriptComponent
    {
        public List<string> Calls { get; } = new List<string>();
        public override void OnCreate() => Calls.Add("OnCreate");
        public override void OnDestroy() => Calls.Add("OnDestroy");
        public override void OnUpdate(float dt) => Calls.Add("Tick");
    }

    [Fact]
    public void CreateInstance_UnknownType_ReturnsZeroHandle()
    {
        ManagedExportsImpl.CreateInstance("Nope.Missing").Should().Be(0,
            "0 is the native 'no handle' sentinel; native code must not get a live handle for a dead name");
    }

    [Fact]
    public void CreateInstance_NullName_ReturnsZeroHandle()
    {
        ManagedExportsImpl.CreateInstance(null!).Should().Be(0);
    }

    [Fact]
    public void CreateInstance_RegisteredType_ReturnsResolvableHandle()
    {
        ScriptTypeRegistry.Register("Probe", () => new Probe());

        int handle = ManagedExportsImpl.CreateInstance("Probe");

        handle.Should().NotBe(0);
        ScriptComponentBridge.Resolve(handle).Should().BeOfType<Probe>();
    }

    [Fact]
    public void InvokeLifecycle_RoutesToTheComponent()
    {
        ScriptTypeRegistry.Register("Probe", () => new Probe());
        int handle = ManagedExportsImpl.CreateInstance("Probe");

        ManagedExportsImpl.InvokeLifecycle(handle, (int)LifecycleId.OnCreate, 0f).Should().Be(1);
        ManagedExportsImpl.InvokeLifecycle(handle, (int)LifecycleId.Tick, 0.25f).Should().Be(1);

        var probe = (Probe)ScriptComponentBridge.Resolve(handle)!;
        probe.Calls.Should().Equal("OnCreate", "Tick");
    }

    [Fact]
    public void InvokeLifecycle_DeadHandle_ReturnsZeroWithoutThrowing()
    {
        // Native teardown can race an in-flight tick. Zero means "nothing to
        // do", which is the correct outcome, not an error.
        ManagedExportsImpl.InvokeLifecycle(999999, (int)LifecycleId.Tick, 0f).Should().Be(0);
    }

    [Fact]
    public void InvokeLifecycle_UnknownLifecycleId_ReturnsZero()
    {
        ScriptTypeRegistry.Register("Probe", () => new Probe());
        int handle = ManagedExportsImpl.CreateInstance("Probe");

        ManagedExportsImpl.InvokeLifecycle(handle, 9999, 0f).Should().Be(0);
    }

    [Fact]
    public void DestroyInstance_ReleasesTheHandle()
    {
        ScriptTypeRegistry.Register("Probe", () => new Probe());
        int handle = ManagedExportsImpl.CreateInstance("Probe");

        ManagedExportsImpl.DestroyInstance(handle);

        ScriptComponentBridge.Resolve(handle).Should().BeNull();
    }

    [Fact]
    public void DestroyInstance_UnknownHandle_IsSilentlyFine()
    {
        var act = () => ManagedExportsImpl.DestroyInstance(4242);
        act.Should().NotThrow("teardown paths can run twice");
    }

    [Fact]
    public void DispatchEBusEvent_UnknownToken_ReturnsNull()
    {
        // Null means "no handler took this", which the native side reports as
        // "0 bytes needed", not as an error.
        ManagedExportsImpl.DispatchEBusEvent(0L, "OnTick", "[]").Should().BeNull();
        ManagedExportsImpl.DispatchEBusEvent(123456L, "OnTick", "[]").Should().BeNull();
    }

    [Fact]
    public void DispatchEBusEvent_MalformedArgsJson_ReturnsNullRatherThanThrowing()
    {
        ManagedExportsImpl.DispatchEBusEvent(1L, "OnTick", "{not json").Should().BeNull();
    }

    [Fact]
    public void HotReloadSwap_InTheCoralBuild_ClearsStateAndSucceeds()
    {
        ScriptTypeRegistry.Register("Probe", () => new Probe());
        ManagedExportsImpl.CreateInstance("Probe");

        int result = ManagedExportsImpl.HotReloadSwap();

#if O3DE_HOST_NATIVEAOT
        result.Should().Be(0, "a NativeAOT image has no AssemblyLoadContext to swap");
#else
        result.Should().Be(1);
        ScriptComponentBridge.ClearAll().Should().Be(0, "the swap already dropped every handle");
        ScriptTypeRegistry.Count.Should().Be(0, "the swap already dropped every registration");
#endif
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~ManagedExportsImplTests"`
Expected: build failure — `error CS0246: The type or namespace name 'ManagedExportsImpl' could not be found`.

- [ ] **Step 3: Write the export bodies**

Create `Assets/Scripts/O3DE.Core/Interop/ManagedExportsImpl.cs`:

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using O3DE.Reflection;

namespace O3DE.Interop
{
    /// <summary>
    /// Bodies of the five ManagedExports entry points.
    ///
    /// Kept as ordinary managed methods so they are unit-testable; the
    /// [UnmanagedCallersOnly] thunks that HostExportsGenerator emits into
    /// ManagedExports.g.cs are thin UTF-8 marshaling wrappers with no logic of
    /// their own. Same split ScriptComponentBridge already uses for
    /// Invoke/Dispatch, and for the same reason: an [UnmanagedCallersOnly]
    /// method cannot be called from managed code at all.
    ///
    /// Every method here is total - it returns a sentinel rather than throwing.
    /// The caller is native code across an [UnmanagedCallersOnly] boundary,
    /// where an escaping exception terminates the process.
    /// </summary>
    public static class ManagedExportsImpl
    {
        /// <summary>
        /// Construct a script instance by name and return its native handle.
        /// 0 means failure (unknown type, or the constructor threw) - native
        /// code treats 0 as "no component".
        /// </summary>
        public static int CreateInstance(string typeName)
        {
            try
            {
                object? instance = ScriptTypeRegistry.Create(typeName);
                return instance is null ? 0 : ScriptComponentBridge.Register(instance);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManagedExports] CreateInstance('{typeName}') failed: {ex}");
                return 0;
            }
        }

        /// <summary>
        /// Dispatch one lifecycle callback. Returns 1 if it was dispatched, 0
        /// if the handle is dead or the id is unknown. A dead handle is a
        /// normal outcome (teardown racing an in-flight tick), not an error.
        /// </summary>
        public static int InvokeLifecycle(int handle, int lifecycleId, float arg)
        {
            try
            {
                object? instance = ScriptComponentBridge.Resolve(handle);
                if (instance is null)
                {
                    return 0;
                }
                return ScriptComponentBridge.Dispatch(instance, (LifecycleId)lifecycleId, arg) ? 1 : 0;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManagedExports] InvokeLifecycle(handle={handle}, id={lifecycleId}) failed: {ex}");
                return 0;
            }
        }

        /// <summary>
        /// Route an EBus event to the managed handler registered under
        /// <paramref name="token"/>. Returns the handler's JSON result, or null
        /// when no handler took it (which the thunk reports as "0 bytes
        /// needed", not as an error).
        /// </summary>
        public static string? DispatchEBusEvent(long token, string eventName, string argsJson)
        {
            try
            {
                return EBusHandlerRegistry.DispatchEvent(token, eventName, argsJson);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManagedExports] DispatchEBusEvent(token={token}, event='{eventName}') failed: {ex}");
                return null;
            }
        }

        /// <summary>Release a script instance handle. Safe to call twice.</summary>
        public static void DestroyInstance(int handle)
        {
            try
            {
                ScriptComponentBridge.Unregister(handle);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManagedExports] DestroyInstance(handle={handle}) failed: {ex}");
            }
        }

        /// <summary>
        /// Prepare the managed side for an AssemblyLoadContext swap: drop every
        /// live handle and every type registration, because both point into the
        /// context about to be unloaded, and clear the reflection caches.
        /// Returns 1 on success.
        ///
        /// In a NativeAOT image there is no ALC and no hot-reload by design, so
        /// this returns 0 and the host reports SupportsHotReload() == false.
        /// </summary>
        public static int HotReloadSwap()
        {
#if O3DE_HOST_NATIVEAOT
            // Not a failure - hot-reload is editor-only by design. Returning 0
            // is how the host learns that, rather than by probing for it.
            return 0;
#else
            try
            {
                ScriptComponentBridge.ClearAll();
                ScriptTypeRegistry.Clear();
                NativeReflection.ClearCache();
                return 1;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManagedExports] HotReloadSwap failed: {ex}");
                return 0;
            }
#endif
        }
    }
}
```

- [ ] **Step 4: Link it into the test project**

Add to the `<ItemGroup>` at `Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj:36-47`:

```xml
    <Compile Include="..\O3DE.Core\Interop\ManagedExportsImpl.cs" Link="O3DE.Core\Interop\ManagedExportsImpl.cs" />
    <Compile Include="..\O3DE.Core\Reflection\EBusHandlerRegistry.cs" Link="O3DE.Core\Reflection\EBusHandlerRegistry.cs" />
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~ManagedExportsImplTests"`
Expected: `Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11`

- [ ] **Step 6: Run the full managed suite in both host modes**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo`
Expected: `Passed!  - Failed:     0, Passed:    86, Skipped:     0, Total:    86`

Then confirm the `#if O3DE_HOST_NATIVEAOT` arm of `HotReloadSwap_InTheCoralBuild_ClearsStateAndSucceeds` also holds:
Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo -p:DefineConstants=O3DE_HOST_NATIVEAOT --filter "FullyQualifiedName~ManagedExportsImplTests"`
Expected: `Passed!  - Failed:     0, Passed:    11`

- [ ] **Step 7: Commit**

```bash
git add Assets/Scripts/O3DE.Core/Interop/ManagedExportsImpl.cs Assets/Scripts/O3DE.Core.Tests/Interop/ManagedExportsImplTests.cs Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj
git commit -m "M3: implement the five ManagedExports bodies

Plain managed methods, so they are unit-testable - the generated
[UnmanagedCallersOnly] thunks are thin UTF-8 wrappers with no logic. Same
split ScriptComponentBridge already uses for Invoke/Dispatch, and for the same
reason: an [UnmanagedCallersOnly] method is unreachable from managed code.

Every export is total and returns a sentinel instead of throwing, because the
caller is native code across a boundary where an escaping exception terminates
the process. HotReloadSwap returns 0 under O3DE_HOST_NATIVEAOT - there is no
AssemblyLoadContext to swap, and that is a designed capability answer rather
than a failure."
```

---

### Task 6: `HostExportsGenerator` — the ABI adapter

**Files:**
- Create: `Code/Tools/SourceGenerators/HostExportsGenerator.cs`
- Modify: `Code/Tools/SourceGenerators.Tests/SourceGenerators.Smoke.csproj:19-38`
- Test: `Editor/Tests/test_host_exports_emit.py`

**Interfaces:**
- Consumes: `build_property.O3DESharpHostMode`, `build_property.O3DESharpEmitHostExports` (Task 3); `ManagedExportsImpl.*` (Task 5); `ScriptTypeRegistry.Register` (Task 4).
- Produces (consumed by Tasks 11, 12, 14):
  - generated type `O3DE.Interop.ManagedExportsThunks` carrying the five `[UnmanagedCallersOnly]` statics `O3DESharp_CreateInstance`, `O3DESharp_InvokeLifecycle`, `O3DESharp_DispatchEBusEvent`, `O3DESharp_DestroyInstance`, `O3DESharp_HotReloadSwap`
  - generated `[UnmanagedCallersOnly(EntryPoint = "O3DESharp_GetManagedExports")] static int GetManagedExports(NativeImports*, ManagedExports*)`
  - generated `O3DE.Interop.GeneratedScriptTypes.RegisterAll()` calling `ScriptTypeRegistry.Register` once per `ScriptComponent` subclass in the compilation

> **What varies by build mode is deliberately small.** `[UnmanagedCallersOnly(EntryPoint = ...)]` is legal in both modes (it only takes effect when compiling to a native library), so the thunks themselves are mode-independent. The generator emits a `HostMode` constant and nothing else conditional. That is the honest size of the difference; inventing more per-mode divergence would be complexity with no consumer.
>
> `EBusHandlerGenerator` (`Code/Tools/SourceGenerators/EBusHandlerGenerator.cs:72-98`) is the proven pattern being extended: an `IIncrementalGenerator` with a cheap syntax predicate, a semantic transform, and a `StringBuilder` emit.

- [ ] **Step 1: Write the failing test**

Create `Editor/Tests/test_host_exports_emit.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Verifies what HostExportsGenerator actually emits into O3DE.Core.

The generator is the ABI adapter: it turns ManagedExportsImpl's plain methods
into [UnmanagedCallersOnly] thunks and packs their addresses into a
ManagedExports struct. Nothing else in the build fails if it emits the wrong
thing - the editor just silently loses its exports - so the emitted text is
asserted directly.

Marked `slow` because it shells out to `dotnet build`.
"""

import shutil
import subprocess
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
CORE_CSPROJ = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "O3DE.Core.csproj"
GENERATED = (
    GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "obj" / "Release" / "net9.0"
    / "generated" / "O3DESharp.SourceGenerators"
    / "O3DESharp.SourceGenerators.HostExportsGenerator"
)

EXPECTED_THUNKS = [
    "O3DESharp_CreateInstance",
    "O3DESharp_InvokeLifecycle",
    "O3DESharp_DispatchEBusEvent",
    "O3DESharp_DestroyInstance",
    "O3DESharp_HotReloadSwap",
]


def _build(host_mode):
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    result = subprocess.run(
        [
            "dotnet", "build", str(CORE_CSPROJ), "-c", "Release", "--nologo",
            "-p:EmitCompilerGeneratedFiles=true",
            f"-p:O3DESharpHostMode={host_mode}",
        ],
        capture_output=True, text=True, timeout=900,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    files = list(GENERATED.glob("*.g.cs"))
    assert files, f"HostExportsGenerator emitted nothing into {GENERATED}"
    return "\n".join(f.read_text(encoding="utf-8") for f in files)


@pytest.mark.slow
def test_coral_mode_emits_all_five_thunks_and_the_exports_getter():
    emitted = _build("Coral")
    for thunk in EXPECTED_THUNKS:
        assert f"[UnmanagedCallersOnly(EntryPoint = \"{thunk}\")]" in emitted, thunk
    assert "O3DESharp_GetManagedExports" in emitted
    assert "HostMode = \"Coral\"" in emitted


@pytest.mark.slow
def test_exports_struct_is_filled_in_the_frozen_field_order():
    emitted = _build("Coral")
    order = [
        "exports->CreateInstance",
        "exports->InvokeLifecycle",
        "exports->DispatchEBusEvent",
        "exports->DestroyInstance",
        "exports->HotReloadSwap",
    ]
    positions = [emitted.index(field) for field in order]
    assert positions == sorted(positions), (
        "ManagedExports must be filled in the frozen field order; the C++ side "
        "reads it as a struct, not by name."
    )


@pytest.mark.slow
def test_version_is_stamped_on_both_structs_before_use():
    emitted = _build("Coral")
    assert "exports->Version = HostAbi.Version" in emitted, (
        "GetManagedExports must stamp the version, or a host cannot tell a "
        "populated struct from a zeroed one."
    )
    assert "imports->Version != HostAbi.Version" in emitted, (
        "GetManagedExports must REJECT an imports struct whose version it does "
        "not recognise rather than reinterpreting its pointers."
    )


@pytest.mark.slow
def test_nativeaot_mode_emits_the_same_thunks():
    emitted = _build("NativeAot")
    for thunk in EXPECTED_THUNKS:
        assert f"[UnmanagedCallersOnly(EntryPoint = \"{thunk}\")]" in emitted, thunk
    assert "HostMode = \"NativeAot\"" in emitted
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest Editor/Tests/test_host_exports_emit.py -q -m slow`
Expected: `4 failed` with `AssertionError: HostExportsGenerator emitted nothing into .../O3DESharp.SourceGenerators.HostExportsGenerator` (the directory does not exist).

- [ ] **Step 3: Write the generator**

Create `Code/Tools/SourceGenerators/HostExportsGenerator.cs`:

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace O3DESharp.SourceGenerators
{
    /// <summary>
    /// Emits the ABI adapter: the five [UnmanagedCallersOnly] thunks that back
    /// O3DE.Interop.ManagedExports, the GetManagedExports entry point that
    /// packs their addresses into the struct, and one ScriptTypeRegistry
    /// registration per ScriptComponent subclass in the compilation.
    ///
    /// Runs only when build_property.O3DESharpEmitHostExports is "true", which
    /// only O3DE.Core.csproj sets. ManagedExports is a single well-known type;
    /// if every consumer assembly emitted its own copy, the name would resolve
    /// ambiguously as soon as two of them were referenced together.
    ///
    /// Almost nothing varies by host mode, and that is the honest size of the
    /// difference: [UnmanagedCallersOnly(EntryPoint = ...)] is legal in both
    /// modes and only takes effect when compiling to a native library, so one
    /// thunk shape serves Coral and NativeAOT alike. Only the HostMode constant
    /// differs. The real per-mode divergence lives in ManagedExportsImpl's
    /// #if O3DE_HOST_NATIVEAOT (HotReloadSwap) and in which C++ IManagedHost
    /// implementation consumes the struct.
    /// </summary>
    [Generator]
    public sealed class HostExportsGenerator : IIncrementalGenerator
    {
        private const string ScriptComponentFullName = "O3DE.ScriptComponent";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Build-mode inputs. Both are published by O3DE.Core.csproj as
            // CompilerVisibleProperty; without that they are simply absent and
            // the generator does nothing.
            var buildMode = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) =>
            {
                provider.GlobalOptions.TryGetValue("build_property.O3DESharpEmitHostExports", out var emit);
                provider.GlobalOptions.TryGetValue("build_property.O3DESharpHostMode", out var mode);
                return new BuildMode(
                    string.Equals(emit, "true", System.StringComparison.OrdinalIgnoreCase),
                    string.IsNullOrEmpty(mode) ? "Coral" : mode!);
            });

            // Every concrete ScriptComponent subclass gets a registry factory.
            // A direct `new T()` is visible to the AOT compiler; the
            // Assembly.GetType + Activator.CreateInstance it replaces is not.
            var scriptTypes = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                    transform: static (ctx, _) => TryGetScriptComponent(ctx))
                .Where(static name => name is not null)
                .Select(static (name, _) => name!)
                .Collect();

            context.RegisterSourceOutput(
                buildMode.Combine(scriptTypes),
                static (sourceContext, pair) =>
                {
                    var (mode, types) = pair;
                    if (!mode.EmitHostExports)
                    {
                        return;
                    }

                    sourceContext.AddSource(
                        "O3DE.Interop.ManagedExports.g.cs",
                        SourceText.From(EmitExports(mode), Encoding.UTF8));

                    sourceContext.AddSource(
                        "O3DE.Interop.GeneratedScriptTypes.g.cs",
                        SourceText.From(EmitScriptTypes(types), Encoding.UTF8));
                });
        }

        // -----------------------------------------------------------
        // Semantic resolution
        // -----------------------------------------------------------

        private static string? TryGetScriptComponent(GeneratorSyntaxContext context)
        {
            var classSyntax = (ClassDeclarationSyntax)context.Node;
            if (context.SemanticModel.GetDeclaredSymbol(classSyntax) is not INamedTypeSymbol symbol)
            {
                return null;
            }

            // Abstract types cannot be constructed, and a type without a public
            // parameterless constructor has no `new T()` to emit.
            if (symbol.IsAbstract || symbol.IsStatic || symbol.IsGenericType)
            {
                return null;
            }
            if (!symbol.InstanceConstructors.Any(c =>
                    c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
            {
                return null;
            }

            for (var baseType = symbol.BaseType; baseType is not null; baseType = baseType.BaseType)
            {
                if (baseType.ToDisplayString() == ScriptComponentFullName)
                {
                    return symbol.ToDisplayString();
                }
            }
            return null;
        }

        // -----------------------------------------------------------
        // Emit
        // -----------------------------------------------------------

        private static void AppendHeader(StringBuilder sb)
        {
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//   This file was generated by O3DESharp.SourceGenerators (HostExportsGenerator).");
            sb.AppendLine("//   DO NOT EDIT.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine();
        }

        private static string EmitExports(BuildMode mode)
        {
            var sb = new StringBuilder();
            AppendHeader(sb);
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Runtime.InteropServices;");
            sb.AppendLine();
            sb.AppendLine("namespace O3DE.Interop");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// The [UnmanagedCallersOnly] thunks backing ManagedExports, plus the");
            sb.AppendLine("    /// single entry point that exchanges the two ABI structs.");
            sb.AppendLine("    ///");
            sb.AppendLine("    /// Bodies live in ManagedExportsImpl - these are UTF-8 marshaling");
            sb.AppendLine("    /// wrappers only. None of them may throw: an exception crossing an");
            sb.AppendLine("    /// [UnmanagedCallersOnly] boundary terminates the process.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static unsafe class ManagedExportsThunks");
            sb.AppendLine("    {");
            sb.AppendLine($"        /// <summary>Build mode this assembly was generated for.</summary>");
            sb.AppendLine($"        public const string HostMode = \"{mode.HostMode}\";");
            sb.AppendLine();

            sb.AppendLine("        [UnmanagedCallersOnly(EntryPoint = \"O3DESharp_CreateInstance\")]");
            sb.AppendLine("        public static int O3DESharp_CreateInstance(byte* utf8TypeName)");
            sb.AppendLine("        {");
            sb.AppendLine("            string? name = Marshal.PtrToStringUTF8((IntPtr)utf8TypeName);");
            sb.AppendLine("            return name is null ? 0 : ManagedExportsImpl.CreateInstance(name);");
            sb.AppendLine("        }");
            sb.AppendLine();

            sb.AppendLine("        [UnmanagedCallersOnly(EntryPoint = \"O3DESharp_InvokeLifecycle\")]");
            sb.AppendLine("        public static int O3DESharp_InvokeLifecycle(int handle, int lifecycleId, float arg)");
            sb.AppendLine("        {");
            sb.AppendLine("            return ManagedExportsImpl.InvokeLifecycle(handle, lifecycleId, arg);");
            sb.AppendLine("        }");
            sb.AppendLine();

            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Writes the handler's JSON result into outBuffer and returns the");
            sb.AppendLine("        /// number of bytes the result needs (snprintf semantics), so a short");
            sb.AppendLine("        /// buffer is a retry rather than a silent truncation. 0 means no");
            sb.AppendLine("        /// handler took the event; -1 means the dispatch itself failed.");
            sb.AppendLine("        /// Keeping the buffer caller-owned is what lets ManagedExports stay");
            sb.AppendLine("        /// at five fields with no Free export.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        [UnmanagedCallersOnly(EntryPoint = \"O3DESharp_DispatchEBusEvent\")]");
            sb.AppendLine("        public static int O3DESharp_DispatchEBusEvent(");
            sb.AppendLine("            long token, byte* utf8EventName, byte* utf8ArgsJson, byte* outBuffer, int outCapacity)");
            sb.AppendLine("        {");
            sb.AppendLine("            string eventName = Marshal.PtrToStringUTF8((IntPtr)utf8EventName) ?? string.Empty;");
            sb.AppendLine("            string argsJson = Marshal.PtrToStringUTF8((IntPtr)utf8ArgsJson) ?? \"[]\";");
            sb.AppendLine();
            sb.AppendLine("            string? result = ManagedExportsImpl.DispatchEBusEvent(token, eventName, argsJson);");
            sb.AppendLine("            if (result is null)");
            sb.AppendLine("            {");
            sb.AppendLine("                return 0;");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(result);");
            sb.AppendLine("            if (outBuffer != null && outCapacity > utf8.Length)");
            sb.AppendLine("            {");
            sb.AppendLine("                Marshal.Copy(utf8, 0, (IntPtr)outBuffer, utf8.Length);");
            sb.AppendLine("                outBuffer[utf8.Length] = 0;");
            sb.AppendLine("            }");
            sb.AppendLine("            // Always the required length including the NUL, so the caller can");
            sb.AppendLine("            // size a buffer and call again.");
            sb.AppendLine("            return utf8.Length + 1;");
            sb.AppendLine("        }");
            sb.AppendLine();

            sb.AppendLine("        [UnmanagedCallersOnly(EntryPoint = \"O3DESharp_DestroyInstance\")]");
            sb.AppendLine("        public static void O3DESharp_DestroyInstance(int handle)");
            sb.AppendLine("        {");
            sb.AppendLine("            ManagedExportsImpl.DestroyInstance(handle);");
            sb.AppendLine("        }");
            sb.AppendLine();

            sb.AppendLine("        [UnmanagedCallersOnly(EntryPoint = \"O3DESharp_HotReloadSwap\")]");
            sb.AppendLine("        public static int O3DESharp_HotReloadSwap()");
            sb.AppendLine("        {");
            sb.AppendLine("            return ManagedExportsImpl.HotReloadSwap();");
            sb.AppendLine("        }");
            sb.AppendLine();

            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// The one call that exchanges the ABI. C++ hands in a populated");
            sb.AppendLine("        /// NativeImports and an empty ManagedExports; this stores the former");
            sb.AppendLine("        /// and fills the latter. Returns 1 on success, 0 on version mismatch.");
            sb.AppendLine("        ///");
            sb.AppendLine("        /// Under NativeAOT this is the exported symbol the host resolves with");
            sb.AppendLine("        /// GetProcAddress/dlsym. Under Coral the host resolves it through");
            sb.AppendLine("        /// CoralNativeThunkHost instead; the code is identical either way.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        [UnmanagedCallersOnly(EntryPoint = \"O3DESharp_GetManagedExports\")]");
            sb.AppendLine("        public static int O3DESharp_GetManagedExports(NativeImports* imports, ManagedExports* exports)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (imports == null || exports == null)");
            sb.AppendLine("            {");
            sb.AppendLine("                return 0;");
            sb.AppendLine("            }");
            sb.AppendLine("            if (imports->Version != HostAbi.Version)");
            sb.AppendLine("            {");
            sb.AppendLine("                // Refuse rather than reinterpret: an unrecognised version means");
            sb.AppendLine("                // every pointer after the first divergence is garbage.");
            sb.AppendLine("                return 0;");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            NativeImportsStore.Imports = *imports;");
            sb.AppendLine("            GeneratedScriptTypes.RegisterAll();");
            sb.AppendLine();
            sb.AppendLine("            exports->Version = HostAbi.Version;");
            sb.AppendLine("            exports->CreateInstance = (IntPtr)(delegate* unmanaged<byte*, int>)&O3DESharp_CreateInstance;");
            sb.AppendLine("            exports->InvokeLifecycle = (IntPtr)(delegate* unmanaged<int, int, float, int>)&O3DESharp_InvokeLifecycle;");
            sb.AppendLine("            exports->DispatchEBusEvent = (IntPtr)(delegate* unmanaged<long, byte*, byte*, byte*, int, int>)&O3DESharp_DispatchEBusEvent;");
            sb.AppendLine("            exports->DestroyInstance = (IntPtr)(delegate* unmanaged<int, void>)&O3DESharp_DestroyInstance;");
            sb.AppendLine("            exports->HotReloadSwap = (IntPtr)(delegate* unmanaged<int>)&O3DESharp_HotReloadSwap;");
            sb.AppendLine("            return 1;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Holds the NativeImports handed over at init. Under Coral this is");
            sb.AppendLine("    /// descriptive only - InternalCalls is still populated by Coral's own");
            sb.AppendLine("    /// AddInternalCall/UploadInternalCalls and nothing about that path");
            sb.AppendLine("    /// changes. Under NativeAOT it is the sole source of the pointers.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static class NativeImportsStore");
            sb.AppendLine("    {");
            sb.AppendLine("        public static NativeImports Imports;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string EmitScriptTypes(ImmutableArray<string> types)
        {
            var sb = new StringBuilder();
            AppendHeader(sb);
            sb.AppendLine("namespace O3DE.Interop");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// One ScriptTypeRegistry entry per concrete ScriptComponent subclass");
            sb.AppendLine("    /// in this compilation. Each factory is a direct `new T()`, which the");
            sb.AppendLine("    /// AOT compiler can see; the Assembly.GetType + Activator.CreateInstance");
            sb.AppendLine("    /// it replaces is exactly what it cannot.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static class GeneratedScriptTypes");
            sb.AppendLine("    {");
            sb.AppendLine("        /// <summary>Idempotent: Register replaces, so a hot-reload re-run is safe.</summary>");
            sb.AppendLine("        public static void RegisterAll()");
            sb.AppendLine("        {");
            foreach (var type in types.Distinct().OrderBy(t => t, System.StringComparer.Ordinal))
            {
                sb.AppendLine($"            ScriptTypeRegistry.Register(\"{type}\", static () => new global::{type}());");
            }
            if (types.Length == 0)
            {
                sb.AppendLine("            // No ScriptComponent subclasses in this compilation.");
            }
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Build-mode inputs, as a record so the incremental generator caches it by
    /// value - a sealed class would invalidate the cache on every keystroke.
    /// </summary>
    internal sealed record BuildMode(bool EmitHostExports, string HostMode);
}
```

- [ ] **Step 4: Keep the smoke project out of the exports path**

`SourceGenerators.Smoke` references `O3DE.Core`, so if it also emitted `ManagedExportsThunks` the two copies would collide the moment both assemblies were referenced together. It must not set `O3DESharpEmitHostExports`. Add this to the `PropertyGroup` at `Code/Tools/SourceGenerators.Tests/SourceGenerators.Smoke.csproj:19-38` so the intent is explicit rather than accidental:

```xml
    <!--
      Deliberately NOT setting O3DESharpEmitHostExports. This project
      references O3DE.Core, which already carries ManagedExportsThunks; a
      second copy here would make the type name resolve ambiguously. Only
      O3DE.Core opts in. Editor/Tests/test_host_build_modes.py asserts this.
    -->
```

- [ ] **Step 5: Run test to verify it passes**

Run: `python -m pytest Editor/Tests/test_host_exports_emit.py -q -m slow`
Expected: `4 passed`

- [ ] **Step 6: Confirm the EBus generator and the smoke project are unaffected**

Run: `dotnet build Code/Tools/SourceGenerators.Tests/SourceGenerators.Smoke.csproj -c Release --nologo && dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo`
Expected: `0 Error(s)` for the smoke build, and `Passed!  - Failed:     0, Passed:    86` for the tests. `Code/Tools/SourceGenerators.Tests/generated/` still contains only the `EBusHandlerGenerator` output directory.

- [ ] **Step 7: Commit**

```bash
git add Code/Tools/SourceGenerators/HostExportsGenerator.cs Code/Tools/SourceGenerators.Tests/SourceGenerators.Smoke.csproj Editor/Tests/test_host_exports_emit.py
git commit -m "M3: generate the ABI adapter - export thunks and script-type registry

Turns ManagedExportsImpl's plain methods into [UnmanagedCallersOnly] thunks,
packs their addresses into ManagedExports in the frozen field order, and emits
one ScriptTypeRegistry factory per ScriptComponent subclass as a direct
new T() the AOT compiler can see.

GetManagedExports refuses an imports struct whose version it does not
recognise rather than reinterpreting its pointers, and stamps the version on
the exports it returns so a host can tell populated from zeroed.

Almost nothing varies by host mode, which is the honest size of the
difference: [UnmanagedCallersOnly(EntryPoint=...)] is legal in both and only
takes effect when compiling to a native library, so one thunk shape serves
both artifacts. Only O3DE.Core opts into emitting them - a second copy in a
consumer assembly would make the type name ambiguous."
```

---

### Task 7: AOT-ready JSON serialization

**Files:**
- Create: `Assets/Scripts/O3DE.Core/Reflection/NativeReflectionJsonContext.cs`
- Modify: `Assets/Scripts/O3DE.Core/Reflection/NativeReflection.cs:529-584`
- Modify: `Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj:36-47`
- Test: `Assets/Scripts/O3DE.Core.Tests/NativeReflectionAotTests.cs`

**Interfaces:**
- Produces (consumed by Task 10): `O3DE.Reflection.NativeReflectionJsonContext` — a `JsonSerializerContext` registering the closed set of types `SerializeArgumentToObject` can produce.

> `NativeReflection.cs:542` and `:583` are the only two `JsonSerializer.Serialize` calls in `O3DE.Core`, and they are 4 of the 14 IL warnings (`IL2026` + `IL3050` each). `SerializeArgumentToObject` (`:545-579`) produces a **closed** set — `bool`, `int`, `long`, `ulong`, `float`, `double`, `string`, `float[]`, `null` — so a source-generated context can cover all of it. The values are boxed into `List<object?>`, and `System.Text.Json`'s object converter looks the runtime type up in the resolver, which finds it because every member of the closed set is registered.

- [ ] **Step 1: Write the failing test**

Create `Assets/Scripts/O3DE.Core.Tests/NativeReflectionAotTests.cs`:

```csharp
//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System.Collections.Generic;
using System.Text.Json;
using O3DE.Reflection;

namespace O3DE.Core.Tests;

/// <summary>
/// NativeReflection's argument serializer is the managed side's only
/// reflection-based JsonSerializer use, and under NativeAOT reflection-based
/// serialization needs runtime code generation that is not there - it throws
/// at the first EBus broadcast rather than failing to build.
///
/// Moving to a source-generated JsonSerializerContext fixes that, but the wire
/// format is a contract with the C++ marshaler
/// (O3DESharp::Marshaling::JsonValueToBehaviorParameter), so these pin the
/// exact output for every type SerializeArgumentToObject can produce. If the
/// context resolver ever misses a type, serialization changes shape or throws -
/// both caught here.
/// </summary>
public class NativeReflectionAotTests
{
    [Fact]
    public void Context_ResolvesEveryTypeTheArgumentSerializerCanProduce()
    {
        // The closed set from SerializeArgumentToObject. A type missing from
        // the context throws NotSupportedException under AOT at the first call.
        var closedSet = new[]
        {
            typeof(bool), typeof(int), typeof(long), typeof(ulong),
            typeof(float), typeof(double), typeof(string),
            typeof(float[]), typeof(List<object?>),
        };

        foreach (var type in closedSet)
        {
            NativeReflectionJsonContext.Default.GetTypeInfo(type)
                .Should().NotBeNull($"{type.Name} is producible by SerializeArgumentToObject");
        }
    }

    [Theory]
    [InlineData(true, "[true]")]
    [InlineData(42, "[42]")]
    [InlineData("hello", "[\"hello\"]")]
    public void SerializeArguments_PrimitiveWireFormatIsUnchanged(object arg, string expected)
    {
        NativeReflection.SerializeArgumentsForTest(new[] { arg }).Should().Be(expected);
    }

    [Fact]
    public void SerializeArguments_MathTypesStayAsNumberArrays()
    {
        // The C++ marshaler maps the 3- vs 4-element array shape to
        // Vector3 / Quaternion. Changing this breaks every EBus call with a
        // math argument, silently, at runtime.
        NativeReflection.SerializeArgumentsForTest(new object[] { new Vector3(1f, 2f, 3f) })
            .Should().Be("[[1,2,3]]");
        NativeReflection.SerializeArgumentsForTest(new object[] { new Quaternion(0f, 0f, 0f, 1f) })
            .Should().Be("[[0,0,0,1]]");
    }

    [Fact]
    public void SerializeArguments_MixedArgumentsKeepTheirOrder()
    {
        NativeReflection.SerializeArgumentsForTest(new object[] { 1, "two", 3.5, true })
            .Should().Be("[1,\"two\",3.5,true]");
    }

    [Fact]
    public void SerializeArguments_EmptyIsAnEmptyArray()
    {
        NativeReflection.SerializeArgumentsForTest(System.Array.Empty<object>()).Should().Be("[]");
    }

    [Fact]
    public void SerializeArguments_UnsupportedType_StillThrowsLoudly()
    {
        // The pre-existing NotSupportedException must survive the AOT change:
        // a silently stringified argument is worse than a hard failure, because
        // the C++ marshaler cannot consume a display string.
        var act = () => NativeReflection.SerializeArgumentsForTest(new object[] { new object() });
        act.Should().Throw<System.NotSupportedException>();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~NativeReflectionAotTests"`
Expected: build failure — `error CS0246: The type or namespace name 'NativeReflectionJsonContext' could not be found` and `error CS0117: 'NativeReflection' does not contain a definition for 'SerializeArgumentsForTest'`.

- [ ] **Step 3: Add the serializer context**

Create `Assets/Scripts/O3DE.Core/Reflection/NativeReflectionJsonContext.cs`:

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace O3DE.Reflection
{
    /// <summary>
    /// Source-generated serialization metadata for NativeReflection's argument
    /// envelope.
    ///
    /// Reflection-based JsonSerializer.Serialize needs runtime code generation,
    /// which a NativeAOT image does not have - the failure is a
    /// NotSupportedException at the first EBus broadcast, not a build error.
    /// This context makes the metadata compile-time.
    ///
    /// The registered set is exactly what NativeReflection.SerializeArgumentToObject
    /// can produce, and it is CLOSED by construction: that method has an
    /// explicit case per supported type and throws NotSupportedException on
    /// anything else. Arguments are boxed into List<object?>, and the
    /// object converter resolves each element's runtime type through this
    /// context - which succeeds precisely because the set is closed and every
    /// member is listed here. Adding a case to SerializeArgumentToObject
    /// therefore requires adding a JsonSerializable line here too, and
    /// NativeReflectionAotTests fails if it is forgotten.
    /// </summary>
    [JsonSerializable(typeof(List<object?>))]
    [JsonSerializable(typeof(bool))]
    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(long))]
    [JsonSerializable(typeof(ulong))]
    [JsonSerializable(typeof(float))]
    [JsonSerializable(typeof(double))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(float[]))]
    internal partial class NativeReflectionJsonContext : JsonSerializerContext
    {
    }
}
```

- [ ] **Step 4: Route both serializer calls through the context**

In `Assets/Scripts/O3DE.Core/Reflection/NativeReflection.cs`, replace the body's serializer call at line 542 and the helper at lines 581-584:

```csharp
            return JsonSerializer.Serialize(elements, NativeReflectionJsonContext.Default.ListObject);
```

```csharp
        private static string SerializeValue(object value)
        {
            // Wrapped in a single-element list so the one registered
            // List<object?> type info covers both call sites; the context does
            // not need a second bare-object entry point.
            var single = new List<object?> { SerializeArgumentToObject(value) };
            string json = JsonSerializer.Serialize(single, NativeReflectionJsonContext.Default.ListObject);
            // Strip the wrapping brackets to preserve the previous bare-value shape.
            return json.Substring(1, json.Length - 2);
        }
```

Then add the test seam immediately after `SerializeArguments` (which ends at line 543):

```csharp
        /// <summary>
        /// Test seam over <c>SerializeArguments</c>. The wire format is a
        /// contract with the C++ marshaler
        /// (O3DESharp::Marshaling::JsonValueToBehaviorParameter) and a change
        /// to it breaks every EBus call silently at runtime, so it is asserted
        /// directly rather than inferred from a round trip.
        /// </summary>
        internal static string SerializeArgumentsForTest(object[] args) => SerializeArguments(args);
```

Finally, make that seam visible to the test assembly. Add to the top of `Assets/Scripts/O3DE.Core/Reflection/NativeReflection.cs`, after the `using` block (line 13):

```csharp
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("O3DE.Core.Tests")]
```

- [ ] **Step 5: Link the context into the test project**

Add to the `<ItemGroup>` at `Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj:36-47`:

```xml
    <Compile Include="..\O3DE.Core\Reflection\NativeReflectionJsonContext.cs" Link="O3DE.Core\Reflection\NativeReflectionJsonContext.cs" />
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~NativeReflectionAotTests"`
Expected: `Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9`

- [ ] **Step 7: Verify the four IL warnings for this file are gone**

Run: `dotnet build Assets/Scripts/O3DE.Core/O3DE.Core.csproj -c Release --nologo -p:IsAotCompatible=true 2>&1 | grep -c "NativeReflection.cs.*IL[0-9]"`
Expected: `0` (was 4 — `IL2026` and `IL3050` at `:542` and `:583`).

- [ ] **Step 8: Run the full managed suite**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo`
Expected: `Passed!  - Failed:     0, Passed:    95, Skipped:     0, Total:    95`

- [ ] **Step 9: Commit**

```bash
git add Assets/Scripts/O3DE.Core/Reflection/NativeReflectionJsonContext.cs Assets/Scripts/O3DE.Core/Reflection/NativeReflection.cs Assets/Scripts/O3DE.Core.Tests/NativeReflectionAotTests.cs Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj
git commit -m "M3: source-generate the NativeReflection argument serializer metadata

Reflection-based JsonSerializer.Serialize needs runtime code generation, which
a NativeAOT image does not have - the failure is a NotSupportedException at the
first EBus broadcast, not a build error. The registered set is exactly what
SerializeArgumentToObject can produce, and that set is closed by construction
because the method throws NotSupportedException on anything it has no case for.

The wire format is a contract with the C++ marshaler, so the tests assert the
exact output shape for every supported type rather than round-tripping; a
Vector3 that stops serializing as a 3-element number array breaks every EBus
call with a math argument, silently.

Clears 4 of the 14 IL warnings an IsAotCompatible build reports."
```

---

### Task 8: AOT-annotate the `ExposedProperty` reflection

**Files:**
- Modify: `Assets/Scripts/O3DE.Core/ExposedProperty.cs:83-109`
- Test: `Assets/Scripts/O3DE.Core.Tests/ExposedPropertyAotTests.cs`

**Interfaces:**
- Produces (consumed by Task 10): `static IEnumerable<ExposedMember> ExposedPropertyHelpers.Enumerate([DynamicallyAccessedMembers(...)] Type type, object instance)` — the annotated overload; the existing `Enumerate(object)` keeps its signature and delegates to it.

> `ExposedProperty.cs:91` and `:99` produce 4 of the 14 IL warnings (`IL2075` ×4 — two from `GetType()`, two from the `BaseType` walk). The `GetType()` pair is fixable with a proper annotation; the `BaseType` pair is not, because `Type.BaseType` carries no annotations in any .NET version. That residual is suppressed with an accurate justification rather than papered over, and the annotation is added now so a future trim pass inherits it.

- [ ] **Step 1: Write the failing test**

Create `Assets/Scripts/O3DE.Core.Tests/ExposedPropertyAotTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~ExposedPropertyAotTests"`
Expected: build failure — `error CS1501: No overload for method 'Enumerate' takes 2 arguments`.

- [ ] **Step 3: Split and annotate `Enumerate`**

In `Assets/Scripts/O3DE.Core/ExposedProperty.cs`, add `using System.Diagnostics.CodeAnalysis;` to the `using` block (after line 12), then replace `Enumerate` (lines 83-109) with:

```csharp
        /// <summary>
        /// Enumerate every <c>[ExposedProperty]</c>-decorated public field and
        /// public auto-property declared on <paramref name="instance"/>'s type
        /// (and its base types up to but not including <see cref="ScriptComponent"/>).
        /// </summary>
        [UnconditionalSuppressMessage(
            "Trimming", "IL2072",
            Justification =
                "instance.GetType() is unannotated by definition, and this walk is over user " +
                "script types the trimmer never sees the construction of anyway. The annotated " +
                "Type overload below is the one a future trim pass constrains; shipping AOT " +
                "images reach these types through generated ScriptTypeRegistry factories " +
                "(direct new T()), which keeps their members rooted.")]
        public static IEnumerable<ExposedMember> Enumerate(object instance)
        {
            if (instance is null) return System.Linq.Enumerable.Empty<ExposedMember>();
            return Enumerate(instance.GetType(), instance);
        }

        /// <summary>
        /// Type-explicit form of <see cref="Enumerate(object)"/>. The annotation
        /// is what tells the trim/AOT analyzer which members this walk needs; an
        /// unannotated GetFields call is its cue that anything may be removed.
        /// </summary>
        [UnconditionalSuppressMessage(
            "Trimming", "IL2075",
            Justification =
                "The base-type walk cannot be annotated - Type.BaseType carries no " +
                "DynamicallyAccessedMembers in any .NET version, so the analyzer cannot " +
                "propagate the parameter's annotation up the hierarchy. Shipping AOT is " +
                "published without an explicit trim pass and script types are rooted by " +
                "generated new T() factories, so base members are present. Enabling trimming " +
                "later must revisit this exact suppression.")]
        public static IEnumerable<ExposedMember> Enumerate(
            [DynamicallyAccessedMembers(
                DynamicallyAccessedMemberTypes.PublicFields |
                DynamicallyAccessedMemberTypes.NonPublicFields |
                DynamicallyAccessedMemberTypes.PublicProperties |
                DynamicallyAccessedMemberTypes.NonPublicProperties)]
            Type type,
            object instance)
        {
            if (type is null || instance is null) yield break;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type? current = type;
            while (current != null && current != typeof(object))
            {
                foreach (var field in current.GetFields(flags | BindingFlags.DeclaredOnly))
                {
                    var attr = field.GetCustomAttribute<ExposedPropertyAttribute>(inherit: true);
                    if (attr != null && !field.IsStatic)
                    {
                        yield return new ExposedMember(field, attr);
                    }
                }
                foreach (var prop in current.GetProperties(flags | BindingFlags.DeclaredOnly))
                {
                    var attr = prop.GetCustomAttribute<ExposedPropertyAttribute>(inherit: true);
                    if (attr != null && prop.CanRead && prop.CanWrite)
                    {
                        yield return new ExposedMember(prop, attr);
                    }
                }
                current = current.BaseType;
            }
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~ExposedPropertyAotTests"`
Expected: `Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5`

- [ ] **Step 5: Verify the four IL warnings for this file are gone**

Run: `dotnet build Assets/Scripts/O3DE.Core/O3DE.Core.csproj -c Release --nologo -p:IsAotCompatible=true 2>&1 | grep -c "ExposedProperty.cs.*IL[0-9]"`
Expected: `0` (was 4 — `IL2075` at `:91` and `:99`, two apiece).

- [ ] **Step 6: Run the full managed suite**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo`
Expected: `Passed!  - Failed:     0, Passed:   100, Skipped:     0, Total:   100`

- [ ] **Step 7: Commit**

```bash
git add Assets/Scripts/O3DE.Core/ExposedProperty.cs Assets/Scripts/O3DE.Core.Tests/ExposedPropertyAotTests.cs
git commit -m "M3: annotate the exposed-property reflection for trim/AOT analysis

The inspector walk stays reflective by design, but an unannotated GetFields
call is the analyzer's cue that anything may be removed. Splitting out a Type
overload carrying DynamicallyAccessedMembers fixes the two GetType() warnings
outright.

The base-type walk cannot be fixed the same way - Type.BaseType carries no
annotations in any .NET version - so that residual is suppressed with a
justification that says exactly why and what a future trim pass must revisit,
rather than a blanket pragma. A test asserts the annotation is present, so
dropping it fails the build rather than producing a warning someone ignores.

Clears 4 more of the 14 IL warnings."
```

---

### Task 9: Exclude the hot-reload reflection from shipping images

**Files:**
- Modify: `Assets/Scripts/O3DE.Core/HotReload/HotReloadManager.cs:1-397`
- Test: `Editor/Tests/test_hotreload_excluded_from_aot.py`

**Interfaces:**
- Produces: nothing new. `O3DE.Core.HotReload.*` simply does not exist when `O3DE_HOST_NATIVEAOT` is defined.

> This file is the last 4 of the 14 IL warnings (`IL2075` at `:180`, `IL2026` at `:220`, `IL2072` at `:230`, `IL2075` at `:240`) — the `Assembly.GetType` + `Activator.CreateInstance` pair the spec names explicitly. It has **no callers anywhere in the repo** (`git grep HotReloadManager` returns only its own file), so guarding the whole file out is safe and is strictly better than annotating machinery that a shipping build must never run: hot-reload is editor-only by design, and there is no ALC in a NativeAOT image to reload into.

- [ ] **Step 1: Write the failing test**

Create `Editor/Tests/test_hotreload_excluded_from_aot.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""HotReloadManager must not exist in a shipping NativeAOT image.

It is built entirely on Assembly.GetType + Activator.CreateInstance + field
reflection - the exact pattern a NativeAOT image cannot see through. Hot-reload
is editor-only by design (there is no AssemblyLoadContext to reload into), so
the file is guarded out rather than annotated: annotating machinery that must
never run in the shipping artifact would be work with no consumer.

Marked `slow` because it shells out to `dotnet build`.
"""

import re
import shutil
import subprocess
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
CORE_CSPROJ = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "O3DE.Core.csproj"
HOT_RELOAD = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "HotReload" / "HotReloadManager.cs"


@pytest.mark.unit
def test_file_is_guarded_by_the_host_mode_symbol():
    text = HOT_RELOAD.read_text(encoding="utf-8")
    assert re.search(r"^#if\s+!O3DE_HOST_NATIVEAOT\s*$", text, re.MULTILINE), (
        "HotReloadManager.cs must open with '#if !O3DE_HOST_NATIVEAOT'."
    )
    assert text.rstrip().endswith("#endif"), (
        "the guard must close at end of file so the whole type is excluded, "
        "not just part of it."
    )


@pytest.mark.unit
def test_file_still_has_no_callers():
    # The guard is only safe while nothing references it. If a caller appears,
    # that caller needs guarding too - and this test is the reminder.
    hits = []
    for path in (GEM_ROOT / "Assets").rglob("*.cs"):
        if path == HOT_RELOAD or "obj" in path.parts or "bin" in path.parts:
            continue
        if "HotReloadManager" in path.read_text(encoding="utf-8", errors="ignore"):
            hits.append(str(path))
    assert not hits, (
        "HotReloadManager gained callers: " + ", ".join(hits) +
        ". Guard them for O3DE_HOST_NATIVEAOT too, or the shipping build will not compile."
    )


@pytest.mark.slow
def test_nativeaot_mode_reports_no_il_warnings_for_the_file():
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    result = subprocess.run(
        [
            "dotnet", "build", str(CORE_CSPROJ), "-c", "Release", "--nologo",
            "-p:O3DESharpHostMode=NativeAot", "-p:IsAotCompatible=true", "-t:Rebuild",
        ],
        capture_output=True, text=True, timeout=900,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    offenders = re.findall(r"HotReloadManager\.cs.*?warning (IL\d+)", result.stdout)
    assert not offenders, (
        f"HotReloadManager.cs still reports {offenders} in NativeAot mode - the guard is not covering it."
    )
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest Editor/Tests/test_hotreload_excluded_from_aot.py -q`
Expected: `1 failed, 1 passed` — `AssertionError: HotReloadManager.cs must open with '#if !O3DE_HOST_NATIVEAOT'.` (`test_file_still_has_no_callers` passes; the `slow` test is deselected by `pytest.ini`'s `-m "not slow"`).

- [ ] **Step 3: Guard the file**

In `Assets/Scripts/O3DE.Core/HotReload/HotReloadManager.cs`, insert immediately after the copyright block (after line 7) and before `using System;`:

```csharp
// Hot-reload is editor-only by design. This whole file is Assembly.GetType +
// Activator.CreateInstance + field reflection over types loaded at runtime -
// precisely what a NativeAOT image cannot see through - and a shipping AOT
// build has no AssemblyLoadContext to reload into anyway, so there is nothing
// here for it to do.
//
// Guarding the file out rather than annotating it is deliberate: annotating
// machinery that must never run in the shipping artifact would be work with no
// consumer. Editor/Tests/test_hotreload_excluded_from_aot.py asserts this file
// still has no callers, because the moment one appears it needs guarding too.
#if !O3DE_HOST_NATIVEAOT
```

and append at end of file, after the final `}` (line 397):

```csharp
#endif // !O3DE_HOST_NATIVEAOT
```

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m pytest Editor/Tests/test_hotreload_excluded_from_aot.py -q -m "unit or slow"`
Expected: `3 passed`

- [ ] **Step 5: Verify both host modes still build and test green**

Run: `dotnet build Assets/Scripts/O3DE.Core/O3DE.Core.csproj -c Release --nologo -t:Rebuild && dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo`
Expected: `0 Error(s)`, then `Passed!  - Failed:     0, Passed:   100, Skipped:     0, Total:   100`

- [ ] **Step 6: Commit**

```bash
git add Assets/Scripts/O3DE.Core/HotReload/HotReloadManager.cs Editor/Tests/test_hotreload_excluded_from_aot.py
git commit -m "M3: exclude the hot-reload reflection from NativeAOT images

The file is Assembly.GetType + Activator.CreateInstance + field reflection over
runtime-loaded types - exactly what a NativeAOT image cannot see through - and
a shipping AOT build has no AssemblyLoadContext to reload into, so there is
nothing for it to do there.

Guarded rather than annotated because annotating machinery that must never run
in the shipping artifact is work with no consumer. It currently has no callers
anywhere in the repo, and a test asserts that stays true - the moment one
appears, that caller needs guarding too.

Clears the last 4 of the 14 IL warnings."
```

---

### Task 10: Enforce AOT-compatibility in the shipping mode

**Files:**
- Modify: `Assets/Scripts/O3DE.Core/O3DE.Core.csproj` (the `O3DESharpHostMode == 'NativeAot'` `PropertyGroup` added in Task 3)
- Test: `Editor/Tests/test_aot_clean_build.py`

**Interfaces:**
- Produces: `IsAotCompatible=true` plus `IL2026;IL2072;IL2075;IL3050;IL3051` promoted to errors, **only** in `NativeAot` mode.

> Scoping the analyzers to `NativeAot` mode is deliberate: `HotReloadManager.cs` is compiled in `Coral` mode and would still warn there, and the editor build must stay exactly as it is (behaviour-preserving). This way the shipping artifact is enforced AOT-clean and the editor build is untouched — a new AOT hazard fails the shipping build instead of accumulating as an ignored warning.

- [ ] **Step 1: Write the failing test**

Create `Editor/Tests/test_aot_clean_build.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""The shipping build config must be AOT-clean, and enforced as such.

An IL2xxx/IL3xxx warning in a NativeAOT build is a runtime failure waiting to
happen - reflection over a type the compiler removed, or serialization needing
codegen that is not there. As a warning it accumulates unnoticed; as an error
it cannot.

Deliberately scoped to NativeAot mode only: the editor build is behaviour-
preserving in M3 and must not gain new failure modes.

Marked `slow` because it shells out to `dotnet build`.
"""

import re
import shutil
import subprocess
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
CORE_CSPROJ = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "O3DE.Core.csproj"


def _build(*extra):
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    return subprocess.run(
        ["dotnet", "build", str(CORE_CSPROJ), "-c", "Release", "--nologo", "-t:Rebuild", *extra],
        capture_output=True, text=True, timeout=900,
    )


@pytest.mark.unit
def test_analyzers_are_scoped_to_the_shipping_mode_only():
    text = CORE_CSPROJ.read_text(encoding="utf-8")
    block = re.search(
        r"<PropertyGroup Condition=\"'\$\(O3DESharpHostMode\)'\s*==\s*'NativeAot'\">(.*?)</PropertyGroup>",
        text, re.DOTALL,
    )
    assert block, "the NativeAot PropertyGroup must exist"
    body = block.group(1)
    assert "<IsAotCompatible>true</IsAotCompatible>" in body
    for code in ("IL2026", "IL2072", "IL2075", "IL3050", "IL3051"):
        assert code in body, f"{code} must be promoted to an error in the shipping mode"
    assert "IsAotCompatible" not in text.replace(body, ""), (
        "IsAotCompatible must not be set outside the NativeAot condition; the editor "
        "build is behaviour-preserving in M3."
    )


@pytest.mark.slow
def test_nativeaot_mode_builds_with_zero_il_diagnostics():
    result = _build("-p:O3DESharpHostMode=NativeAot")
    assert result.returncode == 0, result.stdout + result.stderr
    diagnostics = re.findall(r"(?:warning|error) (IL\d+)", result.stdout)
    assert not diagnostics, f"shipping build is not AOT-clean: {sorted(set(diagnostics))}"


@pytest.mark.slow
def test_coral_mode_is_unaffected():
    result = _build()
    assert result.returncode == 0, result.stdout + result.stderr
    assert "IsAotCompatible" not in result.stdout
    # The editor build keeps its pre-existing CS warnings and gains no IL ones.
    assert not re.findall(r"(?:warning|error) (IL\d+)", result.stdout)
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest Editor/Tests/test_aot_clean_build.py -q`
Expected: `1 failed` — `AssertionError: <IsAotCompatible>true</IsAotCompatible>` is not in the `NativeAot` `PropertyGroup` body.

- [ ] **Step 3: Turn the analyzers on for the shipping mode**

In `Assets/Scripts/O3DE.Core/O3DE.Core.csproj`, replace the `NativeAot` `PropertyGroup` added in Task 3 with:

```xml
  <PropertyGroup Condition="'$(O3DESharpHostMode)' == 'NativeAot'">
    <DefineConstants>$(DefineConstants);O3DE_HOST_NATIVEAOT</DefineConstants>

    <!--
      IsAotCompatible turns on the trim, AOT and single-file analyzers. An
      IL2xxx/IL3xxx warning in a NativeAOT build is a runtime failure waiting
      to happen - reflection over a type the compiler removed, or serialization
      that needs codegen the image does not have - so they are errors here,
      not warnings that accumulate unnoticed.

      Scoped to this mode only. The editor build is behaviour-preserving in M3
      and must not gain new failure modes; HotReloadManager.cs is compiled in
      Coral mode and would legitimately warn there.

      Note this does NOT enable trimming of the CoreCLR artifacts - nothing in
      this repo sets PublishTrimmed. NativeAOT necessarily eliminates dead code
      as part of compiling, which is a property of the toolchain rather than an
      opt-in here.
    -->
    <IsAotCompatible>true</IsAotCompatible>
    <WarningsAsErrors>$(WarningsAsErrors);IL2026;IL2072;IL2075;IL3050;IL3051</WarningsAsErrors>
  </PropertyGroup>
```

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m pytest Editor/Tests/test_aot_clean_build.py -q -m "unit or slow"`
Expected: `3 passed`

- [ ] **Step 5: Full regression in both modes**

Run: `dotnet build Assets/Scripts/O3DE.Core/O3DE.Core.csproj -c Release --nologo -t:Rebuild -p:O3DESharpHostMode=NativeAot && dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo`
Expected: `0 Error(s)` and no `IL` lines for the shipping build, then `Passed!  - Failed:     0, Passed:   100, Skipped:     0, Total:   100`

- [ ] **Step 6: Commit**

```bash
git add Assets/Scripts/O3DE.Core/O3DE.Core.csproj Editor/Tests/test_aot_clean_build.py
git commit -m "M3: make the shipping build config enforce AOT-cleanliness

An IL2xxx/IL3xxx warning in a NativeAOT build is a runtime failure waiting to
happen; as a warning it accumulates unnoticed, as an error it cannot. All 14
that an IsAotCompatible build previously reported are now fixed, so the gate
closes on a clean slate.

Scoped to NativeAot mode only. The editor build is behaviour-preserving in M3
and must not gain new failure modes, and HotReloadManager.cs legitimately warns
in Coral mode where it is still compiled."
```

---
### Task 11: `IManagedHost` and the `CoralHost` adapter

**Files:**
- Create: `Code/Source/Scripting/IManagedHost.h`
- Create: `Code/Source/Scripting/CoralHost.h`
- Create: `Code/Source/Scripting/CoralHost.cpp`
- Modify: `Code/Source/Scripting/ScriptBindings.h:72-79`
- Modify: `Code/Source/Scripting/ScriptBindings.cpp:31-122`
- Modify: `Code/o3desharp_private_files.cmake:20-28`
- Test: `Editor/Tests/test_managed_host_seam.py`

**Interfaces:**
- Consumes: `O3DESharp::Abi::NativeImports` / `ManagedExports` / `HostAbiVersion` (Task 2); `CoralHostManager`, `CoralHostConfig`, `CoralHostStatus`, `ICoralHostManager::GetHostInstance()`, `::GetScriptsDirectory()` (existing, `CoralHostManager.h:73-156`); `CoralNativeThunkHost::SetHost/Get/InvalidateCache` (existing, `CoralNativeThunkHost.h:32-66`).
- Produces (consumed by Tasks 12, 13):
  - `class O3DESharp::IManagedHost` with `Initialize(const Abi::NativeImports&) -> CoralHostStatus`, `GetExports() const -> const Abi::ManagedExports*`, `SupportsHotReload() const -> bool`, `Shutdown()`
  - `using ManagedHostInterface = AZ::Interface<IManagedHost>`
  - `class O3DESharp::CoralHost final : public IManagedHost` — constructed over an existing `CoralHostManager&`
  - `static Abi::NativeImports ScriptBindings::MakeNativeImports()`

> **This is a wrapping refactor, not a rewrite.** `CoralHost` owns nothing: it holds a reference to the `CoralHostManager` the system component already creates, and delegates. Under Coral the transport for `NativeImports` remains `assembly->AddInternalCall` + `UploadInternalCalls` in `ScriptBindings::RegisterAll` (`ScriptBindings.cpp:46-119`) — untouched. `MakeNativeImports` builds the same pointers into the frozen struct so both ends provably agree on the order; the struct is descriptive in the editor and load-bearing only under NativeAOT. Saying that plainly is the point: pretending the editor suddenly routes through the struct would be a behaviour change M3 explicitly forbids.

- [ ] **Step 1: Write the failing test**

Create `Editor/Tests/test_managed_host_seam.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Structural guards for the IManagedHost seam.

None of this C++ can be compiled in the authoring environment (no O3DE engine
SDK), so the properties that matter and are checkable from source are checked
here: the interface has exactly the four methods the design specifies, CoralHost
implements all of them, MakeNativeImports assigns every one of the 47 ABI
fields, and the wrapping refactor did not delete the Coral upload path it is
supposed to preserve.
"""

import re
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
SCRIPTING = GEM_ROOT / "Code" / "Source" / "Scripting"
IMANAGED_HOST = SCRIPTING / "IManagedHost.h"
CORAL_HOST_H = SCRIPTING / "CoralHost.h"
CORAL_HOST_CPP = SCRIPTING / "CoralHost.cpp"
SCRIPT_BINDINGS_CPP = SCRIPTING / "ScriptBindings.cpp"
HOST_ABI_H = SCRIPTING / "HostAbi.h"

REQUIRED_METHODS = ["Initialize", "GetExports", "SupportsHotReload", "Shutdown"]


def _read(path):
    assert path.is_file(), f"{path} is missing."
    return path.read_text(encoding="utf-8")


@pytest.mark.unit
def test_interface_declares_exactly_the_four_designed_methods():
    text = _read(IMANAGED_HOST)
    declared = re.findall(r"virtual\s+[\w:&*<>\s]+?\s(\w+)\s*\([^)]*\)\s*(?:const\s*)?=\s*0\s*;", text)
    assert sorted(declared) == sorted(REQUIRED_METHODS), (
        f"IManagedHost must declare exactly {REQUIRED_METHODS}, found {declared}. "
        "Widening the interface widens what every backend has to implement."
    )


@pytest.mark.unit
def test_coral_host_implements_all_four():
    text = _read(CORAL_HOST_H)
    for method in REQUIRED_METHODS:
        assert re.search(rf"\b{method}\s*\([^)]*\)\s*(?:const\s*)?override\s*;", text), (
            f"CoralHost must override {method}."
        )


@pytest.mark.unit
def test_make_native_imports_assigns_every_abi_field():
    abi_fields = re.findall(r"void\*\s+(\w+)\s*;", _read(HOST_ABI_H))
    native_imports = abi_fields[: abi_fields.index("Component_HasComponent") + 1]

    body = _read(SCRIPT_BINDINGS_CPP)
    m = re.search(r"MakeNativeImports\s*\(\s*\)", body)
    assert m, "ScriptBindings::MakeNativeImports must exist."
    tail = body[m.end():]

    missing = [f for f in native_imports if not re.search(rf"imports\.{f}\s*=", tail)]
    assert not missing, (
        f"MakeNativeImports leaves these ABI fields unassigned: {missing}. "
        "An unassigned field is a null pointer the managed side may call."
    )


@pytest.mark.unit
def test_make_native_imports_stamps_the_version():
    assert re.search(r"imports\.version\s*=\s*Abi::HostAbiVersion\s*;", _read(SCRIPT_BINDINGS_CPP)), (
        "MakeNativeImports must stamp the version, or the managed side cannot "
        "distinguish a populated struct from a zeroed one."
    )


@pytest.mark.unit
def test_the_coral_upload_path_is_preserved():
    # M3 is behaviour-preserving. The struct is descriptive under Coral; the
    # actual transport is still AddInternalCall + UploadInternalCalls.
    text = _read(SCRIPT_BINDINGS_CPP)
    assert "UploadInternalCalls()" in text, (
        "RegisterAll must still upload internal calls - the ABI struct does NOT "
        "replace Coral's transport in M3."
    )
    assert text.count("AddInternalCall") >= 47, (
        "the 47 AddInternalCall registrations must survive the refactor unchanged."
    )


@pytest.mark.unit
def test_coral_host_reports_hot_reload_from_the_config():
    text = _read(CORAL_HOST_CPP)
    assert "SupportsHotReload" in text and "enableHotReload" in text, (
        "CoralHost::SupportsHotReload must report the existing "
        "CoralHostConfig::enableHotReload gate rather than a hard-coded true."
    )


@pytest.mark.unit
def test_new_sources_are_in_the_build_file_list():
    files_cmake = (GEM_ROOT / "Code" / "o3desharp_private_files.cmake").read_text(encoding="utf-8")
    for entry in ("Source/Scripting/IManagedHost.h",
                  "Source/Scripting/CoralHost.h",
                  "Source/Scripting/CoralHost.cpp"):
        assert entry in files_cmake, f"{entry} is missing from o3desharp_private_files.cmake"
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest Editor/Tests/test_managed_host_seam.py -q`
Expected: `6 failed, 1 passed` — the first five fail with `AssertionError: .../IManagedHost.h is missing.` / `.../CoralHost.h is missing.`, and the file-list check fails; `test_the_coral_upload_path_is_preserved` passes already against today's `ScriptBindings.cpp`.

- [ ] **Step 3: Declare the interface**

Create `Code/Source/Scripting/IManagedHost.h`:

```cpp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/RTTI/RTTI.h>
#include <AzCore/Interface/Interface.h>

#include <Scripting/HostAbi.h>
#include <Scripting/CoralHostManager.h>

namespace O3DESharp
{
    //! The one seam between the C++ gem and whatever is hosting the managed
    //! scripting layer.
    //!
    //! Implementations differ ONLY in how the two ABI structs are exchanged:
    //!
    //!   CoralHost     - editor. NativeImports go up through Coral's
    //!                   AddInternalCall/UploadInternalCalls; ManagedExports
    //!                   come back as [UnmanagedCallersOnly] statics resolved
    //!                   by name through CoralNativeThunkHost. Hot-reload
    //!                   re-resolves the exports per ALC swap.
    //!
    //!   NativeAotHost - desktop shipping. The managed side is a NativeAOT
    //!                   native library; C++ dlopen/LoadLibrary's it and
    //!                   resolves one exported symbol. The direction is
    //!                   inverted - C++ IMPORTS exports rather than uploading
    //!                   calls - and there is no Coral and no hostfxr at all.
    //!
    //! Kept to exactly four methods on purpose: every additional one is
    //! something every future backend has to implement.
    class IManagedHost
    {
    public:
        AZ_RTTI(IManagedHost, "{2E4E4E1B-6C3B-4E5B-9E3B-0B1D9C6A5F21}");

        virtual ~IManagedHost() = default;

        //! Hand the native import table to the managed side and bring the host
        //! up. The caller builds the struct with
        //! ScriptBindings::MakeNativeImports().
        virtual CoralHostStatus Initialize(const Abi::NativeImports& imports) = 0;

        //! The managed export table, or nullptr before a successful Initialize
        //! (or after a failed export resolve). Callers MUST null-check: on the
        //! Coral path a failed resolve is survivable and falls back to
        //! ManagedObject::InvokeMethod, exactly as SP-1a established.
        virtual const Abi::ManagedExports* GetExports() const = 0;

        //! False on shipping AOT backends. Hot-reload is editor-only by design:
        //! a NativeAOT image has no AssemblyLoadContext to reload into.
        virtual bool SupportsHotReload() const = 0;

        //! Tear the host down. Must be safe to call when Initialize failed or
        //! was never called.
        virtual void Shutdown() = 0;
    };

    using ManagedHostInterface = AZ::Interface<IManagedHost>;
} // namespace O3DESharp
```

- [ ] **Step 4: Write the Coral adapter header**

Create `Code/Source/Scripting/CoralHost.h`:

```cpp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/Memory/SystemAllocator.h>

#include <Scripting/IManagedHost.h>
#include <Scripting/CoralNativeThunkHost.h>

namespace O3DESharp
{
    //! IManagedHost over the existing CoralHostManager.
    //!
    //! A WRAPPING refactor, not a rewrite: this class owns nothing. It holds a
    //! reference to the CoralHostManager O3DESharpSystemComponent already
    //! creates and delegates to it, so every behaviour of the editor path -
    //! ALC lifecycle, the unified core/user context, hot-reload broadcast
    //! ordering - is exactly what it was.
    //!
    //! What it adds is the seam: Initialize takes the frozen NativeImports
    //! struct, and GetExports resolves the five ManagedExports thunks once
    //! through CoralNativeThunkHost (the SP-1a memoizing cache over Coral's
    //! GetFunctionPointer).
    //!
    //! Deliberate and worth stating plainly: under Coral the NativeImports
    //! struct is DESCRIPTIVE. The transport is still
    //! assembly->AddInternalCall + UploadInternalCalls in
    //! ScriptBindings::RegisterAll, untouched. Building the struct anyway is
    //! what proves both ends agree on the frozen field order (the golden test
    //! in Editor/Tests/test_host_abi_contract.py checks the declarations; this
    //! checks the population). It becomes the sole transport only under
    //! NativeAotHost.
    class CoralHost final
        : public IManagedHost
    {
    public:
        AZ_RTTI(CoralHost, "{9C1F0F2A-2B77-4E33-9E2A-77B0E2C4A913}", IManagedHost);
        AZ_CLASS_ALLOCATOR(CoralHost, AZ::SystemAllocator);

        //! The manager must outlive this adapter; O3DESharpSystemComponent owns
        //! both and destroys the adapter first.
        explicit CoralHost(CoralHostManager& manager, const CoralHostConfig& config);
        ~CoralHost() override;

        // IManagedHost
        CoralHostStatus Initialize(const Abi::NativeImports& imports) override;
        const Abi::ManagedExports* GetExports() const override;
        bool SupportsHotReload() const override;
        void Shutdown() override;

        //! Drop the resolved export pointers. MUST be called on assembly reload
        //! - an [UnmanagedCallersOnly] pointer into an unloaded ALC is
        //! dangling, and the failure is a crash in managed code with no obvious
        //! link back to the missing call.
        void InvalidateExports();

    private:
        //! Resolve the five thunks. Returns false if any is unavailable, in
        //! which case m_exportsValid stays false and GetExports returns nullptr
        //! so callers fall back to InvokeMethod (SP-1a's first-class fallback).
        bool ResolveExports();

        CoralHostManager& m_manager;
        CoralHostConfig m_config;
        CoralNativeThunkHost m_thunkHost;
        Abi::ManagedExports m_exports{};
        bool m_exportsValid = false;
    };
} // namespace O3DESharp
```

- [ ] **Step 5: Write the Coral adapter implementation**

Create `Code/Source/Scripting/CoralHost.cpp`:

```cpp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "CoralHost.h"

#include <AzCore/Console/ILogger.h>

namespace O3DESharp
{
    namespace
    {
        // Assembly-qualified name of the generated thunk holder. Emitted by
        // HostExportsGenerator into O3DE.Core as
        // O3DE.Interop.ManagedExportsThunks.
        constexpr const char* ExportsAssembly = "O3DE.Core.dll";
        constexpr const char* ExportsTypeName = "O3DE.Interop.ManagedExportsThunks, O3DE.Core";
    } // namespace

    CoralHost::CoralHost(CoralHostManager& manager, const CoralHostConfig& config)
        : m_manager(manager)
        , m_config(config)
    {
    }

    CoralHost::~CoralHost()
    {
        // Deliberately does NOT call Shutdown(): the manager is owned by
        // O3DESharpSystemComponent, which shuts it down on its own schedule.
        // Tearing down someone else's host from a destructor would change the
        // editor's shutdown ordering, which M3 must not do.
        InvalidateExports();
    }

    CoralHostStatus CoralHost::Initialize(const Abi::NativeImports& imports)
    {
        if (imports.version != Abi::HostAbiVersion)
        {
            AZLOG_ERROR(
                "CoralHost::Initialize - NativeImports version %u does not match host ABI version %u; refusing to run",
                imports.version,
                Abi::HostAbiVersion);
            return CoralHostStatus::CoralInitError;
        }

        // The manager owns CLR bring-up, assembly loading and the
        // AddInternalCall/UploadInternalCalls upload of exactly these pointers.
        // Nothing about that path changes here.
        const CoralHostStatus status = m_manager.Initialize(m_config);
        if (status != CoralHostStatus::Success)
        {
            return status;
        }

        m_thunkHost.SetHost(m_manager.GetHostInstance(), m_manager.GetScriptsDirectory());

        if (!ResolveExports())
        {
            // Survivable: GetExports() returns nullptr and callers fall back to
            // ManagedObject::InvokeMethod, exactly as SP-1a established. A
            // missing thunk costs speed, never correctness.
            AZLOG_WARN(
                "CoralHost: ManagedExports could not be fully resolved - callers will fall back to InvokeMethod");
        }

        return CoralHostStatus::Success;
    }

    bool CoralHost::ResolveExports()
    {
        m_exportsValid = false;
        m_exports = {};

        auto resolve = [this](const char* methodName) -> void*
        {
            return m_thunkHost.Get(ExportsAssembly, ExportsTypeName, methodName);
        };

        Abi::ManagedExports exports{};
        exports.version = Abi::HostAbiVersion;
        exports.CreateInstance = resolve("O3DESharp_CreateInstance");
        exports.InvokeLifecycle = resolve("O3DESharp_InvokeLifecycle");
        exports.DispatchEBusEvent = resolve("O3DESharp_DispatchEBusEvent");
        exports.DestroyInstance = resolve("O3DESharp_DestroyInstance");
        exports.HotReloadSwap = resolve("O3DESharp_HotReloadSwap");

        // All-or-nothing on purpose. A partially-populated table would let a
        // caller find one pointer, skip its fallback, and then hit a null on
        // the next one - a much harder failure to read than "no exports".
        if (exports.CreateInstance == nullptr || exports.InvokeLifecycle == nullptr ||
            exports.DispatchEBusEvent == nullptr || exports.DestroyInstance == nullptr ||
            exports.HotReloadSwap == nullptr)
        {
            return false;
        }

        m_exports = exports;
        m_exportsValid = true;
        AZLOG_INFO("CoralHost: resolved all %d ManagedExports thunks", 5);
        return true;
    }

    const Abi::ManagedExports* CoralHost::GetExports() const
    {
        return m_exportsValid ? &m_exports : nullptr;
    }

    bool CoralHost::SupportsHotReload() const
    {
        // Reports the existing gate rather than a hard-coded true:
        // O3DESharpSystemComponent sets enableHotReload on in Debug/Profile and
        // off in Release (O3DESharpSystemComponent.cpp:793-797).
        return m_config.enableHotReload;
    }

    void CoralHost::InvalidateExports()
    {
        m_exportsValid = false;
        m_exports = {};
        m_thunkHost.InvalidateCache();
    }

    void CoralHost::Shutdown()
    {
        InvalidateExports();
        m_manager.Shutdown();
    }
} // namespace O3DESharp
```

- [ ] **Step 6: Add `MakeNativeImports`**

In `Code/Source/Scripting/ScriptBindings.h`, add the include after line 18 (`#include <Coral/String.hpp>`):

```cpp
#include <Scripting/HostAbi.h>
```

and declare it in the public section, after `RegisterAll` (line 79):

```cpp
        /**
         * Build the frozen NativeImports table from the same function pointers
         * RegisterAll uploads.
         *
         * Under Coral this struct is DESCRIPTIVE - the transport is still
         * AddInternalCall + UploadInternalCalls and nothing about that changes.
         * Building it anyway is what proves the C++ and managed ends agree on
         * the frozen field ORDER, which is the half sizeof() cannot see. Under
         * NativeAotHost it is the sole transport.
         */
        static Abi::NativeImports MakeNativeImports();
```

In `Code/Source/Scripting/ScriptBindings.cpp`, add immediately after `RegisterAll` (which ends at line 122):

```cpp
    Abi::NativeImports ScriptBindings::MakeNativeImports()
    {
        // Field order here IS the ABI and must match, exactly:
        //   Assets/Scripts/O3DE.Core/Interop/HostAbi.cs
        //   Code/Source/Scripting/HostAbi.h
        //   Assets/Scripts/O3DE.Core/InternalCalls.cs
        // Editor/Tests/test_host_abi_contract.py fails the build if the three
        // declarations drift; test_managed_host_seam.py fails if any field here
        // is left unassigned, because an unassigned field is a null pointer the
        // managed side may call.
        Abi::NativeImports imports{};
        imports.version = Abi::HostAbiVersion;

        imports.Log_Info = reinterpret_cast<void*>(&Log_Info);
        imports.Log_Warning = reinterpret_cast<void*>(&Log_Warning);
        imports.Log_Error = reinterpret_cast<void*>(&Log_Error);

        imports.Entity_IsValid = reinterpret_cast<void*>(&Entity_IsValid);
        imports.Entity_GetName = reinterpret_cast<void*>(&Entity_GetName);
        imports.Entity_SetName = reinterpret_cast<void*>(&Entity_SetName);
        imports.Entity_IsActive = reinterpret_cast<void*>(&Entity_IsActive);
        imports.Entity_Activate = reinterpret_cast<void*>(&Entity_Activate);
        imports.Entity_Deactivate = reinterpret_cast<void*>(&Entity_Deactivate);
        imports.Entity_Destroy = reinterpret_cast<void*>(&Entity_Destroy);
        imports.Entity_FindByName = reinterpret_cast<void*>(&Entity_FindByName);
        imports.Entity_GetChildCount = reinterpret_cast<void*>(&Entity_GetChildCount);
        imports.Entity_GetChildAtIndex = reinterpret_cast<void*>(&Entity_GetChildAtIndex);
        imports.Entity_GetChildren = reinterpret_cast<void*>(&Entity_GetChildren);

        imports.Transform_GetWorldPosition = reinterpret_cast<void*>(&Transform_GetWorldPosition);
        imports.Transform_SetWorldPosition = reinterpret_cast<void*>(&Transform_SetWorldPosition);
        imports.Transform_GetLocalPosition = reinterpret_cast<void*>(&Transform_GetLocalPosition);
        imports.Transform_SetLocalPosition = reinterpret_cast<void*>(&Transform_SetLocalPosition);
        imports.Transform_GetWorldRotation = reinterpret_cast<void*>(&Transform_GetWorldRotation);
        imports.Transform_SetWorldRotation = reinterpret_cast<void*>(&Transform_SetWorldRotation);
        imports.Transform_GetWorldRotationEuler = reinterpret_cast<void*>(&Transform_GetWorldRotationEuler);
        imports.Transform_SetWorldRotationEuler = reinterpret_cast<void*>(&Transform_SetWorldRotationEuler);
        imports.Transform_GetLocalScale = reinterpret_cast<void*>(&Transform_GetLocalScale);
        imports.Transform_SetLocalScale = reinterpret_cast<void*>(&Transform_SetLocalScale);
        imports.Transform_GetLocalUniformScale = reinterpret_cast<void*>(&Transform_GetLocalUniformScale);
        imports.Transform_SetLocalUniformScale = reinterpret_cast<void*>(&Transform_SetLocalUniformScale);
        imports.Transform_GetForward = reinterpret_cast<void*>(&Transform_GetForward);
        imports.Transform_GetRight = reinterpret_cast<void*>(&Transform_GetRight);
        imports.Transform_GetUp = reinterpret_cast<void*>(&Transform_GetUp);
        imports.Transform_GetParentId = reinterpret_cast<void*>(&Transform_GetParentId);
        imports.Transform_SetParent = reinterpret_cast<void*>(&Transform_SetParent);

        imports.Input_IsKeyDown = reinterpret_cast<void*>(&Input_IsKeyDown);
        imports.Input_IsKeyPressed = reinterpret_cast<void*>(&Input_IsKeyPressed);
        imports.Input_IsKeyReleased = reinterpret_cast<void*>(&Input_IsKeyReleased);
        imports.Input_IsMouseButtonDown = reinterpret_cast<void*>(&Input_IsMouseButtonDown);
        imports.Input_IsMouseButtonPressed = reinterpret_cast<void*>(&Input_IsMouseButtonPressed);
        imports.Input_IsMouseButtonReleased = reinterpret_cast<void*>(&Input_IsMouseButtonReleased);
        imports.Input_GetMousePosition = reinterpret_cast<void*>(&Input_GetMousePosition);
        imports.Input_GetMouseDelta = reinterpret_cast<void*>(&Input_GetMouseDelta);
        imports.Input_GetAxis = reinterpret_cast<void*>(&Input_GetAxis);

        imports.Time_GetDeltaTime = reinterpret_cast<void*>(&Time_GetDeltaTime);
        imports.Time_GetTotalTime = reinterpret_cast<void*>(&Time_GetTotalTime);
        imports.Time_GetTimeScale = reinterpret_cast<void*>(&Time_GetTimeScale);
        imports.Time_SetTimeScale = reinterpret_cast<void*>(&Time_SetTimeScale);
        imports.Time_GetFrameCount = reinterpret_cast<void*>(&Time_GetFrameCount);

        imports.Physics_Raycast = reinterpret_cast<void*>(&Physics_Raycast);

        imports.Component_HasComponent = reinterpret_cast<void*>(&Component_HasComponent);

        return imports;
    }
```

- [ ] **Step 7: Add the new sources to the build file list**

In `Code/o3desharp_private_files.cmake`, in the `# C# Scripting Support via Coral` block, alongside `Source/Scripting/HostAbi.h`:

```cmake
    Source/Scripting/IManagedHost.h
    Source/Scripting/CoralHost.h
    Source/Scripting/CoralHost.cpp
```

- [ ] **Step 8: Run test to verify it passes**

Run: `python -m pytest Editor/Tests/test_managed_host_seam.py -q`
Expected: `7 passed`

- [ ] **Step 9: Re-run the golden ABI contract test**

Run: `python -m pytest Editor/Tests/test_host_abi_contract.py -q`
Expected: `5 passed` (unchanged — this task adds a populator, not a declaration).

- [ ] **Step 10: Commit**

```bash
git add Code/Source/Scripting/IManagedHost.h Code/Source/Scripting/CoralHost.h Code/Source/Scripting/CoralHost.cpp Code/Source/Scripting/ScriptBindings.h Code/Source/Scripting/ScriptBindings.cpp Code/o3desharp_private_files.cmake Editor/Tests/test_managed_host_seam.py
git commit -m "M3: introduce IManagedHost and wrap CoralHostManager behind it

A wrapping refactor, not a rewrite: CoralHost owns nothing, holds a reference
to the manager the system component already creates, and delegates. ALC
lifecycle, the unified core/user context and the hot-reload broadcast ordering
are untouched.

Under Coral the NativeImports struct is descriptive - AddInternalCall plus
UploadInternalCalls remains the transport - and MakeNativeImports exists to
prove both ends agree on the frozen field ORDER, the half sizeof() cannot see.
It becomes the sole transport only under NativeAotHost.

Export resolution is all-or-nothing: a partially-populated table would let a
caller find one pointer, skip its fallback, and hit a null on the next, which
is far harder to read than 'no exports'. A failed resolve is survivable and
falls back to InvokeMethod, as SP-1a established.

The interface is deliberately four methods; each additional one is something
every future backend must implement.

NOT COMPILE-VERIFIED: no O3DE engine SDK in the authoring environment."
```

---
### Task 12: Register `CoralHost` from the system component

**Files:**
- Modify: `Code/Source/Clients/O3DESharpSystemComponent.h:138-140`
- Modify: `Code/Source/Clients/O3DESharpSystemComponent.cpp:810-870`
- Test: `Editor/Tests/test_managed_host_seam.py` (extend)

**Interfaces:**
- Consumes: `CoralHost` ctor, `IManagedHost`, `ManagedHostInterface` (Task 11); `ScriptBindings::MakeNativeImports()` (Task 11).
- Produces: `AZ::Interface<IManagedHost>` registered for the lifetime of the gem, alongside the existing `CoralHostManagerInterface`.

> The system component keeps `m_coralHostManager` and keeps registering `CoralHostManagerInterface` exactly as it does today (`O3DESharpSystemComponent.cpp:819`). `CoralHost` is constructed *over* it and registered as a second interface. Nothing that exists today changes consumer; the seam is live and exercised, and M4 has an interface to swap the implementation behind.

- [ ] **Step 1: Write the failing test**

Append to `Editor/Tests/test_managed_host_seam.py`:

```python
SYSTEM_COMPONENT_H = GEM_ROOT / "Code" / "Source" / "Clients" / "O3DESharpSystemComponent.h"
SYSTEM_COMPONENT_CPP = GEM_ROOT / "Code" / "Source" / "Clients" / "O3DESharpSystemComponent.cpp"


@pytest.mark.unit
def test_system_component_owns_a_managed_host():
    header = _read(SYSTEM_COMPONENT_H)
    assert re.search(r"AZStd::unique_ptr<CoralHost>\s+m_managedHost\s*;", header), (
        "O3DESharpSystemComponent must own the CoralHost adapter."
    )
    assert "AZStd::unique_ptr<CoralHostManager> m_coralHostManager;" in header, (
        "the existing manager member must survive - M3 wraps it, it does not replace it."
    )


@pytest.mark.unit
def test_both_interfaces_are_registered_and_unregistered():
    body = _read(SYSTEM_COMPONENT_CPP)
    # The pre-existing registration must not be disturbed.
    assert "CoralHostManagerInterface::Register(m_coralHostManager.get())" in body
    assert "CoralHostManagerInterface::Unregister(m_coralHostManager.get())" in body
    # ...and the new seam registered alongside it.
    assert "ManagedHostInterface::Register(m_managedHost.get())" in body
    assert "ManagedHostInterface::Unregister(m_managedHost.get())" in body


@pytest.mark.unit
def test_the_host_is_initialised_through_the_abi_struct():
    body = _read(SYSTEM_COMPONENT_CPP)
    assert "ScriptBindings::MakeNativeImports()" in body, (
        "the seam must actually be exercised in the editor path, not just declared."
    )
    assert re.search(r"m_managedHost->Initialize\(", body), (
        "InitializeCoralHost must drive the manager THROUGH CoralHost::Initialize, "
        "so the editor exercises the same entry point NativeAotHost will."
    )


@pytest.mark.unit
def test_manager_is_not_initialised_twice():
    body = _read(SYSTEM_COMPONENT_CPP)
    # CoralHost::Initialize calls m_manager.Initialize(config). A surviving
    # direct call here would run CLR bring-up twice and return
    # AlreadyInitialized on the second, which reads as a spurious warning.
    assert "m_coralHostManager->Initialize(config)" not in body, (
        "CoralHost::Initialize already drives the manager; a direct call here "
        "would initialise it twice."
    )
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest Editor/Tests/test_managed_host_seam.py -q`
Expected: `4 failed, 7 passed` — the four new checks fail (`m_managedHost` does not exist; `ManagedHostInterface::Register` is absent; `MakeNativeImports()` is not called; `m_coralHostManager->Initialize(config)` is still present).

- [ ] **Step 3: Add the member**

In `Code/Source/Clients/O3DESharpSystemComponent.h`, add the forward declaration next to the existing `class CoralHostManager;` (line 19):

```cpp
    class CoralHost;
```

and add the member immediately after `m_coralHostManager` (line 140):

```cpp
        // M3: the IManagedHost adapter over m_coralHostManager. Owns nothing;
        // the manager above stays the owner of the CLR. Declared AFTER the
        // manager so it is destroyed BEFORE it - the adapter holds a reference.
        AZStd::unique_ptr<CoralHost> m_managedHost;
```

Add the include next to `#include <Scripting/CoralHostManager.h>` in the .cpp (line 28):

```cpp
#include <Scripting/CoralHost.h>
```

- [ ] **Step 4: Drive initialization through the seam**

In `Code/Source/Clients/O3DESharpSystemComponent.cpp`, replace the initialization call at line 811 (`CoralHostStatus status = m_coralHostManager->Initialize(config);`) with:

```cpp
        // M3: initialise THROUGH the IManagedHost seam rather than calling the
        // manager directly, so the editor exercises the same entry point
        // NativeAotHost will. CoralHost::Initialize forwards to
        // m_coralHostManager->Initialize(config) internally - the manager is
        // still the owner of CLR bring-up and nothing about that path changed.
        m_managedHost = AZStd::make_unique<CoralHost>(*m_coralHostManager, config);

        const Abi::NativeImports imports = ScriptBindings::MakeNativeImports();
        CoralHostStatus status = m_managedHost->Initialize(imports);
```

and extend the `CoralHostStatus::Success` arm (lines 815-823) so the new interface is registered alongside the existing one:

```cpp
        case CoralHostStatus::Success:
            AZLOG_INFO("O3DESharpSystemComponent: Coral host initialized successfully");

            // Register the interface so other systems can access the host
            CoralHostManagerInterface::Register(m_coralHostManager.get());

            // M3: register the ABI seam alongside it. Both are live on purpose -
            // existing consumers keep using ICoralHostManager unchanged, while
            // anything written against the frozen ABI resolves IManagedHost and
            // works identically under NativeAotHost later.
            ManagedHostInterface::Register(m_managedHost.get());
            AZLOG_INFO(
                "O3DESharpSystemComponent: IManagedHost registered (ABI v%u, hot-reload %s)",
                Abi::HostAbiVersion,
                m_managedHost->SupportsHotReload() ? "supported" : "unsupported");

            // Register internal calls (C++ functions exposed to C#)
            RegisterScriptBindings();
            break;
```

- [ ] **Step 5: Mirror it in shutdown**

In `ShutdownCoralHost` (lines 855-870), unregister and drop the adapter before the manager is shut down:

```cpp
    void O3DESharpSystemComponent::ShutdownCoralHost()
    {
        // M3: tear the seam down FIRST. The adapter holds a reference to the
        // manager, so it must stop being reachable before the manager goes
        // away - and any consumer holding a ManagedExports pointer must lose
        // the interface before the ALC those pointers live in is unloaded.
        if (m_managedHost)
        {
            if (ManagedHostInterface::Get() == m_managedHost.get())
            {
                ManagedHostInterface::Unregister(m_managedHost.get());
            }
            m_managedHost->InvalidateExports();
        }

        if (m_coralHostManager)
        {
            // Unregister the interface first
            if (CoralHostManagerInterface::Get() == m_coralHostManager.get())
            {
                CoralHostManagerInterface::Unregister(m_coralHostManager.get());
            }

            // Shutdown the Coral host
            m_coralHostManager->Shutdown();

            AZLOG_INFO("O3DESharpSystemComponent: Coral host shutdown complete");
        }

        m_managedHost.reset();
    }
```

- [ ] **Step 6: Run test to verify it passes**

Run: `python -m pytest Editor/Tests/test_managed_host_seam.py -q`
Expected: `11 passed`

- [ ] **Step 7: Full M3 regression**

Run: `python -m pytest Editor/Tests -q && dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo`
Expected: all Python tests pass (`slow` deselected by `pytest.ini`), then `Passed!  - Failed:     0, Passed:   100, Skipped:     0, Total:   100`

- [ ] **Step 8: Commit**

```bash
git add Code/Source/Clients/O3DESharpSystemComponent.h Code/Source/Clients/O3DESharpSystemComponent.cpp Editor/Tests/test_managed_host_seam.py
git commit -m "M3: drive editor init through the IManagedHost seam

The system component keeps m_coralHostManager and keeps registering
ICoralHostManager exactly as before; CoralHost is constructed over it and
registered alongside. Existing consumers are untouched, and the seam is live
and exercised rather than declared and dead.

Initialize now goes through CoralHost, so the editor uses the same entry point
NativeAotHost will - the manager is still the owner of CLR bring-up, and the
direct call is removed so it is not initialised twice.

Shutdown tears the seam down first: the adapter holds a reference to the
manager, and any consumer holding a ManagedExports pointer must lose the
interface before the ALC those pointers live in is unloaded.

NOT COMPILE-VERIFIED: no O3DE engine SDK in the authoring environment.
M3 exit criteria met: editor path runs through CoralHost + the ABI, and the
golden contract test passes."
```

---

## Milestone M4 — Desktop NativeAOT shipping build

### Task 13: `NativeAotHost`

**Files:**
- Create: `Code/Source/Scripting/NativeAotHost.h`
- Create: `Code/Source/Scripting/NativeAotHost.cpp`
- Modify: `Code/o3desharp_private_files.cmake`
- Test: `Editor/Tests/test_nativeaot_host.py`

**Interfaces:**
- Consumes: `IManagedHost`, `Abi::NativeImports`, `Abi::ManagedExports`, `Abi::HostAbiVersion`, `CoralHostStatus` (Tasks 2, 11); the exported symbol `O3DESharp_GetManagedExports` (Task 6).
- Produces (consumed by Task 19): `class O3DESharp::NativeAotHost final : public IManagedHost`, constructed with the path to the NativeAOT shared library.

> **The inverted direction.** `CoralHost` *uploads* imports and resolves exports by name through a CLR. `NativeAotHost` loads a plain native library and resolves **one** symbol; that symbol takes both structs. No Coral, no hostfxr, no `nethost` — a NativeAOT image has nothing for them to attach to.

- [ ] **Step 1: Write the failing test**

Create `Editor/Tests/test_nativeaot_host.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Structural guards for the NativeAOT backend.

Not compile-verifiable here (no O3DE engine SDK), so the properties that
distinguish this backend from CoralHost - and that a careless edit would
silently undo - are checked against the source.
"""

import re
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
SCRIPTING = GEM_ROOT / "Code" / "Source" / "Scripting"
HOST_H = SCRIPTING / "NativeAotHost.h"
HOST_CPP = SCRIPTING / "NativeAotHost.cpp"


def _read(path):
    assert path.is_file(), f"{path} is missing."
    return path.read_text(encoding="utf-8")


@pytest.mark.unit
def test_implements_the_full_interface():
    text = _read(HOST_H)
    for method in ("Initialize", "GetExports", "SupportsHotReload", "Shutdown"):
        assert re.search(rf"\b{method}\s*\([^)]*\)\s*(?:const\s*)?override\s*;", text), method


@pytest.mark.unit
def test_does_not_touch_coral_or_hostfxr():
    # A NativeAOT image has no JIT and no hostfxr consumer; there is nothing
    # for nethost -> hostfxr to attach to. Any reference here means the two
    # hosting models got mixed, which is the one thing the design forbids.
    both = _read(HOST_H) + _read(HOST_CPP)
    banned = re.findall(r"\b(Coral::\w+|hostfxr_\w+|get_hostfxr_path|nethost)\b", both)
    assert not banned, (
        f"NativeAotHost must not reference the CoreCLR hosting stack: {sorted(set(banned))}"
    )


@pytest.mark.unit
def test_resolves_exactly_one_exported_symbol():
    body = _read(HOST_CPP)
    assert "O3DESharp_GetManagedExports" in body, (
        "the whole ABI is exchanged through one exported symbol."
    )
    # The five thunks come back inside the struct; resolving them individually
    # would reintroduce name-based lookup for no reason.
    for thunk in ("O3DESharp_CreateInstance", "O3DESharp_InvokeLifecycle",
                  "O3DESharp_DispatchEBusEvent", "O3DESharp_DestroyInstance"):
        assert thunk not in body, (
            f"{thunk} must arrive inside ManagedExports, not be resolved by name."
        )


@pytest.mark.unit
def test_covers_both_desktop_loaders():
    body = _read(HOST_CPP)
    assert "LoadLibrary" in body and "dlopen" in body, (
        "win-x64 and linux-x64 are both in M4 scope."
    )
    assert "GetProcAddress" in body and "dlsym" in body


@pytest.mark.unit
def test_rejects_an_abi_version_mismatch():
    body = _read(HOST_CPP)
    assert re.search(r"exports\.version\s*!=\s*Abi::HostAbiVersion", body), (
        "a shipping image built against a different ABI version must be refused, "
        "not have its pointers reinterpreted."
    )


@pytest.mark.unit
def test_hot_reload_is_hard_false():
    body = _read(HOST_CPP)
    assert re.search(r"bool\s+NativeAotHost::SupportsHotReload\(\)\s*const\s*\{\s*return\s+false\s*;",
                     body, re.MULTILINE | re.DOTALL), (
        "hot-reload is editor-only by design; there is no AssemblyLoadContext in "
        "a NativeAOT image, so this must be an unconditional false rather than a probe."
    )


@pytest.mark.unit
def test_sources_are_in_the_build_file_list():
    files_cmake = (GEM_ROOT / "Code" / "o3desharp_private_files.cmake").read_text(encoding="utf-8")
    for entry in ("Source/Scripting/NativeAotHost.h", "Source/Scripting/NativeAotHost.cpp"):
        assert entry in files_cmake, f"{entry} is missing from o3desharp_private_files.cmake"
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest Editor/Tests/test_nativeaot_host.py -q`
Expected: `7 failed` — `AssertionError: .../Code/Source/Scripting/NativeAotHost.h is missing.`

- [ ] **Step 3: Write the header**

Create `Code/Source/Scripting/NativeAotHost.h`:

```cpp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/Memory/SystemAllocator.h>
#include <AzCore/IO/Path/Path.h>

#include <Scripting/IManagedHost.h>

namespace O3DESharp
{
    //! IManagedHost over a NativeAOT-compiled managed library. Desktop
    //! shipping builds only.
    //!
    //! The direction is INVERTED relative to CoralHost. CoralHost brings up a
    //! CLR, uploads NativeImports through Coral's AddInternalCall, and resolves
    //! managed statics by name. This host does none of that: the managed side
    //! is an ordinary native shared library, so it is loaded with
    //! LoadLibrary/dlopen and exactly ONE exported symbol is resolved -
    //! O3DESharp_GetManagedExports - which takes both ABI structs at once. C++
    //! imports the exports rather than uploading calls.
    //!
    //! There is deliberately no Coral and no hostfxr here. A NativeAOT image
    //! has no JIT and is not a hostfxr consumer; there is nothing for
    //! nethost -> hostfxr to attach to. The two hosting models are mutually
    //! exclusive per build artifact, which is exactly why they are two classes
    //! behind one interface rather than one class with a flag.
    class NativeAotHost final
        : public IManagedHost
    {
    public:
        AZ_RTTI(NativeAotHost, "{5D7F1A44-9B2C-4C0E-8E5A-3B6D2F81C7A4}", IManagedHost);
        AZ_CLASS_ALLOCATOR(NativeAotHost, AZ::SystemAllocator);

        //! libraryPath is the NativeAOT shared library produced by
        //! `dotnet publish -p:PublishAot=true -p:NativeLib=Shared`, deployed
        //! next to the launcher (Bin/Scripts/aot/).
        explicit NativeAotHost(AZ::IO::Path libraryPath);
        ~NativeAotHost() override;

        // IManagedHost
        CoralHostStatus Initialize(const Abi::NativeImports& imports) override;
        const Abi::ManagedExports* GetExports() const override;
        bool SupportsHotReload() const override;
        void Shutdown() override;

    private:
        //! Signature of the single exported entry point. Must match
        //! ManagedExportsThunks.O3DESharp_GetManagedExports exactly.
        using GetManagedExportsFn = int (*)(const Abi::NativeImports*, Abi::ManagedExports*);

        AZ::IO::Path m_libraryPath;

        //! Opaque module handle (HMODULE on Windows, void* from dlopen
        //! elsewhere). void* so the header pulls in no platform headers.
        void* m_module = nullptr;

        Abi::ManagedExports m_exports{};
        bool m_exportsValid = false;
    };
} // namespace O3DESharp
```

- [ ] **Step 4: Write the implementation**

Create `Code/Source/Scripting/NativeAotHost.cpp`:

```cpp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "NativeAotHost.h"

#include <AzCore/Console/ILogger.h>
#include <AzCore/PlatformIncl.h>

#if !AZ_TRAIT_OS_PLATFORM_APPLE && !defined(AZ_PLATFORM_WINDOWS)
#include <dlfcn.h>
#elif AZ_TRAIT_OS_PLATFORM_APPLE
#include <dlfcn.h>
#endif

namespace O3DESharp
{
    namespace
    {
        //! The one symbol the whole ABI travels through.
        constexpr const char* GetManagedExportsSymbol = "O3DESharp_GetManagedExports";

        void* LoadModule(const char* path)
        {
#if defined(AZ_PLATFORM_WINDOWS)
            return reinterpret_cast<void*>(::LoadLibraryA(path));
#else
            // RTLD_LOCAL so the scripting image's symbols do not leak into the
            // global namespace and collide with the launcher's.
            return ::dlopen(path, RTLD_NOW | RTLD_LOCAL);
#endif
        }

        void* FindSymbol(void* module, const char* name)
        {
            if (module == nullptr)
            {
                return nullptr;
            }
#if defined(AZ_PLATFORM_WINDOWS)
            return reinterpret_cast<void*>(::GetProcAddress(reinterpret_cast<HMODULE>(module), name));
#else
            return ::dlsym(module, name);
#endif
        }

        void UnloadModule(void* module)
        {
            if (module == nullptr)
            {
                return;
            }
#if defined(AZ_PLATFORM_WINDOWS)
            ::FreeLibrary(reinterpret_cast<HMODULE>(module));
#else
            ::dlclose(module);
#endif
        }
    } // namespace

    NativeAotHost::NativeAotHost(AZ::IO::Path libraryPath)
        : m_libraryPath(AZStd::move(libraryPath))
    {
    }

    NativeAotHost::~NativeAotHost()
    {
        Shutdown();
    }

    CoralHostStatus NativeAotHost::Initialize(const Abi::NativeImports& imports)
    {
        if (m_module != nullptr)
        {
            return CoralHostStatus::AlreadyInitialized;
        }

        if (imports.version != Abi::HostAbiVersion)
        {
            AZLOG_ERROR(
                "NativeAotHost: refusing to initialize - NativeImports version %u, host ABI version %u",
                imports.version,
                Abi::HostAbiVersion);
            return CoralHostStatus::CoralInitError;
        }

        AZLOG_INFO("NativeAotHost: loading %s", m_libraryPath.c_str());
        m_module = LoadModule(m_libraryPath.c_str());
        if (m_module == nullptr)
        {
            AZLOG_ERROR(
                "NativeAotHost: could not load the NativeAOT scripting library at %s. "
                "It is produced by the O3DESharp.PublishNativeAot build target and deployed "
                "to Bin/Scripts/aot/.",
                m_libraryPath.c_str());
            return CoralHostStatus::CoralManagedNotFound;
        }

        auto getExports = reinterpret_cast<GetManagedExportsFn>(
            FindSymbol(m_module, GetManagedExportsSymbol));
        if (getExports == nullptr)
        {
            AZLOG_ERROR(
                "NativeAotHost: %s does not export %s. The library was almost certainly published "
                "without -p:O3DESharpHostMode=NativeAot, so HostExportsGenerator emitted no entry point.",
                m_libraryPath.c_str(),
                GetManagedExportsSymbol);
            UnloadModule(m_module);
            m_module = nullptr;
            return CoralHostStatus::AssemblyLoadFailed;
        }

        // The entire ABI exchange - one call, both structs. This is the
        // inverted direction: nothing is uploaded, the exports are imported.
        Abi::ManagedExports exports{};
        if (getExports(&imports, &exports) != 1)
        {
            AZLOG_ERROR(
                "NativeAotHost: %s rejected the import table. The shipping image was built "
                "against a different ABI version than this host.",
                GetManagedExportsSymbol);
            UnloadModule(m_module);
            m_module = nullptr;
            return CoralHostStatus::CoralInitError;
        }

        if (exports.version != Abi::HostAbiVersion)
        {
            AZLOG_ERROR(
                "NativeAotHost: ManagedExports version %u, host ABI version %u - refusing rather "
                "than reinterpreting the table",
                exports.version,
                Abi::HostAbiVersion);
            UnloadModule(m_module);
            m_module = nullptr;
            return CoralHostStatus::CoralInitError;
        }

        // Unlike the Coral path there is NO fallback here: without a CLR there
        // is no ManagedObject::InvokeMethod to degrade to. A null pointer in a
        // version-matched table means the image is malformed, so fail loudly at
        // startup rather than at the first dispatch.
        if (exports.CreateInstance == nullptr || exports.InvokeLifecycle == nullptr ||
            exports.DispatchEBusEvent == nullptr || exports.DestroyInstance == nullptr ||
            exports.HotReloadSwap == nullptr)
        {
            AZLOG_ERROR("NativeAotHost: ManagedExports contains a null entry - the image is malformed");
            UnloadModule(m_module);
            m_module = nullptr;
            return CoralHostStatus::AssemblyLoadFailed;
        }

        m_exports = exports;
        m_exportsValid = true;
        AZLOG_INFO("NativeAotHost: initialized (ABI v%u, no CoreCLR, no hostfxr)", exports.version);
        return CoralHostStatus::Success;
    }

    const Abi::ManagedExports* NativeAotHost::GetExports() const
    {
        return m_exportsValid ? &m_exports : nullptr;
    }

    bool NativeAotHost::SupportsHotReload() const
    {
        // Unconditional, not a probe: hot-reload is editor-only by design and
        // there is no AssemblyLoadContext in a NativeAOT image to swap.
        return false;
    }

    void NativeAotHost::Shutdown()
    {
        m_exportsValid = false;
        m_exports = {};

        if (m_module != nullptr)
        {
            UnloadModule(m_module);
            m_module = nullptr;
            AZLOG_INFO("NativeAotHost: shutdown complete");
        }
    }
} // namespace O3DESharp
```

- [ ] **Step 5: Add to the build file list**

In `Code/o3desharp_private_files.cmake`, alongside the other scripting sources:

```cmake
    Source/Scripting/NativeAotHost.h
    Source/Scripting/NativeAotHost.cpp
```

- [ ] **Step 6: Run test to verify it passes**

Run: `python -m pytest Editor/Tests/test_nativeaot_host.py -q`
Expected: `7 passed`

- [ ] **Step 7: Commit**

```bash
git add Code/Source/Scripting/NativeAotHost.h Code/Source/Scripting/NativeAotHost.cpp Code/o3desharp_private_files.cmake Editor/Tests/test_nativeaot_host.py
git commit -m "M4: add the NativeAOT backend behind IManagedHost

The direction is inverted relative to CoralHost: no CLR is brought up and
nothing is uploaded. The managed side is an ordinary native shared library, so
it is loaded with LoadLibrary/dlopen and exactly one exported symbol is
resolved - O3DESharp_GetManagedExports - which takes both ABI structs at once.

No Coral and no hostfxr, deliberately: a NativeAOT image has no JIT and is not
a hostfxr consumer, so there is nothing for nethost to attach to. A test
asserts neither ever creeps back in.

There is also no fallback path here. Without a CLR there is no
ManagedObject::InvokeMethod to degrade to, so a null entry in a version-matched
table means a malformed image and fails at startup rather than at the first
dispatch. SupportsHotReload is an unconditional false - editor-only by design.

NOT COMPILE-VERIFIED: no O3DE engine SDK in the authoring environment.
Linux paths are authored but NOT verified - no Linux toolchain here."
```

---

### Task 14: The closed-world diagnostic analyzer

**Files:**
- Create: `Code/Tools/SourceGenerators/DynamicDispatchAnalyzer.cs`
- Create: `Code/Tools/SourceGenerators.Tests/DynamicDispatchSamples.cs`
- Test: `Editor/Tests/test_closed_world_diagnostic.py`

**Interfaces:**
- Produces (consumed by Tasks 15, 16): diagnostic id `O3DESHARP1001` — "EBus dispatch target is not a compile-time constant", severity `Warning`, reported at the exact call site.

> This is the build-time half of the locked closed-world decision. Runtime-dynamic BehaviorContext dispatch is **out of scope on NativeAOT desktop** and must be *loudly diagnosed*, never silently degraded. The analyzer is separate from the generator on purpose: it has to run on the **game's** assembly (where the call sites are), whereas the dispatch table is generated into `O3DE.Core` (where `ReflectionInternalCalls` is `internal`).

- [ ] **Step 1: Write the failing test**

Create `Editor/Tests/test_closed_world_diagnostic.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""The closed-world diagnostic must fire at build time, on the exact call site.

Runtime-dynamic BehaviorContext dispatch is out of scope on NativeAOT desktop.
That is a designed restriction, and the whole point of designing it rather than
discovering it is that it is diagnosable: a bus or event name the generator
cannot see at compile time gets a build warning naming the site, and a runtime
hard error if it is reached anyway. Never a silent degrade.

Marked `slow` because it shells out to `dotnet build`.
"""

import re
import shutil
import subprocess
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
SMOKE = GEM_ROOT / "Code" / "Tools" / "SourceGenerators.Tests" / "SourceGenerators.Smoke.csproj"
SAMPLES = GEM_ROOT / "Code" / "Tools" / "SourceGenerators.Tests" / "DynamicDispatchSamples.cs"


def _build():
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    result = subprocess.run(
        ["dotnet", "build", str(SMOKE), "-c", "Release", "--nologo", "-t:Rebuild"],
        capture_output=True, text=True, timeout=900,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    return result.stdout


@pytest.mark.slow
def test_non_constant_bus_or_event_name_warns():
    output = _build()
    warnings = re.findall(r"DynamicDispatchSamples\.cs\((\d+),\d+\): warning O3DESHARP1001", output)
    assert warnings, (
        "O3DESHARP1001 did not fire on the deliberately-dynamic call sites in "
        "DynamicDispatchSamples.cs."
    )


@pytest.mark.slow
def test_constant_call_sites_are_silent():
    output = _build()
    # The sample file marks its constant-name calls with // CLOSED-WORLD.
    text = SAMPLES.read_text(encoding="utf-8").splitlines()
    closed = {i + 1 for i, line in enumerate(text) if "// CLOSED-WORLD" in line}
    warned = {int(n) for n in re.findall(r"DynamicDispatchSamples\.cs\((\d+),\d+\): warning O3DESHARP1001", output)}
    assert not (closed & warned), (
        f"constant-name call sites must NOT warn; these did: {sorted(closed & warned)}. "
        "A false positive here trains people to ignore the diagnostic."
    )


@pytest.mark.slow
def test_every_dynamic_site_is_flagged():
    output = _build()
    text = SAMPLES.read_text(encoding="utf-8").splitlines()
    open_world = {i + 1 for i, line in enumerate(text) if "// OPEN-WORLD" in line}
    warned = {int(n) for n in re.findall(r"DynamicDispatchSamples\.cs\((\d+),\d+\): warning O3DESHARP1001", output)}
    missed = open_world - warned
    assert not missed, (
        f"these dynamic call sites were not flagged: {sorted(missed)}. A missed site "
        "becomes a runtime hard error in a shipped game instead of a build warning."
    )


@pytest.mark.slow
def test_the_message_names_the_api_and_the_reason():
    output = _build()
    line = next((l for l in output.splitlines() if "O3DESHARP1001" in l), None)
    assert line, "expected at least one O3DESHARP1001 line"
    assert "NativeAOT" in line, "the message must say WHICH build config this breaks"
    for token in ("BroadcastEBusEvent", "SendEBusEvent"):
        if token in line:
            break
    else:
        pytest.fail("the message must name the API being called")
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest Editor/Tests/test_closed_world_diagnostic.py -q -m slow`
Expected: `4 failed` — the build succeeds but emits no `O3DESHARP1001` at all, so `test_non_constant_bus_or_event_name_warns` fails with `AssertionError: O3DESHARP1001 did not fire ...` (and `DynamicDispatchSamples.cs` does not exist yet, failing the file reads).

- [ ] **Step 3: Add the sample call sites**

Create `Code/Tools/SourceGenerators.Tests/DynamicDispatchSamples.cs`:

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using O3DE;
using O3DE.Reflection;

namespace O3DESharp.SourceGenerators.Smoke
{
    /// <summary>
    /// Fixture for the closed-world diagnostic. Every call below is tagged so
    /// Editor/Tests/test_closed_world_diagnostic.py can assert BOTH directions:
    /// that open-world sites warn, and - just as importantly - that closed-world
    /// sites do not. A false positive trains people to ignore the diagnostic,
    /// which is worse than not having it.
    /// </summary>
    public static class DynamicDispatchSamples
    {
        private const string ConstBus = "TickBus";

        public static void ConstantNames(float dt, ulong entityId)
        {
            NativeReflection.BroadcastEBusEvent("TickBus", "OnTick", dt); // CLOSED-WORLD
            NativeReflection.SendEBusEvent("TransformBus", "GetWorldTranslation", entityId); // CLOSED-WORLD
            NativeReflection.BroadcastEBusEvent(ConstBus, "OnTick", dt); // CLOSED-WORLD
            NativeReflection.BroadcastEBusEvent("Tick" + "Bus", "OnTick", dt); // CLOSED-WORLD
        }

        public static void RuntimeComputedNames(string bus, string evt, float dt, ulong entityId)
        {
            NativeReflection.BroadcastEBusEvent(bus, "OnTick", dt); // OPEN-WORLD
            NativeReflection.BroadcastEBusEvent("TickBus", evt, dt); // OPEN-WORLD
            NativeReflection.SendEBusEvent(bus, evt, entityId); // OPEN-WORLD
            NativeReflection.BroadcastEBusEvent($"{bus}Notifications", "OnTick", dt); // OPEN-WORLD
        }
    }
}
```

- [ ] **Step 4: Write the analyzer**

Create `Code/Tools/SourceGenerators/DynamicDispatchAnalyzer.cs`:

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace O3DESharp.SourceGenerators
{
    /// <summary>
    /// Flags EBus dispatch whose bus or event name is not a compile-time
    /// constant.
    ///
    /// Static dispatch on the desktop NativeAOT build can only cover the
    /// closed world: names the generator can read out of the source. A
    /// runtime-computed name has no generated path, and NativeAOT desktop
    /// deliberately does NOT provide an interpreter tail for it (that is the
    /// console/mobile Mono backend's job, M5).
    ///
    /// The design decision is that this restriction is LOUD: a build warning
    /// here, and a runtime hard error naming the site if the call is reached in
    /// a shipping image. Never a silent degrade to something slower or subtly
    /// different.
    ///
    /// Warning rather than error on purpose - the editor build handles these
    /// call sites fine, so a game that never ships an AOT desktop artifact is
    /// not blocked by them.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class DynamicDispatchAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "O3DESHARP1001";

        private static readonly DiagnosticDescriptor s_rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "EBus dispatch target is not a compile-time constant",
            messageFormat:
                "'{0}' is called with a non-constant {1} name, so it cannot be statically " +
                "dispatched. This call works in the editor but is a hard runtime error in a " +
                "NativeAOT desktop build - constant-fold the name, or ship the Mono backend.",
            category: "O3DESharp.AOT",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description:
                "Desktop NativeAOT supports only closed-world BehaviorContext dispatch: bus and " +
                "event names the generator can resolve at compile time. Runtime-computed names " +
                "are out of scope by design and are diagnosed here rather than degrading " +
                "silently at runtime.");

        // Method name -> (bus arg index, event arg index). Both must be
        // constant for the call to be statically dispatchable.
        private const string BroadcastName = "BroadcastEBusEvent";
        private const string SendName = "SendEBusEvent";
        private const string BroadcastResultName = "BroadcastResultEBusEvent";
        private const string SendResultName = "SendResultEBusEvent";
        private const string NativeReflectionFullName = "O3DE.Reflection.NativeReflection";

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(s_rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
        }

        private static void Analyze(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;

            // Cheap syntactic reject first: only four method names matter.
            if (invocation.Expression is not MemberAccessExpressionSyntax member)
            {
                return;
            }
            string methodName = member.Name.Identifier.Text;
            if (methodName != BroadcastName && methodName != SendName &&
                methodName != BroadcastResultName && methodName != SendResultName)
            {
                return;
            }

            // Then confirm it really is NativeReflection and not a same-named
            // method on some unrelated type.
            if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol symbol)
            {
                return;
            }
            if (symbol.ContainingType?.ToDisplayString() != NativeReflectionFullName)
            {
                return;
            }

            var args = invocation.ArgumentList.Arguments;
            if (args.Count < 2)
            {
                return;
            }

            // Argument 0 is the bus name, argument 1 the event name, on all
            // four overloads (the addressed variants take the bus id third).
            if (!IsConstant(context, args[0].Expression))
            {
                Report(context, args[0].Expression, methodName, "bus");
            }
            if (!IsConstant(context, args[1].Expression))
            {
                Report(context, args[1].Expression, methodName, "event");
            }
        }

        private static bool IsConstant(SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
        {
            // GetConstantValue folds literals, const fields and constant
            // concatenation ("Tick" + "Bus"), which is exactly the set the
            // generator can also resolve. Interpolated strings are not folded,
            // and correctly so - their value is a runtime decision.
            return context.SemanticModel.GetConstantValue(expression).HasValue;
        }

        private static void Report(
            SyntaxNodeAnalysisContext context,
            ExpressionSyntax expression,
            string methodName,
            string argumentKind)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(s_rule, expression.GetLocation(), methodName, argumentKind));
        }
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `python -m pytest Editor/Tests/test_closed_world_diagnostic.py -q -m slow`
Expected: `4 passed`

- [ ] **Step 6: Confirm the existing examples are clean**

The repo's own sample scripts use constant names (`Assets/Scripts/Examples/ReflectionExample.cs:267,270`), so the analyzer must be silent on them.
Run: `dotnet build Assets/Scripts/O3DE.Core/O3DE.Core.csproj -c Release --nologo -t:Rebuild 2>&1 | grep -c O3DESHARP1001`
Expected: `0`

- [ ] **Step 7: Commit**

```bash
git add Code/Tools/SourceGenerators/DynamicDispatchAnalyzer.cs Code/Tools/SourceGenerators.Tests/DynamicDispatchSamples.cs Editor/Tests/test_closed_world_diagnostic.py
git commit -m "M4: warn at build time on EBus dispatch the generator cannot see

Desktop NativeAOT supports only closed-world dispatch, and the point of
designing that restriction rather than discovering it is that it is
diagnosable. O3DESHARP1001 fires on the exact call site when a bus or event
name is not a compile-time constant.

Warning, not error: these call sites work in the editor, so a game that never
ships an AOT desktop artifact should not be blocked. The runtime hard error is
the other half and lands with the dispatch routing.

The fixture tags both directions and the tests assert both, because a false
positive on a constant name trains people to ignore the diagnostic - which is
worse than not having one.

A separate analyzer from the generator on purpose: call sites live in the
game's assembly, the dispatch table is generated into O3DE.Core where
ReflectionInternalCalls is internal."
```

---

### Task 15: Static dispatch table from `reflection_data.json`

**Files:**
- Create: `Code/Tools/SourceGenerators/StaticDispatchGenerator.cs`
- Modify: `Assets/Scripts/O3DE.Core/O3DE.Core.csproj` (add the `AdditionalFiles` item)
- Test: `Editor/Tests/test_static_dispatch_emit.py`

**Interfaces:**
- Consumes: `reflection_data.json` as an `AdditionalFile` (schema: `Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Configuration/ReflectionDataSchema.cs:98-159` — `ebuses[].name`, `ebuses[].events[].name`, `.is_broadcast`, `.parameters[].marshal_type`); `build_property.O3DESharpEmitHostExports` (Task 3).
- Produces (consumed by Task 16):
  - `internal static class O3DE.Reflection.StaticEBusDispatch`
  - `static bool StaticEBusDispatch.TryGetShape(string busName, string eventName, out int arity, out bool isBroadcast)`
  - `static int StaticEBusDispatch.EntryCount`

> **This is the existing `reflection_data.json`, not SP-1b's `native_bindings.json`.** The two tracks are orthogonal: SP-1b recovers native C++ symbols for trampolines; this reads the reflected *script* surface the dispatcher already exposes. The table's job is to let a shipping build answer "is this (bus, event) a thing, and what shape are its arguments?" without any managed-side reflection.

- [ ] **Step 1: Write the failing test**

Create `Editor/Tests/test_static_dispatch_emit.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""StaticDispatchGenerator turns reflection_data.json into a compile-time table.

The shipping build has to answer 'is this (bus, event) real, and what shape are
its arguments?' with no managed-side reflection. The table is that answer. It
consumes the EXISTING reflection_data.json - the dump the reflection binding
backend already produces - not SP-1b's separate native_bindings.json, which is
an orthogonal track.

Marked `slow` because it shells out to `dotnet build`.
"""

import json
import re
import shutil
import subprocess
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
CORE_CSPROJ = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "O3DE.Core.csproj"
GENERATED = (
    GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "obj" / "Release" / "net9.0"
    / "generated" / "O3DESharp.SourceGenerators"
    / "O3DESharp.SourceGenerators.StaticDispatchGenerator"
)

FIXTURE = {
    "classes": [],
    "global_methods": [],
    "global_properties": [],
    "ebuses": [
        {
            "name": "TickBus",
            "address_type": {"marshal_type": "Void"},
            "events": [
                {
                    "name": "OnTick",
                    "bus_name": "TickBus",
                    "is_broadcast": True,
                    "return_type": {"marshal_type": "Void"},
                    "parameters": [
                        {"name": "deltaTime", "marshal_type": "Float"},
                        {"name": "timePoint", "marshal_type": "Double"},
                    ],
                }
            ],
        },
        {
            "name": "TransformBus",
            "address_type": {"marshal_type": "EntityId"},
            "events": [
                {
                    "name": "GetWorldTranslation",
                    "bus_name": "TransformBus",
                    "is_broadcast": False,
                    "return_type": {"marshal_type": "Vector3"},
                    "parameters": [],
                }
            ],
        },
    ],
}


def _build(tmp_path):
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    data = tmp_path / "reflection_data.json"
    data.write_text(json.dumps(FIXTURE), encoding="utf-8")
    result = subprocess.run(
        [
            "dotnet", "build", str(CORE_CSPROJ), "-c", "Release", "--nologo", "-t:Rebuild",
            "-p:EmitCompilerGeneratedFiles=true",
            f"-p:O3DESharpReflectionData={data}",
        ],
        capture_output=True, text=True, timeout=900,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    files = list(GENERATED.glob("*.g.cs"))
    assert files, f"StaticDispatchGenerator emitted nothing into {GENERATED}"
    return "\n".join(f.read_text(encoding="utf-8") for f in files)


@pytest.mark.slow
def test_emits_one_entry_per_reflected_event(tmp_path):
    emitted = _build(tmp_path)
    assert '"TickBus\\u0000OnTick"' in emitted or '"TickBus\\0OnTick"' in emitted or \
           ('case "TickBus"' in emitted and '"OnTick"' in emitted)
    assert "TransformBus" in emitted and "GetWorldTranslation" in emitted
    assert "EntryCount => 2" in emitted


@pytest.mark.slow
def test_records_arity_and_broadcast_flag(tmp_path):
    emitted = _build(tmp_path)
    # OnTick takes two parameters and is a broadcast; GetWorldTranslation takes
    # none and is addressed. Both facts are what the runtime routing needs in
    # order to reject a malformed call without reflecting.
    assert re.search(r"OnTick.*?arity\s*=\s*2", emitted, re.DOTALL)
    assert re.search(r"OnTick.*?isBroadcast\s*=\s*true", emitted, re.DOTALL)
    assert re.search(r"GetWorldTranslation.*?arity\s*=\s*0", emitted, re.DOTALL)
    assert re.search(r"GetWorldTranslation.*?isBroadcast\s*=\s*false", emitted, re.DOTALL)


@pytest.mark.slow
def test_lookup_is_a_switch_not_a_dictionary(tmp_path):
    emitted = _build(tmp_path)
    assert "switch (busName)" in emitted, (
        "a generated switch is resolved at compile time; a Dictionary would be "
        "runtime state that has to be built during startup."
    )


@pytest.mark.slow
def test_missing_reflection_data_emits_an_empty_but_valid_table(tmp_path):
    # A fresh clone has no reflection_data.json. The build must still succeed -
    # the table is simply empty and every dispatch falls to the diagnostic.
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    result = subprocess.run(
        [
            "dotnet", "build", str(CORE_CSPROJ), "-c", "Release", "--nologo", "-t:Rebuild",
            "-p:EmitCompilerGeneratedFiles=true",
            f"-p:O3DESharpReflectionData={tmp_path / 'does-not-exist.json'}",
        ],
        capture_output=True, text=True, timeout=900,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    emitted = "\n".join(f.read_text(encoding="utf-8") for f in GENERATED.glob("*.g.cs"))
    assert "EntryCount => 0" in emitted


@pytest.mark.slow
def test_malformed_reflection_data_does_not_break_the_build(tmp_path):
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    bad = tmp_path / "reflection_data.json"
    bad.write_text("{ not json", encoding="utf-8")
    result = subprocess.run(
        [
            "dotnet", "build", str(CORE_CSPROJ), "-c", "Release", "--nologo", "-t:Rebuild",
            f"-p:O3DESharpReflectionData={bad}",
        ],
        capture_output=True, text=True, timeout=900,
    )
    assert result.returncode == 0, (
        "a corrupt reflection dump must degrade to an empty table, not take the "
        "build down: " + result.stdout + result.stderr
    )
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest Editor/Tests/test_static_dispatch_emit.py -q -m slow`
Expected: `5 failed` — `AssertionError: StaticDispatchGenerator emitted nothing into .../O3DESharp.SourceGenerators.StaticDispatchGenerator`.

- [ ] **Step 3: Feed `reflection_data.json` to the generator**

In `Assets/Scripts/O3DE.Core/O3DE.Core.csproj`, add after the `CompilerVisibleProperty` `ItemGroup` from Task 3:

```xml
  <!--
    The reflected script surface, as dumped by the C++ ReflectionDataExporter.
    StaticDispatchGenerator reads it to emit a compile-time (bus, event) table
    so the shipping build can validate a dispatch without managed reflection.

    Overridable so tests can point at a fixture. Absent is a normal state on a
    fresh clone - the generator emits an empty table and the build succeeds.
  -->
  <PropertyGroup>
    <O3DESharpReflectionData Condition="'$(O3DESharpReflectionData)' == ''">$(MSBuildThisFileDirectory)..\..\..\reflection_data.json</O3DESharpReflectionData>
  </PropertyGroup>

  <ItemGroup Condition="Exists('$(O3DESharpReflectionData)')">
    <AdditionalFiles Include="$(O3DESharpReflectionData)" O3DESharpKind="ReflectionData" />
  </ItemGroup>

  <ItemGroup>
    <CompilerVisibleItemMetadata Include="AdditionalFiles" MetadataName="O3DESharpKind" />
  </ItemGroup>
```

- [ ] **Step 4: Write the generator**

Create `Code/Tools/SourceGenerators/StaticDispatchGenerator.cs`:

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace O3DESharp.SourceGenerators
{
    /// <summary>
    /// Turns the reflected EBus surface into a compile-time lookup table.
    ///
    /// The shipping NativeAOT build has to answer "is this (bus, event) real,
    /// and what shape are its arguments?" without any managed-side reflection.
    /// This emits that answer as a generated switch - resolved when the
    /// compiler runs, not built into a Dictionary during startup.
    ///
    /// Input is the EXISTING reflection_data.json, the dump the reflection
    /// binding backend already produces and ReflectionBindingGenerator already
    /// consumes. It is deliberately NOT SP-1b's native_bindings.json: that
    /// track recovers native C++ symbols for trampolines and is orthogonal to
    /// this one.
    ///
    /// A missing or corrupt dump yields an EMPTY table rather than a build
    /// failure. A fresh clone has no dump, and taking the build down over an
    /// optional optimisation input would be a worse failure than the empty
    /// table's - every dispatch then simply falls to the closed-world
    /// diagnostic, which is the honest outcome.
    /// </summary>
    [Generator]
    public sealed class StaticDispatchGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var emitEnabled = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) =>
            {
                provider.GlobalOptions.TryGetValue("build_property.O3DESharpEmitHostExports", out var emit);
                return string.Equals(emit, "true", StringComparison.OrdinalIgnoreCase);
            });

            var reflectionData = context.AdditionalTextsProvider
                .Combine(context.AnalyzerConfigOptionsProvider)
                .Where(static pair =>
                {
                    var options = pair.Right.GetOptions(pair.Left);
                    options.TryGetValue("build_metadata.AdditionalFiles.O3DESharpKind", out var kind);
                    return string.Equals(kind, "ReflectionData", StringComparison.OrdinalIgnoreCase);
                })
                .Select(static (pair, token) => pair.Left.GetText(token)?.ToString() ?? string.Empty)
                .Collect();

            context.RegisterSourceOutput(
                emitEnabled.Combine(reflectionData),
                static (sourceContext, pair) =>
                {
                    var (enabled, texts) = pair;
                    if (!enabled)
                    {
                        return;
                    }

                    var events = texts.SelectMany(ParseEvents).ToImmutableArray();
                    sourceContext.AddSource(
                        "O3DE.Reflection.StaticEBusDispatch.g.cs",
                        SourceText.From(Emit(events), Encoding.UTF8));
                });
        }

        // -----------------------------------------------------------
        // Parse
        // -----------------------------------------------------------

        private static IEnumerable<EventShape> ParseEvents(string json)
        {
            // Hand-rolled over System.Text.Json's reader rather than
            // deserializing the full ReflectionDocument: the generator targets
            // netstandard2.0 and must not take a dependency on the
            // BindingGenerator assembly. Only four fields are needed.
            var results = new List<EventShape>();
            if (string.IsNullOrWhiteSpace(json))
            {
                return results;
            }

            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("ebuses", out var buses) ||
                    buses.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    return results;
                }

                foreach (var bus in buses.EnumerateArray())
                {
                    if (!bus.TryGetProperty("name", out var busName) ||
                        busName.ValueKind != System.Text.Json.JsonValueKind.String)
                    {
                        continue;
                    }
                    if (!bus.TryGetProperty("events", out var events) ||
                        events.ValueKind != System.Text.Json.JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var evt in events.EnumerateArray())
                    {
                        if (!evt.TryGetProperty("name", out var eventName) ||
                            eventName.ValueKind != System.Text.Json.JsonValueKind.String)
                        {
                            continue;
                        }

                        int arity = evt.TryGetProperty("parameters", out var parameters) &&
                                    parameters.ValueKind == System.Text.Json.JsonValueKind.Array
                            ? parameters.GetArrayLength()
                            : 0;

                        bool isBroadcast = evt.TryGetProperty("is_broadcast", out var broadcast) &&
                                           broadcast.ValueKind == System.Text.Json.JsonValueKind.True;

                        results.Add(new EventShape(
                            busName.GetString()!, eventName.GetString()!, arity, isBroadcast));
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // A corrupt dump degrades to an empty table. Failing the build
                // over an optional optimisation input would be the worse
                // outcome; an empty table simply routes every dispatch to the
                // closed-world diagnostic instead.
                return new List<EventShape>();
            }

            return results;
        }

        // -----------------------------------------------------------
        // Emit
        // -----------------------------------------------------------

        private static string Emit(ImmutableArray<EventShape> events)
        {
            var distinct = events
                .GroupBy(e => (e.BusName, e.EventName))
                .Select(g => g.First())
                .OrderBy(e => e.BusName, StringComparer.Ordinal)
                .ThenBy(e => e.EventName, StringComparer.Ordinal)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//   This file was generated by O3DESharp.SourceGenerators (StaticDispatchGenerator).");
            sb.AppendLine("//   Source: reflection_data.json. DO NOT EDIT.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine();
            sb.AppendLine("namespace O3DE.Reflection");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Compile-time (bus, event) table built from the reflected EBus");
            sb.AppendLine("    /// surface. A generated switch rather than a Dictionary: the switch is");
            sb.AppendLine("    /// resolved when the compiler runs, whereas a Dictionary would be");
            sb.AppendLine("    /// runtime state built during startup, which is the managed-side work");
            sb.AppendLine("    /// the shipping build exists to avoid.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    internal static class StaticEBusDispatch");
            sb.AppendLine("    {");
            sb.AppendLine($"        /// <summary>Number of statically known events.</summary>");
            sb.AppendLine($"        internal static int EntryCount => {distinct.Count};");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Look up an event's shape. False means the pair is not in the");
            sb.AppendLine("        /// reflected surface this image was built against - which on a");
            sb.AppendLine("        /// shipping NativeAOT build is a hard error, not a fallback.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        internal static bool TryGetShape(string busName, string eventName, out int arity, out bool isBroadcast)");
            sb.AppendLine("        {");
            sb.AppendLine("            arity = 0;");
            sb.AppendLine("            isBroadcast = false;");
            sb.AppendLine("            switch (busName)");
            sb.AppendLine("            {");

            foreach (var busGroup in distinct.GroupBy(e => e.BusName, StringComparer.Ordinal))
            {
                sb.AppendLine($"                case {Literal(busGroup.Key)}:");
                sb.AppendLine("                    switch (eventName)");
                sb.AppendLine("                    {");
                foreach (var evt in busGroup)
                {
                    sb.AppendLine($"                        case {Literal(evt.EventName)}:");
                    sb.AppendLine($"                            arity = {evt.Arity};");
                    sb.AppendLine($"                            isBroadcast = {(evt.IsBroadcast ? "true" : "false")};");
                    sb.AppendLine("                            return true;");
                }
                sb.AppendLine("                        default: return false;");
                sb.AppendLine("                    }");
            }

            sb.AppendLine("                default: return false;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string Literal(string value) =>
            "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>One reflected EBus event, reduced to what static dispatch needs.</summary>
    internal sealed record EventShape(string BusName, string EventName, int Arity, bool IsBroadcast);
}
```

- [ ] **Step 5: Add the JSON dependency to the generator project**

`System.Text.Json` is not in the netstandard2.0 box. Add to the `ItemGroup` at `Code/Tools/SourceGenerators/O3DESharp.SourceGenerators.csproj:51-60`:

```xml
    <!--
      StaticDispatchGenerator reads reflection_data.json. GeneratePathProperty +
      the analyzer packaging below is the standard way to make a dependency
      available INSIDE the compiler process - a plain PackageReference would be
      resolved for the consumer, not for the analyzer host.
    -->
    <PackageReference Include="System.Text.Json" Version="8.0.5" GeneratePathProperty="true" PrivateAssets="all" />
  </ItemGroup>

  <ItemGroup>
    <None Include="$(PkgSystem_Text_Json)\lib\netstandard2.0\System.Text.Json.dll"
          Pack="true" PackagePath="analyzers/dotnet/cs" Visible="false" />
    <None Include="$(PkgSystem_Text_Json)\lib\netstandard2.0\System.Text.Json.dll"
          CopyToOutputDirectory="PreserveNewest" Visible="false" />
```

- [ ] **Step 6: Run test to verify it passes**

Run: `python -m pytest Editor/Tests/test_static_dispatch_emit.py -q -m slow`
Expected: `5 passed`

- [ ] **Step 7: Commit**

```bash
git add Code/Tools/SourceGenerators/StaticDispatchGenerator.cs Code/Tools/SourceGenerators/O3DESharp.SourceGenerators.csproj Assets/Scripts/O3DE.Core/O3DE.Core.csproj Editor/Tests/test_static_dispatch_emit.py
git commit -m "M4: generate a compile-time EBus dispatch table from reflection_data.json

The shipping build has to answer 'is this (bus, event) real, and what shape are
its arguments?' with no managed-side reflection. A generated switch is that
answer, resolved when the compiler runs - a Dictionary would be runtime state
built during startup, which is the work this exists to avoid.

Consumes the EXISTING reflection_data.json that ReflectionBindingGenerator
already reads, not SP-1b's native_bindings.json; that track recovers native C++
symbols for trampolines and is orthogonal.

A missing or corrupt dump yields an empty table rather than a build failure. A
fresh clone has no dump, and taking the build down over an optional
optimisation input is a worse failure than an empty table - which simply routes
every dispatch to the closed-world diagnostic, the honest outcome."
```

---

### Task 16: Route dispatch through the table and hard-error on a miss

**Files:**
- Modify: `Assets/Scripts/O3DE.Core/Reflection/NativeReflection.cs:336-342` and `:423-429`
- Test: `Assets/Scripts/O3DE.Core.Tests/StaticDispatchRoutingTests.cs`

**Interfaces:**
- Consumes: `StaticEBusDispatch.TryGetShape` (Task 15).
- Produces: under `O3DE_HOST_NATIVEAOT`, `BroadcastEBusEvent` / `SendEBusEvent` throw `NotSupportedException` naming bus, event and the reason when the pair is not in the table or the argument count disagrees.

> The runtime half of the locked decision. Task 14 warns at build time; this makes the failure unmissable if a warned-past call is actually reached in a shipping image. **Editor behaviour is unchanged** — the whole change is inside `#if O3DE_HOST_NATIVEAOT`.

- [ ] **Step 1: Write the failing test**

Create `Assets/Scripts/O3DE.Core.Tests/StaticDispatchRoutingTests.cs`:

```csharp
//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using O3DE.Reflection;

namespace O3DE.Core.Tests;

/// <summary>
/// The runtime half of the closed-world decision. O3DESHARP1001 warns at build
/// time; this is what happens if a warned-past call is actually reached in a
/// shipping image.
///
/// The requirement is that it is UNMISSABLE and names the exact site. A dispatch
/// that quietly returned null would be indistinguishable from an event with no
/// handlers - the silent degrade the design explicitly rules out.
///
/// The editor path is unchanged, so these assertions are conditional: in Coral
/// mode the calls go through as they always have.
/// </summary>
public class StaticDispatchRoutingTests
{
    [Fact]
    public void UnknownBusEventPair_IsAHardErrorNamingTheSite()
    {
#if O3DE_HOST_NATIVEAOT
        var act = () => NativeReflection.BroadcastEBusEvent("NoSuchBus", "NoSuchEvent");

        act.Should().Throw<NotSupportedException>()
            .Which.Message.Should().Contain("NoSuchBus").And.Contain("NoSuchEvent");
#else
        // In the editor this reaches the native dispatcher, which is not present
        // in a test host - the assertion here is only that no static-dispatch
        // gate was introduced into the editor path.
        typeof(NativeReflection).Should().NotBeNull();
#endif
    }

    [Fact]
    public void HardError_ExplainsWhyAndWhatToDo()
    {
#if O3DE_HOST_NATIVEAOT
        var act = () => NativeReflection.SendEBusEvent("NoSuchBus", "NoSuchEvent", 0UL);

        var message = act.Should().Throw<NotSupportedException>().Which.Message;
        message.Should().Contain("NativeAOT",
            "the message has to say which build config this is, or it reads as a generic bug");
        message.Should().Contain("O3DESHARP1001",
            "pointing at the build warning is what turns a runtime failure into a fixable one");
#else
        typeof(NativeReflection).Should().NotBeNull();
#endif
    }

    [Fact]
    public void TableIsConsultedBeforeTheNativeCall()
    {
#if O3DE_HOST_NATIVEAOT
        // A miss must never reach the native dispatcher: without a table entry
        // the argument blob's shape is unvalidated, and handing an unvalidated
        // blob to BehaviorContext is the memory-unsafe outcome, not merely a
        // wrong one.
        var act = () => NativeReflection.BroadcastEBusEvent("NoSuchBus", "NoSuchEvent", 1, 2, 3);
        act.Should().Throw<NotSupportedException>();
#else
        typeof(NativeReflection).Should().NotBeNull();
#endif
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo -p:DefineConstants=O3DE_HOST_NATIVEAOT --filter "FullyQualifiedName~StaticDispatchRoutingTests"`
Expected: `Failed!  - Failed:     3, Passed:     0` — `Expected a <System.NotSupportedException> to be thrown, but no exception was thrown.`

- [ ] **Step 3: Add the gate**

In `Assets/Scripts/O3DE.Core/Reflection/NativeReflection.cs`, insert this helper immediately before `BroadcastEBusEvent` (line 336):

```csharp
        /// <summary>
        /// Closed-world gate for the shipping NativeAOT build.
        ///
        /// Desktop NativeAOT supports only dispatch the generator could see at
        /// compile time. A pair that is not in the generated table has no
        /// validated argument shape, and handing an unvalidated argument blob to
        /// BehaviorContext is memory-unsafe rather than merely wrong - so this
        /// throws before the native call, never after.
        ///
        /// Throwing rather than returning null is the point: a null return is
        /// indistinguishable from "no handlers were listening", which is exactly
        /// the silent degrade the design rules out. The message names the site
        /// and points at the build warning that predicted it.
        ///
        /// Compiled out entirely in the editor build.
        /// </summary>
        [System.Diagnostics.Conditional("O3DE_HOST_NATIVEAOT")]
        private static void EnsureStaticallyDispatchable(string busName, string eventName, int argCount)
        {
#if O3DE_HOST_NATIVEAOT
            if (!StaticEBusDispatch.TryGetShape(busName, eventName, out int arity, out _))
            {
                throw new NotSupportedException(
                    $"EBus dispatch '{busName}.{eventName}' is not in this NativeAOT image's static " +
                    $"dispatch table ({StaticEBusDispatch.EntryCount} entries, built from " +
                    "reflection_data.json). Desktop NativeAOT supports closed-world dispatch only; " +
                    "the build reported O3DESHARP1001 for any call site whose bus or event name is " +
                    "not a compile-time constant. Constant-fold the name, regenerate " +
                    "reflection_data.json if the bus is new, or ship the Mono backend.");
            }

            if (argCount != arity)
            {
                throw new NotSupportedException(
                    $"EBus dispatch '{busName}.{eventName}' expects {arity} argument(s) but was " +
                    $"given {argCount}. This NativeAOT image was built against a reflection_data.json " +
                    "in which the event had a different signature; regenerate it and republish.");
            }
#endif
        }
```

Then add the call as the first statement of `BroadcastEBusEvent` (line 338) and `SendEBusEvent` (line 425) respectively:

```csharp
            EnsureStaticallyDispatchable(busName, eventName, args?.Length ?? 0);
```

- [ ] **Step 4: Link the generated table into the test project**

The generated `StaticEBusDispatch` lives in `O3DE.Core`'s compilation, which `O3DE.Core.Tests` does not reference. Add a minimal stand-in so the routing logic is testable, at `Assets/Scripts/O3DE.Core.Tests/Stubs/StaticEBusDispatchStub.cs`:

```csharp
//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

namespace O3DE.Reflection
{
    /// <summary>
    /// Test-only stand-in for the generated StaticEBusDispatch. The real one is
    /// emitted into O3DE.Core's compilation from reflection_data.json, and this
    /// test assembly compiles source files rather than referencing that DLL (see
    /// the comment in O3DE.Core.Tests.csproj).
    ///
    /// Deliberately EMPTY: what these tests exercise is the miss path - the hard
    /// error - and an empty table makes every lookup a miss. The emit itself is
    /// covered by Editor/Tests/test_static_dispatch_emit.py.
    /// </summary>
    internal static class StaticEBusDispatch
    {
        internal static int EntryCount => 0;

        internal static bool TryGetShape(string busName, string eventName, out int arity, out bool isBroadcast)
        {
            arity = 0;
            isBroadcast = false;
            return false;
        }
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo -p:DefineConstants=O3DE_HOST_NATIVEAOT --filter "FullyQualifiedName~StaticDispatchRoutingTests"`
Expected: `Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3`

- [ ] **Step 6: Confirm the editor path is unchanged**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/O3DE.Core.Tests.csproj -c Release --nologo`
Expected: `Passed!  - Failed:     0, Passed:   103, Skipped:     0, Total:   103` (100 + 3, all three taking the `#else` arm).

Then confirm the gate genuinely compiles away in the editor build:
Run: `dotnet build Assets/Scripts/O3DE.Core/O3DE.Core.csproj -c Release --nologo -t:Rebuild 2>&1 | grep -c "error\|IL[0-9]"`
Expected: `0`

- [ ] **Step 7: Commit**

```bash
git add Assets/Scripts/O3DE.Core/Reflection/NativeReflection.cs Assets/Scripts/O3DE.Core.Tests/StaticDispatchRoutingTests.cs Assets/Scripts/O3DE.Core.Tests/Stubs/StaticEBusDispatchStub.cs
git commit -m "M4: hard-error on out-of-table EBus dispatch in NativeAOT images

The runtime half of the closed-world decision. O3DESHARP1001 warns at build
time; this is what happens if a warned-past call is actually reached in a
shipped game.

It throws rather than returning null on purpose: a null return is
indistinguishable from 'no handlers were listening', which is precisely the
silent degrade the design rules out. The message names the bus, the event, the
table size and the build warning that predicted it.

The check runs BEFORE the native call. Without a table entry the argument
blob's shape is unvalidated, and handing an unvalidated blob to BehaviorContext
is memory-unsafe rather than merely wrong.

[Conditional(\"O3DE_HOST_NATIVEAOT\")] plus an #if body, so the editor build
compiles the gate away entirely and is byte-for-byte unaffected."
```

---

### Task 17: CMake — publish `O3DE.Core` as a NativeAOT shared library

**Files:**
- Create: `Code/o3desharp_nativeaot_publish.cmake`
- Modify: `Code/CMakeLists.txt:274-285` (options) and `:586-598` (deploy)
- Test: `Editor/Tests/test_nativeaot_publish.py`

**Interfaces:**
- Produces (consumed by Tasks 18, 19): CMake option `O3DESHARP_PUBLISH_NATIVEAOT` (default `OFF`), function `o3de_sharp_publish_nativeaot(out_dir_var)`, target `${gem_name}.PublishNativeAot`, deploy into `Bin/Scripts/aot`.

> Mirrors the M2 runtime-bundle precedent exactly (`Code/o3desharp_runtime_bundle.cmake`): a self-contained `.cmake` module defining one function, an opt-in option so the default build is untouched, a configure-time `message(STATUS)`, the `FOLDER` property computed locally, and the same "build the target once, then re-configure so the glob picks up the output" caveat.

- [ ] **Step 1: Write the failing test**

Create `Editor/Tests/test_nativeaot_publish.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Proves the M4 publish mechanism: O3DE.Core compiles to a native shared
library exporting the ABI entry point, with no CoreCLR involved.

VERIFIED PREMISE (2026-08-21): NativeAOT publish works in this environment, but
the ILCompiler targets shell out to vswhere.exe, so the Visual Studio Installer
directory must be on PATH or the native link step fails with MSB3073 exit 123.
The test adds it rather than requiring a Developer Command Prompt.

win-x64 only here. linux-x64 needs a Linux toolchain and is the maintainer's
verification - see the M4 docs task.
"""

import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
CORE_CSPROJ = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "O3DE.Core.csproj"
PUBLISH_CMAKE = GEM_ROOT / "Code" / "o3desharp_nativeaot_publish.cmake"
ROOT_CMAKE = GEM_ROOT / "Code" / "CMakeLists.txt"

VS_INSTALLER = r"C:\Program Files (x86)\Microsoft Visual Studio\Installer"


@pytest.mark.unit
def test_publish_module_exists_and_is_opt_in():
    assert PUBLISH_CMAKE.is_file(), f"{PUBLISH_CMAKE} is missing."
    module = PUBLISH_CMAKE.read_text(encoding="utf-8")
    assert "o3de_sharp_publish_nativeaot" in module

    root = ROOT_CMAKE.read_text(encoding="utf-8")
    assert re.search(
        r'option\(O3DESHARP_PUBLISH_NATIVEAOT\s+"[^"]*"\s+OFF\)', root
    ), "the publish must default OFF so the existing build is untouched."
    assert "include(o3desharp_nativeaot_publish.cmake)" in root


@pytest.mark.unit
def test_publish_passes_the_shipping_host_mode():
    module = PUBLISH_CMAKE.read_text(encoding="utf-8")
    for flag in ("-p:PublishAot=true", "-p:NativeLib=Shared", "-p:O3DESharpHostMode=NativeAot"):
        assert flag in module, (
            f"{flag} is required: without NativeLib=Shared there is no shared library, and "
            f"without the host mode HostExportsGenerator emits no exported entry point."
        )


@pytest.mark.unit
def test_deploy_target_is_separate_from_the_coral_deploy():
    root = ROOT_CMAKE.read_text(encoding="utf-8")
    assert 'OUTPUT_SUBDIRECTORY "Bin/Scripts/aot"' in root, (
        "the AOT image must deploy alongside, not over, the Coral/O3DE.Core "
        "managed assemblies - the two artifacts are mutually exclusive per "
        "launcher, and mixing them in one directory invites loading the wrong one."
    )


@pytest.mark.slow
def test_o3de_core_publishes_as_a_native_shared_library(tmp_path):
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    if sys.platform != "win32":
        pytest.skip("win-x64 publish; linux-x64 is verified by the maintainer")

    env = dict(os.environ)
    if Path(VS_INSTALLER).is_dir():
        # ILCompiler's link step shells out to vswhere.exe. Without this the
        # publish fails with MSB3073 exit 123 in a plain shell.
        env["PATH"] = VS_INSTALLER + os.pathsep + env.get("PATH", "")

    out = tmp_path / "aot"
    result = subprocess.run(
        [
            "dotnet", "publish", str(CORE_CSPROJ), "-c", "Release", "-r", "win-x64",
            "-p:PublishAot=true", "-p:NativeLib=Shared",
            "-p:O3DESharpHostMode=NativeAot",
            "-o", str(out), "--nologo",
        ],
        capture_output=True, text=True, timeout=1800, env=env,
    )
    assert result.returncode == 0, result.stdout + result.stderr

    native = out / "O3DE.Core.dll"
    assert native.is_file(), f"no native library in {sorted(p.name for p in out.iterdir())}"
    # A managed O3DE.Core.dll is ~100 KB; a NativeAOT image is multi-megabyte.
    # This distinguishes "published the native library" from "copied the IL one".
    assert native.stat().st_size > 2_000_000, (
        f"O3DE.Core.dll is only {native.stat().st_size} bytes - that is the managed "
        "assembly, not a NativeAOT image."
    )
    assert (out / "O3DE.Core.lib").is_file(), "the import library proves a real native link ran"


@pytest.mark.slow
def test_published_library_exports_the_abi_entry_point(tmp_path):
    if shutil.which("dotnet") is None or sys.platform != "win32":
        pytest.skip("win-x64 only")
    dumpbin = shutil.which("dumpbin")
    if dumpbin is None:
        pytest.skip("dumpbin not on PATH; the export is verified at runtime by NativeAotHost")

    env = dict(os.environ)
    if Path(VS_INSTALLER).is_dir():
        env["PATH"] = VS_INSTALLER + os.pathsep + env.get("PATH", "")

    out = tmp_path / "aot"
    subprocess.run(
        [
            "dotnet", "publish", str(CORE_CSPROJ), "-c", "Release", "-r", "win-x64",
            "-p:PublishAot=true", "-p:NativeLib=Shared",
            "-p:O3DESharpHostMode=NativeAot", "-o", str(out), "--nologo",
        ],
        check=True, capture_output=True, text=True, timeout=1800, env=env,
    )

    exports = subprocess.run(
        [dumpbin, "/exports", str(out / "O3DE.Core.dll")],
        capture_output=True, text=True, timeout=300,
    ).stdout
    assert "O3DESharp_GetManagedExports" in exports, (
        "NativeAotHost resolves exactly this symbol; without it the image is unusable."
    )
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest Editor/Tests/test_nativeaot_publish.py -q`
Expected: `3 failed` — `AssertionError: .../Code/o3desharp_nativeaot_publish.cmake is missing.` (the two `slow` tests are deselected).

- [ ] **Step 3: Write the publish module**

Create `Code/o3desharp_nativeaot_publish.cmake`:

```cmake
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
# Publishes O3DE.Core as a NativeAOT shared library for the shipping desktop
# build: no CoreCLR, no hostfxr, no Coral. NativeAotHost LoadLibrary/dlopen's
# the result and resolves one exported symbol.
#
# Opt-in via O3DESHARP_PUBLISH_NATIVEAOT (default OFF). The default build stays
# on the Coral path and is unchanged. The two artifacts are mutually exclusive
# per launcher - a NativeAOT image has no JIT and nothing for nethost -> hostfxr
# to attach to - which is why they deploy to different directories rather than
# both landing in Bin/Scripts.
#
# NOTE on deployment, same shape as the M2 runtime bundle: the published file
# set is not knowable at configure time (it varies by RID and by which
# ILCompiler resolves), so the CMakeLists.txt deploy block globs the published
# directory. Practical consequence: on the first configure after turning the
# option on the directory does not exist yet, the glob is empty, and nothing is
# queued. Build ${gem_name}.PublishNativeAot once, then re-run CMake configure.
#
# NOTE on the toolchain: ILCompiler's native link step shells out to vswhere.exe
# on Windows. A build launched from an environment without the Visual Studio
# Installer directory on PATH fails with MSB3073 exit code 123. CMake-driven
# builds from the VS generator inherit a developer environment and are fine;
# command-line builds from a bare shell may not be.

function(o3de_sharp_publish_nativeaot out_dir_var)
    # Desktop RIDs only. Console/mobile is the Mono milestone, not this one.
    if(WIN32)
        set(_rid "win-x64")
    elseif(APPLE)
        if(CMAKE_SYSTEM_PROCESSOR MATCHES "arm64|aarch64")
            set(_rid "osx-arm64")
        else()
            set(_rid "osx-x64")
        endif()
    else()
        set(_rid "linux-x64")
    endif()

    get_property(_gem_root GLOBAL PROPERTY "@GEMROOT:${gem_name}@")
    set(_csproj "${_gem_root}/Assets/Scripts/O3DE.Core/O3DE.Core.csproj")
    set(_out "${CMAKE_BINARY_DIR}/Gems/O3DESharp/NativeAot/${_rid}")

    # Same graceful degradation as the runtime bundle: an install/export tree
    # that ships Code/ without the C# sources must not fail configure over an
    # experimental, opt-in feature.
    if(NOT EXISTS "${_csproj}")
        message(WARNING
            "O3DESharp: O3DESHARP_PUBLISH_NATIVEAOT is ON but O3DE.Core.csproj is missing at "
            "${_csproj}. Skipping the NativeAOT publish.")
        return()
    endif()

    add_custom_target(${gem_name}.PublishNativeAot
        COMMENT "O3DESharp: publishing O3DE.Core as a NativeAOT shared library (${_rid})"
        COMMAND ${CMAKE_COMMAND} -E make_directory "${_out}"
        COMMAND ${DOTNET_EXECUTABLE} publish "${_csproj}"
                -c Release -r ${_rid}
                # NativeLib=Shared is what makes this a loadable library rather
                # than an executable; without it there is nothing to dlopen.
                -p:PublishAot=true
                -p:NativeLib=Shared
                # Without the host mode HostExportsGenerator emits no
                # O3DESharp_GetManagedExports and NativeAotHost cannot resolve
                # anything - the image loads and is then useless.
                -p:O3DESharpHostMode=NativeAot
                -o "${_out}"
        VERBATIM
    )

    # Computed locally rather than relying on the including file's
    # relative_o3desharp_gem_root, which is referenced above its own assignment
    # in CMakeLists.txt - same reasoning as o3desharp_runtime_bundle.cmake.
    ly_get_engine_relative_source_dir(${_gem_root} _relative_gem_root)
    set_property(TARGET ${gem_name}.PublishNativeAot
        PROPERTY FOLDER "${_relative_gem_root}/Deploy")

    set(${out_dir_var} "${_out}" PARENT_SCOPE)
endfunction()
```

- [ ] **Step 4: Wire the option into `Code/CMakeLists.txt`**

Add immediately after the `O3DESHARP_BUNDLE_DOTNET_RUNTIME` block (lines 276-285):

```cmake
# Publish O3DE.Core as a NativeAOT shared library for the shipping desktop build
# (M4, experimental). OFF keeps the Coral/CoreCLR path as the only artifact.
# The two are mutually exclusive per launcher, never a runtime switch.
option(O3DESHARP_PUBLISH_NATIVEAOT
    "Publish O3DE.Core as a NativeAOT shared library for shipping (experimental)" OFF)

if(O3DESHARP_PUBLISH_NATIVEAOT)
    include(o3desharp_nativeaot_publish.cmake)
    o3de_sharp_publish_nativeaot(O3DESHARP_NATIVEAOT_DIR)
    if(TARGET ${gem_name}.PublishNativeAot)
        message(STATUS "O3DESharp: NativeAOT image -> ${O3DESHARP_NATIVEAOT_DIR}")
    endif()
endif()
```

- [ ] **Step 5: Wire the deploy**

Add immediately after the runtime-bundle deploy block (lines 586-598):

```cmake
# Deploy the NativeAOT scripting image (M4, opt-in via
# O3DESHARP_PUBLISH_NATIVEAOT) into Bin/Scripts/aot - deliberately NOT
# Bin/Scripts, where the managed O3DE.Core.dll lives. The native image has the
# same filename, and a launcher that loaded the wrong one would fail in a way
# that reads as an unrelated bug. Keeping them in sibling directories makes the
# artifact a launcher uses an explicit choice.
#
# Same first-configure caveat as the runtime bundle: PublishNativeAot only
# produces the directory at BUILD time, so build that target once and re-run
# CMake configure for the glob to pick it up.
if(O3DESHARP_PUBLISH_NATIVEAOT AND TARGET ${gem_name}.PublishNativeAot)
    file(GLOB O3DESHARP_NATIVEAOT_FILES "${O3DESHARP_NATIVEAOT_DIR}/*")
    if(O3DESHARP_NATIVEAOT_FILES)
        ly_add_target_files(
            TARGETS ${gem_name}.Clients ${gem_name}.Servers ${gem_name}.Unified
            FILES ${O3DESHARP_NATIVEAOT_FILES}
            OUTPUT_SUBDIRECTORY "Bin/Scripts/aot"
        )
        message(STATUS "O3DESharp: NativeAOT scripting image will be deployed to Bin/Scripts/aot/")
    else()
        message(STATUS "O3DESharp: NativeAOT image not yet built at ${O3DESHARP_NATIVEAOT_DIR} - build ${gem_name}.PublishNativeAot then re-run CMake configure to deploy it")
    endif()
endif()
```

- [ ] **Step 6: Run test to verify it passes**

Run: `python -m pytest Editor/Tests/test_nativeaot_publish.py -q`
Expected: `3 passed`

Then the real publish (long — allow up to 30 minutes on a cold ILCompiler restore):
Run: `python -m pytest Editor/Tests/test_nativeaot_publish.py -q -m slow`
Expected: `2 passed`, or `1 passed, 1 skipped` if `dumpbin` is not on `PATH`.

- [ ] **Step 7: Commit**

```bash
git add Code/o3desharp_nativeaot_publish.cmake Code/CMakeLists.txt Editor/Tests/test_nativeaot_publish.py
git commit -m "M4: opt-in CMake step to publish O3DE.Core as a NativeAOT library

Mirrors the M2 runtime-bundle precedent: a self-contained cmake module with one
function, opt-in so the default build is untouched, and the same
build-once-then-reconfigure caveat because the published file set is not
knowable at configure time.

Deploys to Bin/Scripts/aot, deliberately not Bin/Scripts. The native image has
the same filename as the managed O3DE.Core.dll, and a launcher loading the
wrong one would fail in a way that reads as an unrelated bug; sibling
directories make the choice explicit.

NativeLib=Shared and O3DESharpHostMode=NativeAot are both load-bearing: without
the first there is nothing to dlopen, without the second the generator emits no
exported entry point and the image loads but is useless.

Verified on win-x64. ILCompiler's link step shells out to vswhere.exe, so a
bare shell needs the VS Installer directory on PATH or it fails MSB3073/123."
```

---

### Task 18: The sample game's scripts

**Files:**
- Create: `Assets/Scripts/Examples/Examples.csproj`
- Create: `Assets/Scripts/Examples/AotSampleComponent.cs`
- Test: `Editor/Tests/test_sample_aot_publish.py`

**Interfaces:**
- Consumes: `O3DE.Core` (`ScriptComponent`, `NativeReflection`), the analyzer and generators (Tasks 6, 14, 15).
- Produces (consumed by Task 19): a publishable sample scripts assembly, `Examples.dll` / its NativeAOT image.

> The repo already has `Assets/Scripts/Examples/PlayerController.cs` and `ReflectionExample.cs` as loose files with no csproj — they are exactly the "sample game's scripts" M4's exit criteria names, and `ReflectionExample.cs:267,270` already contains constant-name EBus calls. Giving them a csproj turns them into a publishable artifact; one new component adds the `ScriptComponent` subclass the registry generator needs and a deliberately-dynamic call to prove the diagnostic fires on real game code.

- [ ] **Step 1: Write the failing test**

Create `Editor/Tests/test_sample_aot_publish.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""The M4 exit criteria, as far as they are checkable without a launcher.

A sample game's scripts must build against O3DE.Core, register themselves with
the script-type registry through generated factories, and trip the closed-world
diagnostic on a deliberately-dynamic call. Actually RUNNING them in a desktop
NativeAOT launcher needs an engine build and is the maintainer's step.
"""

import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
EXAMPLES = GEM_ROOT / "Assets" / "Scripts" / "Examples" / "Examples.csproj"
SAMPLE = GEM_ROOT / "Assets" / "Scripts" / "Examples" / "AotSampleComponent.cs"
VS_INSTALLER = r"C:\Program Files (x86)\Microsoft Visual Studio\Installer"


def _build(*extra):
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    return subprocess.run(
        ["dotnet", "build", str(EXAMPLES), "-c", "Release", "--nologo", "-t:Rebuild", *extra],
        capture_output=True, text=True, timeout=900,
    )


@pytest.mark.unit
def test_sample_project_exists_and_references_o3de_core():
    assert EXAMPLES.is_file(), f"{EXAMPLES} is missing."
    text = EXAMPLES.read_text(encoding="utf-8")
    assert "O3DE.Core.csproj" in text
    assert "<TargetFramework>net9.0</TargetFramework>" in text, (
        "must match O3DE.Core's TFM; a mismatch is the drift class a prior audit caught."
    )


@pytest.mark.unit
def test_sample_does_not_emit_its_own_host_exports():
    # ManagedExports lives in O3DE.Core alone. A second copy here would make
    # the type name ambiguous the moment both were referenced.
    assert "O3DESharpEmitHostExports" not in EXAMPLES.read_text(encoding="utf-8")


@pytest.mark.slow
def test_sample_builds_and_the_diagnostic_fires_on_the_dynamic_call():
    result = _build()
    assert result.returncode == 0, result.stdout + result.stderr

    warned = re.findall(r"AotSampleComponent\.cs\((\d+),\d+\): warning O3DESHARP1001", result.stdout)
    assert warned, (
        "the deliberately-dynamic call in AotSampleComponent must trip O3DESHARP1001 - "
        "that is the closed-world diagnostic firing on real game code, not just a fixture."
    )

    text = SAMPLE.read_text(encoding="utf-8").splitlines()
    closed = {i + 1 for i, line in enumerate(text) if "// CLOSED-WORLD" in line}
    assert not (closed & {int(n) for n in warned}), (
        "the constant-name calls in the sample must stay silent."
    )


@pytest.mark.slow
def test_sample_publishes_as_nativeaot(tmp_path):
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    if sys.platform != "win32":
        pytest.skip("win-x64 publish; linux-x64 is verified by the maintainer")

    env = dict(os.environ)
    if Path(VS_INSTALLER).is_dir():
        env["PATH"] = VS_INSTALLER + os.pathsep + env.get("PATH", "")

    out = tmp_path / "aot"
    result = subprocess.run(
        [
            "dotnet", "publish", str(EXAMPLES), "-c", "Release", "-r", "win-x64",
            "-p:PublishAot=true", "-p:NativeLib=Shared",
            "-p:O3DESharpHostMode=NativeAot", "-o", str(out), "--nologo",
        ],
        capture_output=True, text=True, timeout=1800, env=env,
    )
    assert result.returncode == 0, result.stdout + result.stderr

    native = out / "Examples.dll"
    assert native.is_file()
    assert native.stat().st_size > 2_000_000, (
        "that is the managed assembly, not a NativeAOT image"
    )
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest Editor/Tests/test_sample_aot_publish.py -q`
Expected: `2 failed` — `AssertionError: .../Assets/Scripts/Examples/Examples.csproj is missing.`

- [ ] **Step 3: Add the sample component**

Create `Assets/Scripts/Examples/AotSampleComponent.cs`:

```csharp
/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using O3DE;
using O3DE.Reflection;

namespace Examples
{
    /// <summary>
    /// The sample game's NativeAOT exercise.
    ///
    /// Three things have to be true of it, and each is asserted somewhere:
    ///   1. It is a concrete ScriptComponent subclass with a public
    ///      parameterless constructor, so HostExportsGenerator emits a
    ///      ScriptTypeRegistry factory for it and a shipping image can
    ///      construct it without Activator.
    ///   2. Its normal EBus traffic uses constant names, so it dispatches
    ///      statically and stays silent under O3DESHARP1001.
    ///   3. It has one deliberately-dynamic call, so the closed-world
    ///      diagnostic is proven to fire on real game code rather than only on
    ///      a synthetic fixture.
    /// </summary>
    public class AotSampleComponent : ScriptComponent
    {
        [ExposedProperty("Broadcast Interval")]
        public float BroadcastInterval = 1.0f;

        private float _elapsed;

        public override void OnCreate()
        {
            Debug.Log("[AotSample] created");
        }

        public override void OnUpdate(float deltaTime)
        {
            _elapsed += deltaTime;
            if (_elapsed < BroadcastInterval)
            {
                return;
            }
            _elapsed = 0.0f;

            // Constant bus and event names: resolvable at compile time, so the
            // generated table covers them and the shipping image dispatches
            // without touching managed reflection.
            NativeReflection.BroadcastEBusEvent("TickBus", "OnTick", deltaTime); // CLOSED-WORLD
        }

        /// <summary>
        /// Deliberately dynamic. This is expected to produce O3DESHARP1001 at
        /// build time and NotSupportedException if reached in a NativeAOT image
        /// - it is the sample's proof that the restriction is diagnosed rather
        /// than silently degraded, so the warning here is intentional and must
        /// NOT be suppressed.
        /// </summary>
        public void DispatchByRuntimeName(string busName, string eventName)
        {
            NativeReflection.BroadcastEBusEvent(busName, eventName); // OPEN-WORLD
        }
    }
}
```

- [ ] **Step 4: Add the sample project**

Create `Assets/Scripts/Examples/Examples.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    The sample game's scripts. These files already existed as loose .cs with no
    project; giving them one makes them a publishable artifact so M4's exit
    criteria - a sample game's scripts running in a desktop NativeAOT launcher -
    has something concrete to point at.

    Deliberately does NOT set O3DESharpEmitHostExports: ManagedExports lives in
    O3DE.Core alone, and a second copy here would make the type name ambiguous
    the moment both assemblies were referenced together.
  -->
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <LangVersion>latest</LangVersion>

    <Configurations>Debug;Profile;Release</Configurations>
    <Platforms>AnyCPU</Platforms>

    <AssemblyName>Examples</AssemblyName>
    <RootNamespace>Examples</RootNamespace>
    <OutputType>Library</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\O3DE.Core\O3DE.Core.csproj">
      <Private>false</Private>
    </ProjectReference>
    <!--
      The analyzer is referenced separately from O3DE.Core's own analyzer
      reference: O3DE.Core marks it ReferenceOutputAssembly=false, which does
      not flow to consumers. Without this line the closed-world diagnostic never
      runs on game code, which is the only place it matters.
    -->
    <ProjectReference Include="..\..\..\Code\Tools\SourceGenerators\O3DESharp.SourceGenerators.csproj"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
  </ItemGroup>

</Project>
```

- [ ] **Step 5: Run test to verify it passes**

Run: `python -m pytest Editor/Tests/test_sample_aot_publish.py -q -m "unit or slow"`
Expected: `4 passed` (or `3 passed, 1 skipped` off win-x64).

- [ ] **Step 6: Confirm the registry factory was generated for it**

Run: `dotnet build Assets/Scripts/Examples/Examples.csproj -c Release --nologo -t:Rebuild -p:EmitCompilerGeneratedFiles=true && grep -r "Examples.AotSampleComponent" Assets/Scripts/O3DE.Core/obj/Release/net9.0/generated/ | head -3`
Expected: no match — the registry is generated into `O3DE.Core`'s own compilation, which does not see the sample's types. This is expected and is exactly why the shipping publish (Task 17 / 19) publishes the game's assembly with `O3DESharpEmitHostExports` unset and relies on `GeneratedScriptTypes.RegisterAll()` covering only `O3DE.Core`'s own subclasses.

Record the gap explicitly:
Run: `grep -c "GeneratedScriptTypes" Code/Tools/SourceGenerators/HostExportsGenerator.cs`
Expected: a non-zero count, confirming the emit exists. **Per-game script registration is a known limitation** — see "What is NOT in this plan".

- [ ] **Step 7: Commit**

```bash
git add Assets/Scripts/Examples/Examples.csproj Assets/Scripts/Examples/AotSampleComponent.cs Editor/Tests/test_sample_aot_publish.py
git commit -m "M4: make the example scripts a publishable sample project

The example scripts already existed as loose .cs files with no project; giving
them one makes them the concrete artifact M4's exit criteria points at.

AotSampleComponent covers all three things the sample has to prove: it is a
constructible ScriptComponent subclass, its normal EBus traffic uses constant
names and stays silent, and one deliberately-dynamic call trips O3DESHARP1001
on real game code rather than only on a synthetic fixture. That warning is
intentional and must not be suppressed.

The analyzer is referenced directly because O3DE.Core's own reference is
ReferenceOutputAssembly=false and does not flow to consumers - without this the
diagnostic never runs where it matters."
```

---

### Task 19: Documentation and maintainer verification

**Files:**
- Modify: `README.md`
- Test: none (documentation + a maintainer-executed checklist)

> The M4 exit criteria — *a sample game's scripts run in a desktop NativeAOT launcher via `NativeAotHost`* — needs a real O3DE engine build and cannot be reached from this environment. This task writes the documentation and states the verification the maintainer performs, mirroring how M2 Task 5 and SP-1a Task 6 handled the same boundary.

- [ ] **Step 1: Document the shipping AOT build**

Add a `### Shipping with NativeAOT (experimental)` subsection under the deployment docs in `README.md`, covering:

- `-DO3DESHARP_PUBLISH_NATIVEAOT=ON`, then build `O3DESharp.PublishNativeAot`, then **re-run CMake configure** so the deploy glob picks up the output (same first-configure caveat as the M2 runtime bundle).
- The image lands in `Bin/Scripts/aot/`, deliberately separate from `Bin/Scripts/` — the native `O3DE.Core.dll` and the managed one share a filename, and a launcher loading the wrong one fails in a way that reads as an unrelated bug.
- Windows command-line builds need `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on `PATH`; ILCompiler's link step shells out to `vswhere.exe` and otherwise fails with `MSB3073` exit code 123.
- **No hot-reload in AOT builds** — editor-only by design; `NativeAotHost::SupportsHotReload()` is an unconditional `false`.
- **Closed-world dispatch only.** `O3DESHARP1001` at build time for any non-constant bus/event name, `NotSupportedException` at runtime if such a call is reached. Constant-fold the name, regenerate `reflection_data.json` if the bus is new, or stay on the Coral artifact.
- The two artifacts are mutually exclusive **per launcher**, never a runtime switch: `O3DESHARP_BUNDLE_DOTNET_RUNTIME` (M2, CoreCLR + Coral) and `O3DESHARP_PUBLISH_NATIVEAOT` (M4) target different launchers.
- `win-x64` is verified; `linux-x64` is authored but not verified in the maintainer's absence.

- [ ] **Step 2: Verify the doc landed**

Run: `python -c "import pathlib; t = pathlib.Path('README.md').read_text(encoding='utf-8'); assert 'Shipping with NativeAOT' in t; assert 'O3DESHARP1001' in t; assert 'Bin/Scripts/aot' in t; print('README section present')"`
Expected: `README section present`

- [ ] **Step 3: Commit the docs**

```bash
git add README.md
git commit -m "Docs: experimental NativeAOT shipping build

Covers the opt-in CMake option and its build-once-then-reconfigure caveat, why
the image deploys to Bin/Scripts/aot rather than over the managed assemblies,
the vswhere.exe requirement on Windows command-line builds, and - most
importantly - that dispatch is closed-world only, with O3DESHARP1001 at build
time and a hard error at runtime rather than a silent degrade."
```

- [ ] **Step 4: MAINTAINER-EXECUTED — engine build**

Requires a real O3DE engine SDK. Every C++ file in Tasks 2, 11, 12 and 13 was authored blind.

Build the gem with the Coral path (default, `O3DESHARP_PUBLISH_NATIVEAOT=OFF`).
Expected: builds clean. Compilation errors in `HostAbi.h`, `IManagedHost.h`, `CoralHost.cpp`, `NativeAotHost.cpp` or `ScriptBindings.cpp` are expected fallout of authoring C++ without a compiler; fix and report them.

- [ ] **Step 5: MAINTAINER-EXECUTED — M3 exit criteria**

Run the Editor with a C# script component on an entity.
Expected: **behaviour identical to before M3.** Specifically confirm in the log:
- `O3DESharpSystemComponent: IManagedHost registered (ABI v1, hot-reload supported)`
- `CoralHost: resolved all 5 ManagedExports thunks`
- **No** `CoralHost: ManagedExports could not be fully resolved` — that line means `HostExportsGenerator` did not emit, or `O3DE.Core.dll` was not redeployed after the change, and the seam delivered nothing.

Then confirm `OnCreate`, `Tick`, `OnTransformChanged` and `OnDestroy` still fire in order, hot-reload still works, and the inspector still shows exposed properties. Any difference is an M3 failure — M3 is behaviour-preserving.

- [ ] **Step 6: MAINTAINER-EXECUTED — M4 exit criteria**

Configure with `-DO3DESHARP_PUBLISH_NATIVEAOT=ON`, build `O3DESharp.PublishNativeAot`, re-run configure, build a desktop launcher, and run the sample game's scripts through `NativeAotHost`.
Expected in the launcher log:
- `NativeAotHost: initialized (ABI v1, no CoreCLR, no hostfxr)`
- `AotSampleComponent` ticks and broadcasts on `TickBus`
- Calling `DispatchByRuntimeName` raises `NotSupportedException` naming the bus and event and citing `O3DESHARP1001` — **this firing is a pass, not a failure.** A call that silently succeeded or returned null would be the failure.

- [ ] **Step 7: MAINTAINER-EXECUTED — linux-x64**

Repeat Step 6 on Linux. `NativeAotHost`'s `dlopen`/`dlsym` paths and the `linux-x64` publish are **authored but not verified** — no Linux toolchain was available in the authoring environment, and cross-OS native linking is not possible from Windows. Report any breakage.

- [ ] **Step 8: MAINTAINER-EXECUTED — report**

Report: C++ build fixes, whether the exports resolved in the editor, whether the editor behaviour was identical, whether the sample ran under `NativeAotHost` on win-x64 and linux-x64, and whether the closed-world diagnostic fired at runtime as designed.

---

## What is NOT in this plan

**SP-1b Half B — the native binding manifest and trampolines.** `NativeBindingManifest`, `ReflectionCallSiteParser`, `NativeBindingJoin`, `BindingRegistry`, load-time manifest validation against the live `BehaviorContext`, differential testing and binding-coverage telemetry are a **separate, orthogonal track** (`docs/superpowers/plans/2026-07-15-sp1b1-native-binding-manifest-offline.md` and its unwritten SP-1b-2 sequel). That track recovers native C++ symbols so managed code can call them through generated trampolines. M4's static dispatch consumes the **existing `reflection_data.json`** and answers a different question — what the reflected *script* surface contains — so the two never touch. Do not merge them.

**M5 — console/mobile Mono-AOT.** `MonoAotHost`, mono embedding, and the interpreter tail that covers the open-world dispatch monomorphization cannot. This is the reason the capability gradient in the spec has three tiers rather than two: consoles get an interpreter tail, desktop NativeAOT deliberately does not. `IManagedHost` is shaped so `MonoAotHost` is a third implementation behind the same four methods, but nothing here builds it.

**The v1.3 open-world dispatch refactor.** `NativeReflection.InvokeStaticMethod` / `InvokeInstanceMethod` / `InvokeGlobalMethod` / `GetProperty` / `SetProperty` still throw `NotImplementedException` from the native dispatcher, exactly as they do today (`NativeReflection.cs:231,248,262,281,295`). This plan does not touch them, does not funnel dispatch through a single choke point, and does not redesign `GenericDispatcher` or `BehaviorMethod::Call`. The spec's Section 5 contract describes what v1.3 must provide; M4 implements only the closed-world subset that needs nothing from it.

**Trimming.** Nothing here sets `PublishTrimmed`, adds an `ILLink.Descriptors.xml`, or tunes a trim pass. The two `[UnconditionalSuppressMessage]` justifications in `ExposedProperty.cs` name themselves as the things a future trim pass must revisit. NativeAOT's intrinsic whole-program dead-code elimination is a property of that toolchain, not an opt-in made here.

**Hot-reload in AOT builds.** Editor-only by design. `NativeAotHost::SupportsHotReload()` is an unconditional `false`, `HotReloadSwap` returns 0 under `O3DE_HOST_NATIVEAOT`, and `HotReloadManager.cs` is compiled out entirely. There is no `AssemblyLoadContext` in a NativeAOT image to reload into, so this is a designed absence rather than a gap.

**Per-game script-type registration under NativeAOT.** `GeneratedScriptTypes.RegisterAll()` is emitted into `O3DE.Core` and therefore covers only `ScriptComponent` subclasses in `O3DE.Core`'s own compilation — not the game's. A shipping AOT build of a real game needs the game assembly to emit and call its own `RegisterAll`, which means either setting `O3DESharpEmitHostExports` per game with a namespaced type name, or an aggregating module initializer. Task 18 Step 6 records the gap deliberately rather than hiding it; closing it is the first task of whatever follows M4, and it is small.

**`ReflectionInternalCalls` in the ABI.** ABI v1 covers the 47 pointers of `O3DE.InternalCalls` only. The ~20 reflection-dispatcher pointers are registered separately by `GenericDispatcher` and reach a NativeAOT image through Coral-independent means that this plan does not change. Adding them is an ABI v2 change — which is what the version field exists for, and what the golden contract test will force to be done on both sides at once.

**The Coral fork.** No cross-repo change is required by this plan. `CoralNativeThunkHost` already has everything `CoralHost` needs (`GetFunctionPointer` landed as `WatchDogStudios/Coral@a98550d`), and `NativeAotHost` deliberately touches no Coral API at all.

**Mac desktop.** `o3desharp_nativeaot_publish.cmake` maps `osx-x64` / `osx-arm64` RIDs for completeness, but M4's exit criteria and all verification are win-x64 and linux-x64. Mac is neither built nor tested here.

### Critical Files for Implementation
- `F:\O3DESharp\Assets\Scripts\O3DE.Core\InternalCalls.cs` — the 47-field ordering that *is* ABI v1; every ABI artifact mirrors it
- `F:\O3DESharp\Code\Source\Scripting\CoralHostManager.cpp` — the host being wrapped (init at `:120`, ALC swap at `:364`, `RegisterInternalCalls` at `:692`)
- `F:\O3DESharp\Code\Tools\SourceGenerators\EBusHandlerGenerator.cs` — the proven `IIncrementalGenerator` pattern both new generators extend
- `F:\O3DESharp\Assets\Scripts\O3DE.Core\O3DE.Core.csproj` — where the build-mode split, AOT analyzers, `CompilerVisibleProperty` and `AdditionalFiles` all land
- `F:\O3DESharp\Code\CMakeLists.txt` — options block at `:274-285` and the launcher deploy block at `:586-598` that M4's publish target extends
