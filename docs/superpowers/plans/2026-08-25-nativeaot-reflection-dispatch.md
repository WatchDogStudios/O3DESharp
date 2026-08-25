# NativeAOT Reflection Dispatch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire `ReflectionInternalCalls`' 20 functions (today populated only by Coral) into the frozen NativeAOT ABI, so `NativeReflection` dispatch — and therefore every generated gem binding — actually works under NativeAOT instead of crashing on a null function pointer; and extend the existing EBus-only closed-world diagnostic (`O3DESHARP1001`) to the other 8 name-taking dispatch methods, so wiring doesn't quietly let non-provable dynamic dispatch through.

**Architecture:** `HostAbiVersion` bumps 1→2. The 20 `ReflectionInternalCalls` functions already exist and work under Coral (`GenericDispatcherInternalCalls` namespace, `GenericDispatcher.cpp`) — no new C++ dispatch logic, just register the same function pointers into `NativeImports` the way the existing 47-function table already does. `StaticDispatchGenerator` widens from EBus-only to also cover classes/global methods/global properties; `DynamicDispatchAnalyzer` widens from 4 checked method names to 12; `NativeReflection.cs` and `EBusHandlerRegistry.cs` gain runtime guards mirroring the existing `EnsureStaticallyDispatchable` pattern.

**Tech Stack:** C++ (AzCore, Coral-adjacent but Coral-free ABI), C# (.NET 9, Roslyn incremental generators + analyzers), Python (pytest regression tests for the golden ABI contract).

## Global Constraints

- `HostAbiVersion`/`HostAbi.Version` must move together (C++ and C#) — the golden contract test (`test_host_abi_contract.py`) fails the build otherwise.
- Field order in `NativeImports` is the ABI — insertions must be in the same order in both `HostAbi.h` and `HostAbi.cs`, and appended (not interleaved) after the existing 47 fields.
- No new C++ dispatch logic — every one of the 20 `ReflectionInternalCalls` functions already exists, is already tested under Coral, and is not to be modified. This plan only adds a second registration path (`NativeImports`) alongside the existing one (`AddInternalCall`).
- The closed-world guard (§3.4 of the spec) is a `[Conditional("O3DE_HOST_NATIVEAOT")]` runtime check plus an `O3DESHARP1001` build-time diagnostic — compiled out entirely in editor/Coral builds, matching the existing `EnsureStaticallyDispatchable` pattern exactly.
- Generated bindings (`O3DESharp.GeneratedBindings.csproj`) need zero source changes — every call site the generator emits is already a compile-time literal by construction. This plan only adds an analyzer reference so that fact becomes verified, not assumed.
- No live O3DE engine build is available in this environment. C++ changes are reviewed by careful reading against the established pattern and flagged "NOT COMPILE-VERIFIED" in commit messages, matching this repo's existing convention for prior NativeAOT C++ work.

---

### Task 1: ABI extension — `HostAbiVersion` 1 → 2

**Files:**
- Modify: `Code/Source/Scripting/HostAbi.h:42,59-138,174-176`
- Modify: `Assets/Scripts/O3DE.Core/Interop/HostAbi.cs` (`NativeImports` struct, `HostAbi.Version`)
- Modify: `Editor/Tests/test_host_abi_contract.py`

**Interfaces:**
- Produces: 20 new `NativeImports` fields (C++ `void*`, C# `IntPtr`), in this exact order, appended after the existing 47:
  `Reflection_GetClassNames`, `Reflection_GetMethodNames`, `Reflection_GetPropertyNames`, `Reflection_GetEBusNames`, `Reflection_GetEBusEventNames`, `Reflection_ClassExists`, `Reflection_MethodExists`, `Reflection_InvokeStaticMethod`, `Reflection_InvokeInstanceMethod`, `Reflection_InvokeGlobalMethod`, `Reflection_GetProperty`, `Reflection_SetProperty`, `Reflection_GetGlobalProperty`, `Reflection_SetGlobalProperty`, `Reflection_BroadcastEBusEvent`, `Reflection_SendEBusEvent`, `Reflection_RegisterEBusHandler`, `Reflection_UnregisterEBusHandler`, `Reflection_CreateInstance`, `Reflection_DestroyInstance`.
- `HostAbiVersion` (C++) / `HostAbi.Version` (C#) both become `2`.

- [ ] **Step 1: Write the failing test**

In `Editor/Tests/test_host_abi_contract.py`, add a helper for the new source file and a new test asserting `NativeImports` mirrors `InternalCalls` + `ReflectionInternalCalls` concatenated (67 total), replacing the old 47-only assertion. Add this helper right after `_csharp_internal_call_fields` (after line 63):

```python
REFLECTION_CS = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "Reflection" / "NativeReflection.cs"


def _csharp_reflection_internal_call_fields():
    text = _read(REFLECTION_CS)
    return re.findall(r"internal\s+static\s+delegate\*\s+unmanaged<[^>]*>\s+(\w+)\s*;", text)
```

Replace `test_native_imports_mirrors_internal_calls_exactly` (lines 76-86) with:

```python
@pytest.mark.unit
def test_native_imports_mirrors_internal_calls_exactly():
    internal_calls = _csharp_internal_call_fields()
    assert len(internal_calls) == 47, (
        f"O3DE.InternalCalls declares {len(internal_calls)} function pointers, not 47. "
        "Adding one is an ABI change: mirror it in BOTH HostAbi.cs and HostAbi.h and "
        "bump HostAbi.Version / HostAbiVersion together."
    )
    reflection_calls = _csharp_reflection_internal_call_fields()
    assert len(reflection_calls) == 20, (
        f"O3DE.Reflection.ReflectionInternalCalls declares {len(reflection_calls)} function "
        "pointers, not 20. Adding one is an ABI change: mirror it in BOTH HostAbi.cs and "
        "HostAbi.h and bump HostAbi.Version / HostAbiVersion together."
    )
    assert _csharp_struct_fields("NativeImports") == internal_calls + reflection_calls, (
        "NativeImports (C#) must mirror O3DE.InternalCalls, then "
        "O3DE.Reflection.ReflectionInternalCalls, field-for-field, in that order."
    )
```

`test_native_imports_cpp_matches_csharp` (lines 89-95) and `test_version_constants_agree` (104-113) need no changes — they already compare the two sides generically, not against a hardcoded count.

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest Editor/Tests/test_host_abi_contract.py -v`
Expected: FAIL — `_csharp_reflection_internal_call_fields()` returns 20 real fields (they already exist in `NativeReflection.cs` today), but `NativeImports` (C#) doesn't have them yet, so the field-list comparison fails.

- [ ] **Step 3: Add the 20 fields to `HostAbi.h`**

In `Code/Source/Scripting/HostAbi.h`, change line 42:
```cpp
    inline constexpr AZ::u32 HostAbiVersion = 2;
```

Replace the comment at lines 51-52 (now stale — these ARE part of the ABI as of this task):
```cpp
    //! O3DE.Reflection.ReflectionInternalCalls fields were added in v2 (see
    //! the block below) - previously deliberately excluded from v1.
```

Insert 20 new fields immediately after `void* Component_HasComponent;` (line 137), before the closing `};` (line 138):
```cpp

        // ============================================================
        // Reflection (added in HostAbiVersion 2)
        // ============================================================
        void* Reflection_GetClassNames;
        void* Reflection_GetMethodNames;
        void* Reflection_GetPropertyNames;
        void* Reflection_GetEBusNames;
        void* Reflection_GetEBusEventNames;
        void* Reflection_ClassExists;
        void* Reflection_MethodExists;
        void* Reflection_InvokeStaticMethod;
        void* Reflection_InvokeInstanceMethod;
        void* Reflection_InvokeGlobalMethod;
        void* Reflection_GetProperty;
        void* Reflection_SetProperty;
        void* Reflection_GetGlobalProperty;
        void* Reflection_SetGlobalProperty;
        void* Reflection_BroadcastEBusEvent;
        void* Reflection_SendEBusEvent;
        void* Reflection_RegisterEBusHandler;
        void* Reflection_UnregisterEBusHandler;
        void* Reflection_CreateInstance;
        void* Reflection_DestroyInstance;
```

Update the `static_assert` (lines 174-176):
```cpp
    static_assert(
        sizeof(NativeImports) == (1 + 47 + 20) * sizeof(void*),
        "NativeImports layout drifted from O3DE.Interop.NativeImports - see HostAbi.cs");
```

- [ ] **Step 4: Add the matching 20 fields to `HostAbi.cs`**

In `Assets/Scripts/O3DE.Core/Interop/HostAbi.cs`, change `HostAbi.Version`:
```csharp
        public const uint Version = 2;
```

Insert 20 new fields immediately after `public IntPtr Component_HasComponent;` (the last field in `NativeImports`), in the exact same order as Step 3:
```csharp

        // ============================================================
        // Reflection (added in HostAbiVersion 2)
        // ============================================================
        public IntPtr Reflection_GetClassNames;
        public IntPtr Reflection_GetMethodNames;
        public IntPtr Reflection_GetPropertyNames;
        public IntPtr Reflection_GetEBusNames;
        public IntPtr Reflection_GetEBusEventNames;
        public IntPtr Reflection_ClassExists;
        public IntPtr Reflection_MethodExists;
        public IntPtr Reflection_InvokeStaticMethod;
        public IntPtr Reflection_InvokeInstanceMethod;
        public IntPtr Reflection_InvokeGlobalMethod;
        public IntPtr Reflection_GetProperty;
        public IntPtr Reflection_SetProperty;
        public IntPtr Reflection_GetGlobalProperty;
        public IntPtr Reflection_SetGlobalProperty;
        public IntPtr Reflection_BroadcastEBusEvent;
        public IntPtr Reflection_SendEBusEvent;
        public IntPtr Reflection_RegisterEBusHandler;
        public IntPtr Reflection_UnregisterEBusHandler;
        public IntPtr Reflection_CreateInstance;
        public IntPtr Reflection_DestroyInstance;
```

- [ ] **Step 5: Run test to verify it passes**

Run: `python -m pytest Editor/Tests/test_host_abi_contract.py -v`
Expected: PASS (5 tests)

- [ ] **Step 6: Run the full Python unit suite for regressions**

Run: `python -m pytest Editor/Tests/ -q -m unit`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add Code/Source/Scripting/HostAbi.h Assets/Scripts/O3DE.Core/Interop/HostAbi.cs Editor/Tests/test_host_abi_contract.py
git commit -m "ABI v2: add the 20 ReflectionInternalCalls fields to NativeImports

NOT COMPILE-VERIFIED (C++ side) - no O3DE engine SDK in this environment."
```

---

### Task 2: C++ wiring — register the 20 functions into `NativeImports`

**Files:**
- Modify: `Code/Source/Scripting/ScriptBindings.cpp` (add `#include`, extend `MakeNativeImports()`)

**Interfaces:**
- Consumes: the 20 `NativeImports` fields from Task 1; the 20 existing free functions in the `GenericDispatcherInternalCalls` namespace (`Code/Source/Scripting/Reflection/GenericDispatcher.h:336-370`), unmodified.
- Produces: `ScriptBindings::MakeNativeImports()` now populates all 67 fields.

No test is written for this task — it's C++ with no build available in this environment (see Global Constraints). Self-review by reading, matching the existing pattern exactly, is the verification method.

- [ ] **Step 1: Add the include**

In `Code/Source/Scripting/ScriptBindings.cpp`, add near the top with the other includes:
```cpp
#include <Scripting/Reflection/GenericDispatcher.h>
```

- [ ] **Step 2: Extend `MakeNativeImports()`**

In `Code/Source/Scripting/ScriptBindings.cpp`, inside `ScriptBindings::MakeNativeImports()` (currently ends at line ~190 with `imports.Component_HasComponent = reinterpret_cast<void*>(&Component_HasComponent);` followed by `return imports;`), insert 20 new assignments right before `return imports;`. Note the naming split: 7 of these functions already carry the `Reflection_` prefix in their C++ symbol; the other 13 do not (the registered *string* name for Coral always has the prefix — see `GenericDispatcher.cpp:856-903` — but the C++ symbol doesn't for these 13):

```cpp
        imports.Reflection_GetClassNames = reinterpret_cast<void*>(&GenericDispatcherInternalCalls::Reflection_GetClassNames);
        imports.Reflection_GetMethodNames = reinterpret_cast<void*>(&GenericDispatcherInternalCalls::Reflection_GetMethodNames);
        imports.Reflection_GetPropertyNames = reinterpret_cast<void*>(&GenericDispatcherInternalCalls::Reflection_GetPropertyNames);
        imports.Reflection_GetEBusNames = reinterpret_cast<void*>(&GenericDispatcherInternalCalls::Reflection_GetEBusNames);
        imports.Reflection_GetEBusEventNames = reinterpret_cast<void*>(&GenericDispatcherInternalCalls::Reflection_GetEBusEventNames);
        imports.Reflection_ClassExists = reinterpret_cast<void*>(&GenericDispatcherInternalCalls::Reflection_ClassExists);
        imports.Reflection_MethodExists = reinterpret_cast<void*>(&GenericDispatcherInternalCalls::Reflection_MethodExists);

        imports.Reflection_InvokeStaticMethod = reinterpret_cast<void*>(&GenericDispatcherInternalCalls::InvokeStaticMethod);
        imports.Reflection_InvokeInstanceMethod = reinterpret_cast<void*>(&GenericDispatcherInternalCalls::InvokeInstanceMethod);
        imports.Reflection_InvokeGlobalMethod = reinterpret_cast<void*>(&GenericDispatcherInternalCalls::InvokeGlobalMethod);

        imports.Reflection_GetProperty = reinterpret_cast<void*>(&GenericDispatcherInternalCalls::GetProperty);
        imports.Reflection_SetProperty = reinterpret_cast<void*>(&GenericDispatcherInternalCalls::SetProperty);
        imports.Reflection_GetGlobalProperty = reinterpret_cast<void*>(&GenericDispatcherInternalCalls::GetGlobalProperty);
        imports.Reflection_SetGlobalProperty = reinterpret_cast<void*>(&GenericDispatcherInternalCalls::SetGlobalProperty);

        imports.Reflection_BroadcastEBusEvent = reinterpret_cast<void*>(&GenericDispatcherInternalCalls::BroadcastEBusEvent);
        imports.Reflection_SendEBusEvent = reinterpret_cast<void*>(&GenericDispatcherInternalCalls::SendEBusEvent);
        imports.Reflection_RegisterEBusHandler = reinterpret_cast<void*>(&GenericDispatcherInternalCalls::RegisterEBusHandler);
        imports.Reflection_UnregisterEBusHandler = reinterpret_cast<void*>(&GenericDispatcherInternalCalls::UnregisterEBusHandler);

        imports.Reflection_CreateInstance = reinterpret_cast<void*>(&GenericDispatcherInternalCalls::CreateInstance);
        imports.Reflection_DestroyInstance = reinterpret_cast<void*>(&GenericDispatcherInternalCalls::DestroyInstance);
```

- [ ] **Step 3: Self-review**

Re-read the edited `MakeNativeImports()` top to bottom. Confirm: (a) all 20 new lines appear before `return imports;`, in the same order as `HostAbi.h`'s field declarations from Task 1; (b) every `GenericDispatcherInternalCalls::X` name matches a real declaration in `GenericDispatcher.h:336-370` exactly (no typos — this compiles only if every symbol exists); (c) the existing 47-field assignments above are completely untouched.

- [ ] **Step 4: Commit**

```bash
git add Code/Source/Scripting/ScriptBindings.cpp
git commit -m "ABI v2: wire the 20 ReflectionInternalCalls functions into NativeImports

Same functions already registered via AddInternalCall for Coral
(GenericDispatcher.cpp:856-903) - this adds the second, NativeAOT-side
registration alongside it. No dispatch logic changed.

NOT COMPILE-VERIFIED - no O3DE engine SDK in this environment."
```

---

### Task 3: C# wiring — `NativeImportsWiring.cs`

**Files:**
- Modify: `Assets/Scripts/O3DE.Core/Interop/NativeImportsWiring.cs`

**Interfaces:**
- Consumes: the 20 `NativeImports` fields from Task 1; `ReflectionInternalCalls`' 20 existing delegate fields (`Assets/Scripts/O3DE.Core/Reflection/NativeReflection.cs:30-75`), unmodified.
- Produces: `NativeImportsWiring.Apply` now also wires `ReflectionInternalCalls.*`.

- [ ] **Step 1: Write the failing test**

Create `Editor/Tests/test_native_imports_wiring_reflection.py`, following the exact pattern of the existing `Editor/Tests/test_native_imports_wiring.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Regression guard: NativeImportsWiring.Apply must wire every
ReflectionInternalCalls field (the 20 added to the ABI in HostAbiVersion 2),
not just the original 47 O3DE.InternalCalls fields. See
docs/superpowers/specs/2026-08-25-nativeaot-reflection-dispatch-design.md.
"""

import re
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
WIRING_CS = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "Interop" / "NativeImportsWiring.cs"
REFLECTION_CS = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "Reflection" / "NativeReflection.cs"


def _reflection_fields():
    text = REFLECTION_CS.read_text(encoding="utf-8")
    return re.findall(r"internal\s+static\s+delegate\*\s+unmanaged<[^>]*>\s+(\w+)\s*;", text)


@pytest.mark.unit
def test_every_reflection_internal_calls_field_is_wired():
    fields = _reflection_fields()
    assert len(fields) == 20, f"expected 20 ReflectionInternalCalls fields, found {len(fields)}"

    wiring = WIRING_CS.read_text(encoding="utf-8")
    for field in fields:
        assert f"ReflectionInternalCalls.{field} = " in wiring, (
            f"NativeImportsWiring.Apply does not assign ReflectionInternalCalls.{field} - "
            "under NativeAOT this stays a null function pointer forever."
        )


@pytest.mark.unit
def test_reflection_wiring_reads_from_the_matching_imports_field():
    wiring = WIRING_CS.read_text(encoding="utf-8")
    for field in _reflection_fields():
        assert f"(void*)imports.{field};" in wiring, (
            f"ReflectionInternalCalls.{field} must be cast from imports.{field}, "
            "the matching NativeImports field - not some other field."
        )
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest Editor/Tests/test_native_imports_wiring_reflection.py -v`
Expected: FAIL — `NativeImportsWiring.cs` has no `ReflectionInternalCalls.*` assignments yet.

- [ ] **Step 3: Add the 20 cast-assignment lines**

In `Assets/Scripts/O3DE.Core/Interop/NativeImportsWiring.cs`, inside `Apply` (the method body ends with `InternalCalls.Component_HasComponent = (delegate* unmanaged<ulong, NativeString, Bool32>)(void*)imports.Component_HasComponent;` followed by the closing `}`), insert before that closing `}`. Exact delegate signatures, from `NativeReflection.cs:30-75`:

```csharp

            ReflectionInternalCalls.Reflection_GetClassNames = (delegate* unmanaged<NativeString>)(void*)imports.Reflection_GetClassNames;
            ReflectionInternalCalls.Reflection_GetMethodNames = (delegate* unmanaged<NativeString, NativeString>)(void*)imports.Reflection_GetMethodNames;
            ReflectionInternalCalls.Reflection_GetPropertyNames = (delegate* unmanaged<NativeString, NativeString>)(void*)imports.Reflection_GetPropertyNames;
            ReflectionInternalCalls.Reflection_GetEBusNames = (delegate* unmanaged<NativeString>)(void*)imports.Reflection_GetEBusNames;
            ReflectionInternalCalls.Reflection_GetEBusEventNames = (delegate* unmanaged<NativeString, NativeString>)(void*)imports.Reflection_GetEBusEventNames;
            ReflectionInternalCalls.Reflection_ClassExists = (delegate* unmanaged<NativeString, Bool32>)(void*)imports.Reflection_ClassExists;
            ReflectionInternalCalls.Reflection_MethodExists = (delegate* unmanaged<NativeString, NativeString, Bool32>)(void*)imports.Reflection_MethodExists;

            ReflectionInternalCalls.Reflection_InvokeStaticMethod = (delegate* unmanaged<NativeString, NativeString, NativeString, NativeString>)(void*)imports.Reflection_InvokeStaticMethod;
            ReflectionInternalCalls.Reflection_InvokeInstanceMethod = (delegate* unmanaged<NativeString, NativeString, long, NativeString, NativeString>)(void*)imports.Reflection_InvokeInstanceMethod;
            ReflectionInternalCalls.Reflection_InvokeGlobalMethod = (delegate* unmanaged<NativeString, NativeString, NativeString>)(void*)imports.Reflection_InvokeGlobalMethod;

            ReflectionInternalCalls.Reflection_GetProperty = (delegate* unmanaged<NativeString, NativeString, long, NativeString>)(void*)imports.Reflection_GetProperty;
            ReflectionInternalCalls.Reflection_SetProperty = (delegate* unmanaged<NativeString, NativeString, long, NativeString, Bool32>)(void*)imports.Reflection_SetProperty;
            ReflectionInternalCalls.Reflection_GetGlobalProperty = (delegate* unmanaged<NativeString, NativeString>)(void*)imports.Reflection_GetGlobalProperty;
            ReflectionInternalCalls.Reflection_SetGlobalProperty = (delegate* unmanaged<NativeString, NativeString, Bool32>)(void*)imports.Reflection_SetGlobalProperty;

            ReflectionInternalCalls.Reflection_BroadcastEBusEvent = (delegate* unmanaged<NativeString, NativeString, NativeString, NativeString>)(void*)imports.Reflection_BroadcastEBusEvent;
            ReflectionInternalCalls.Reflection_SendEBusEvent = (delegate* unmanaged<NativeString, NativeString, long, NativeString, NativeString>)(void*)imports.Reflection_SendEBusEvent;
            ReflectionInternalCalls.Reflection_RegisterEBusHandler = (delegate* unmanaged<NativeString, ulong, long, long>)(void*)imports.Reflection_RegisterEBusHandler;
            ReflectionInternalCalls.Reflection_UnregisterEBusHandler = (delegate* unmanaged<long, void>)(void*)imports.Reflection_UnregisterEBusHandler;

            ReflectionInternalCalls.Reflection_CreateInstance = (delegate* unmanaged<NativeString, NativeString, long>)(void*)imports.Reflection_CreateInstance;
            ReflectionInternalCalls.Reflection_DestroyInstance = (delegate* unmanaged<NativeString, long, void>)(void*)imports.Reflection_DestroyInstance;
```

`ReflectionInternalCalls` is `internal` and lives in `O3DE.Reflection` (`NativeReflection.cs:24`); `NativeImportsWiring` lives in `O3DE.Interop`, same assembly (`O3DE.Core`) — no `InternalsVisibleTo` needed, `internal` types are visible across namespaces within one assembly. Add `using O3DE.Reflection;` at the top of `NativeImportsWiring.cs` if it isn't already there.

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m pytest Editor/Tests/test_native_imports_wiring_reflection.py -v`
Expected: PASS (2 tests)

- [ ] **Step 5: Compile-verify the casts with a real `dotnet build`**

Run: `dotnet build Assets/Scripts/O3DE.Core/O3DE.Core.csproj -c Release -p:O3DESharpHostMode=NativeAot --nologo`
Expected: build succeeds (0 errors) — this is the real proof the 20 new delegate signatures actually match `ReflectionInternalCalls`' declared types (a mismatch is a compiler error, not a runtime one, since these are `delegate*` casts).

- [ ] **Step 6: Run the full Python unit suite for regressions**

Run: `python -m pytest Editor/Tests/ -q -m unit`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add Assets/Scripts/O3DE.Core/Interop/NativeImportsWiring.cs Editor/Tests/test_native_imports_wiring_reflection.py
git commit -m "ABI v2: wire ReflectionInternalCalls under NativeImportsWiring.Apply

Compiled: dotnet build -p:O3DESharpHostMode=NativeAot succeeds, proving
all 20 new delegate* casts match ReflectionInternalCalls' declared types."
```

---

### Task 4: Extend `StaticDispatchGenerator` beyond EBus

**Files:**
- Modify: `Code/Tools/SourceGenerators/StaticDispatchGenerator.cs`
- Test: `Code/Tools/SourceGenerators.Tests/` (new test file — see Step 1; the existing directory has no unit-test project today, see Step 0)

**Interfaces:**
- Consumes: `reflection_data.json`'s `"classes"`, `"global_methods"`, `"global_properties"` arrays (same JSON `ReflectionBindingGenerator` already parses for code generation — this task adds a second, independent consumer for validation).
- Produces: a new generated source file `O3DE.Reflection.StaticReflectionDispatch.g.cs`, alongside the existing `O3DE.Reflection.StaticEBusDispatch.g.cs`, with:
  ```csharp
  internal static class StaticReflectionDispatch
  {
      internal static bool ClassExists(string className);
      internal static bool MethodExists(string className, string methodName);
      internal static bool PropertyExists(string className, string propertyName);
      internal static bool GlobalMethodExists(string methodName);
      internal static bool GlobalPropertyExists(string propertyName);
  }
  ```
  Also adds one member to the existing `StaticEBusDispatch` (needed by Task 6 for `EBusHandlerRegistry.Register`'s guard, which only has a bus name, no event name): `internal static bool BusExists(string busName);`

**Step 0 note:** `Code/Tools/SourceGenerators.Tests/` today contains only a smoke-test project (`SourceGenerators.Smoke.csproj`, compiled samples) with no xUnit test methods — there is no existing test file to extend for `StaticDispatchGenerator` specifically. This task adds the first one. Check whether `Code/Tools/SourceGenerators.Tests/SourceGenerators.Smoke.csproj` already references `xunit`/`Microsoft.CodeAnalysis.CSharp` (needed to unit-test a Roslyn generator in isolation, without a full compile-through-smoke-project pass) — if not, this task's implementer should read `SourceGenerators.Smoke.csproj` first to determine whether to add a lightweight generator-invocation test there, or to test `StaticDispatchGenerator`'s parsing logic directly against a synthetic JSON string with a plain xUnit fact (bypassing the generator pipeline entirely — parsing is a static/testable method independent of `IIncrementalGenerator`, matching how the existing `ParseEvents` method is already structured as a separately-callable static method). Prefer the latter (direct call to the parsing method) — it's simpler and doesn't require standing up a Roslyn test harness.

- [ ] **Step 1: Write the failing test**

Create `Code/Tools/SourceGenerators.Tests/StaticDispatchGeneratorTests.cs` (a new xUnit test file — check whether `SourceGenerators.Smoke.csproj` needs an `xunit`/`Microsoft.NET.Test.Sdk` PackageReference added, following the same package versions already used in `Code/Tools/BindingGenerator.Tests/BindingGenerator.Tests.csproj` if a new test project is needed, or add to the existing one if it already has test infrastructure — inspect first):

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Code/Tools/SourceGenerators.Tests/ --filter StaticDispatchGeneratorTests`
Expected: FAIL to compile — `StaticDispatchGenerator.ParseReflectionData` doesn't exist yet (today's parsing entry point is `ParseEvents`, EBus-only, and there's no unified `ParsedReflectionData`-shaped return value with these five `*Exists` methods).

- [ ] **Step 3: Read the current file, then extend it**

Read `Code/Tools/SourceGenerators/StaticDispatchGenerator.cs` in full first — this task adds a parallel parsing path and a parallel emission block alongside the existing `ParseEvents`/`Emit` (EBus), following the exact same structural pattern (hand-rolled `JsonDocument` reads, graceful empty-list fallback on malformed JSON, corrupt input never fails the build). Specifically:

1. Add a new public static entry point `ParseReflectionData(string json)` returning a new small record/class (name it `ParsedReflectionData` or similar) exposing `ClassExists(string)`, `MethodExists(string, string)`, `PropertyExists(string, string)`, `GlobalMethodExists(string)`, `GlobalPropertyExists(string)`, and `BusExists(string)` — parsing `"classes"` (class name + each method name + each property name), `"global_methods"`, `"global_properties"`, and `"ebuses"` (just the bus names, for `BusExists` — reuse the existing EBus parsing internally rather than re-parsing) from the same `reflection_data.json` structure `ParseEvents` already reads. Same graceful-degradation behavior as `ParseEvents`: malformed/missing JSON yields empty sets, never an exception, never a build failure.
2. Wire this into the generator's `RegisterSourceOutput` pipeline (alongside the existing EBus `Emit` call) to also emit `O3DE.Reflection.StaticReflectionDispatch.g.cs`, containing an `internal static class StaticReflectionDispatch` with the same five `*Exists` methods, implemented the same way `StaticEBusDispatch.TryGetShape` is (nested `switch` statements over the parsed names — no runtime dictionary allocation, matching the existing style).
3. Add `internal static bool BusExists(string busName)` to the *existing* `StaticEBusDispatch` emission (a simple outer-switch-only lookup, no event-name/arity needed) — implement this using the SAME event-shape data `Emit` already has, not a second JSON parse.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Code/Tools/SourceGenerators.Tests/ --filter StaticDispatchGeneratorTests`
Expected: PASS (2 tests)

- [ ] **Step 5: Run the full SourceGenerators test suite for regressions**

Run: `dotnet test Code/Tools/SourceGenerators.Tests/`
Expected: PASS, no regressions to the existing EBus-only generation path

- [ ] **Step 6: Commit**

```bash
git add Code/Tools/SourceGenerators/StaticDispatchGenerator.cs Code/Tools/SourceGenerators.Tests/StaticDispatchGeneratorTests.cs
git commit -m "Extend StaticDispatchGenerator beyond EBus: classes, global methods/properties"
```

---

### Task 5: Widen `DynamicDispatchAnalyzer` (`O3DESHARP1001`) beyond EBus

**Files:**
- Modify: `Code/Tools/SourceGenerators/DynamicDispatchAnalyzer.cs`
- Test: `Code/Tools/SourceGenerators.Tests/` (new test file, or extend `DynamicDispatchSamples.cs` if that's the established fixture pattern for this analyzer — check first)

**Interfaces:**
- Consumes: nothing new from earlier tasks (independent of Task 4's generator work — the analyzer only checks constant-ness of C# expressions, it never queries the generated `StaticReflectionDispatch` table; that's the runtime guard's job in Task 6).
- Produces: `O3DESHARP1001` now fires for non-constant name arguments on 8 more `NativeReflection` methods, not just the 4 EBus ones.

Per-method argument positions to check (0-indexed, matching each method's actual parameter list — `NativeReflection.cs:227,242,267,286,311,345,358,549`):

| Method | Checked argument(s) |
|---|---|
| `InvokeStaticMethod(className, methodName, args)` | position 0 (`className`), position 1 (`methodName`) |
| `InvokeInstanceMethod(instance, methodName, args)` | position 1 (`methodName`) only — `className` comes from `instance.TypeName` at runtime, not a call-site literal |
| `InvokeGlobalMethod(methodName, args)` | position 0 (`methodName`) |
| `GetProperty<T>(instance, propertyName)` | position 1 (`propertyName`) |
| `SetProperty(instance, propertyName, value)` | position 1 (`propertyName`) |
| `GetGlobalProperty<T>(propertyName)` | position 0 (`propertyName`) |
| `SetGlobalProperty(propertyName, value)` | position 0 (`propertyName`) |
| `CreateInstance(className, constructorArgs)` | position 0 (`className`) |

`DestroyInstance(instance)` has no name-shaped argument at the call site — not covered by the analyzer (see Task 6 for its runtime-only guard).

- [ ] **Step 1: Write the failing test**

First, read `Code/Tools/SourceGenerators.Tests/DynamicDispatchSamples.cs` to see how the existing 4-method EBus coverage is exercised (this file is a compiled sample the smoke-test project builds through the real analyzer — confirm whether test assertions live in a separate file or whether this IS the fixture consumed by a test elsewhere; search the whole `SourceGenerators.Tests` directory for how `O3DESHARP1001` is currently asserted, e.g. via `CSharpAnalyzerVerifier`-style Roslyn testing helpers or via the smoke project's build output warnings). Add new sample methods to that file (or a sibling), one deliberately non-constant call per newly-covered method, e.g.:

```csharp
public void DynamicInvokeStaticMethod(string className, string methodName)
{
    NativeReflection.InvokeStaticMethod(className, methodName); // expect O3DESHARP1001 x2 (both args)
}

public void DynamicInvokeInstanceMethodName(NativeObject obj, string methodName)
{
    NativeReflection.InvokeInstanceMethod(obj, methodName); // expect O3DESHARP1001 (methodName only)
}

public void DynamicCreateInstance(string className)
{
    NativeReflection.CreateInstance(className); // expect O3DESHARP1001
}

public void ConstantCreateInstance()
{
    NativeReflection.CreateInstance("KnownType"); // expect NO diagnostic
}
```

Match whatever test-running mechanism the existing EBus samples use (extend the same test method/list that already asserts on `BroadcastEBusEvent`'s dynamic-name sample, adding assertions for these new cases using the identical mechanism).

- [ ] **Step 2: Run test to verify it fails**

Run whatever command the existing EBus analyzer test uses (found in Step 1) with the new method names.
Expected: FAIL — the analyzer doesn't check these 8 method names yet, so no `O3DESHARP1001` is reported for the new dynamic-call samples.

- [ ] **Step 3: Widen the analyzer**

In `Code/Tools/SourceGenerators/DynamicDispatchAnalyzer.cs`, replace the four hardcoded name constants (lines 58-61) and the fixed `args[0]`/`args[1]` positional check (lines 133-142) with a small lookup table mapping method name to the argument position(s) to check, per the table above. Keep the existing `NativeReflectionFullName` containing-type check, the self-referential-call skip (lines 122-125), and the `IsConstant`/`Report` helpers unchanged — only the "which method names are we watching, and which argument index/indices does each one check" part changes. The 4 existing EBus method names keep their exact current behavior (both busName and eventName checked) — this is a strict widening, not a rewrite.

- [ ] **Step 4: Run test to verify it passes**

Same command as Step 2.
Expected: PASS — all new samples produce the expected diagnostics (or lack thereof for the constant-name case), existing EBus samples still behave identically.

- [ ] **Step 5: Run the full SourceGenerators test suite for regressions**

Run: `dotnet test Code/Tools/SourceGenerators.Tests/`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add Code/Tools/SourceGenerators/DynamicDispatchAnalyzer.cs Code/Tools/SourceGenerators.Tests/DynamicDispatchSamples.cs
git commit -m "Widen O3DESHARP1001 to 8 more NativeReflection dispatch methods

DestroyInstance is intentionally not covered - it takes no name-shaped
call-site argument (instance.TypeName is derived from the NativeObject
at runtime, not literal at the call site)."
```

---

### Task 6: Runtime closed-world guards in `NativeReflection.cs` and `EBusHandlerRegistry.cs`

**Files:**
- Modify: `Assets/Scripts/O3DE.Core/Reflection/NativeReflection.cs`
- Modify: `Assets/Scripts/O3DE.Core/Reflection/EBusHandlerRegistry.cs`

**Interfaces:**
- Consumes: `StaticReflectionDispatch.ClassExists/MethodExists/PropertyExists/GlobalMethodExists/GlobalPropertyExists` and `StaticEBusDispatch.BusExists` from Task 4.
- Produces: `NotSupportedException` at the same point in the call, under NativeAOT only, for any of the 8 analyzer-covered methods called with a name not present in the reflection-data-derived table — mirroring the existing `EnsureStaticallyDispatchable`/EBus behavior exactly.

This task adds guards to 9 call sites: the 8 from Task 5's table, plus `EBusHandlerRegistry.Register`'s `busName` (checked via `StaticEBusDispatch.BusExists`, not `StaticReflectionDispatch` — it's a bus, not a class/method). `DestroyInstance` gets a lightweight body-level check too (using `instance.TypeName`, always concrete at runtime) for defense-in-depth, but is not analyzer-covered (see Task 5).

- [ ] **Step 1: Write the failing test**

The runtime guards are `[Conditional("O3DE_HOST_NATIVEAOT")]` and compiled out entirely in the editor/Coral build — they can only be exercised by compiling under `-p:O3DESharpHostMode=NativeAot`, the same constraint the existing `EnsureStaticallyDispatchable` tests already work within. Read `Assets/Scripts/O3DE.Core.Tests/NativeReflectionAotTests.cs` (mentioned in the existing `EnsureStaticallyDispatchable` doc comment) first, to find how the EXISTING EBus guard is tested (it must use some seam — the class doc comments reference "Editor/Tests/test_nativeaot_host.py" and similar patterns elsewhere in this repo for testing `[Conditional]`-gated code; find the actual mechanism). Add parallel test cases for the new guards following the identical pattern, e.g.:

```csharp
[Fact]
public void CreateInstance_UnknownClassName_ThrowsUnderNativeAot()
{
    // Mirrors the existing BroadcastEBusEvent_UnknownBus_ThrowsUnderNativeAot test shape.
    Assert.Throws<NotSupportedException>(() => NativeReflection.CreateInstance("NotInTheStaticTable"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run the project's existing NativeAOT-conditional test command (found in Step 1 — likely `dotnet test Assets/Scripts/O3DE.Core.Tests/ -c Release -p:O3DESharpHostMode=NativeAot` or similar; use exactly what the existing EBus guard test uses).
Expected: FAIL — `CreateInstance` doesn't guard yet, so no exception is thrown; the JSON call proceeds normally (and either succeeds against real BehaviorContext data in this test's fixture, or fails a different way not matching `NotSupportedException`).

- [ ] **Step 3: Add the guards**

In `Assets/Scripts/O3DE.Core/Reflection/NativeReflection.cs`, add two new guard helper methods near the existing `EnsureStaticallyDispatchable` (after line 418):

```csharp
        /// <summary>
        /// Guards the 8 non-EBus NativeReflection dispatch methods whose
        /// name argument(s) O3DESHARP1001 checks for compile-time
        /// constant-ness. Same shape as EnsureStaticallyDispatchable, one
        /// level less specific (no arity check - method/property arity
        /// isn't tracked the way EBus event arity is).
        /// </summary>
        [System.Diagnostics.Conditional("O3DE_HOST_NATIVEAOT")]
        private static void EnsureMethodIsStaticallyDispatchable(string siteName, string className, string methodName)
        {
#if O3DE_HOST_NATIVEAOT
            if (!StaticReflectionDispatch.MethodExists(className, methodName))
            {
                throw new NotSupportedException(
                    $"{siteName} '{className}.{methodName}' is not in this NativeAOT image's static " +
                    "dispatch table, built from reflection_data.json. Desktop NativeAOT supports " +
                    "closed-world dispatch only; the build reported O3DESHARP1001 for any call site " +
                    "whose class or method name is not a compile-time constant. Constant-fold the " +
                    "name, regenerate reflection_data.json if the method is new, or ship the Mono backend.");
            }
#endif
        }

        [System.Diagnostics.Conditional("O3DE_HOST_NATIVEAOT")]
        private static void EnsurePropertyIsStaticallyDispatchable(string siteName, string className, string propertyName)
        {
#if O3DE_HOST_NATIVEAOT
            if (!StaticReflectionDispatch.PropertyExists(className, propertyName))
            {
                throw new NotSupportedException(
                    $"{siteName} '{className}.{propertyName}' is not in this NativeAOT image's static " +
                    "dispatch table, built from reflection_data.json. Desktop NativeAOT supports " +
                    "closed-world dispatch only; the build reported O3DESHARP1001 for any call site " +
                    "whose class or property name is not a compile-time constant. Constant-fold the " +
                    "name, regenerate reflection_data.json if the property is new, or ship the Mono backend.");
            }
#endif
        }

        [System.Diagnostics.Conditional("O3DE_HOST_NATIVEAOT")]
        private static void EnsureGlobalMethodIsStaticallyDispatchable(string methodName)
        {
#if O3DE_HOST_NATIVEAOT
            if (!StaticReflectionDispatch.GlobalMethodExists(methodName))
            {
                throw new NotSupportedException(
                    $"InvokeGlobalMethod '{methodName}' is not in this NativeAOT image's static " +
                    "dispatch table, built from reflection_data.json. Constant-fold the name, " +
                    "regenerate reflection_data.json if new, or ship the Mono backend.");
            }
#endif
        }

        [System.Diagnostics.Conditional("O3DE_HOST_NATIVEAOT")]
        private static void EnsureGlobalPropertyIsStaticallyDispatchable(string propertyName)
        {
#if O3DE_HOST_NATIVEAOT
            if (!StaticReflectionDispatch.GlobalPropertyExists(propertyName))
            {
                throw new NotSupportedException(
                    $"Global property '{propertyName}' is not in this NativeAOT image's static " +
                    "dispatch table, built from reflection_data.json. Constant-fold the name, " +
                    "regenerate reflection_data.json if new, or ship the Mono backend.");
            }
#endif
        }

        [System.Diagnostics.Conditional("O3DE_HOST_NATIVEAOT")]
        private static void EnsureClassIsStaticallyDispatchable(string siteName, string className)
        {
#if O3DE_HOST_NATIVEAOT
            if (!StaticReflectionDispatch.ClassExists(className))
            {
                throw new NotSupportedException(
                    $"{siteName} '{className}' is not in this NativeAOT image's static dispatch " +
                    "table, built from reflection_data.json. Constant-fold the name, regenerate " +
                    "reflection_data.json if the class is new, or ship the Mono backend.");
            }
#endif
        }
```

Then call them at the top of each method body, before the existing logic (each guard call is one new line):

- `InvokeStaticMethod` (line 227): add `EnsureMethodIsStaticallyDispatchable("InvokeStaticMethod", className, methodName);` as the first line of the method body.
- `InvokeInstanceMethod` (line 242): add `EnsureMethodIsStaticallyDispatchable("InvokeInstanceMethod", instance.TypeName, methodName);` right after the existing null/invalid checks (so a null instance still throws `ArgumentNullException` first, matching current behavior) — uses `instance.TypeName` since there's no call-site `className` argument, but this is a real runtime value the guard can check.
- `InvokeGlobalMethod` (line 267): add `EnsureGlobalMethodIsStaticallyDispatchable(methodName);` as the first line.
- `GetProperty<T>` (line 286): add `EnsurePropertyIsStaticallyDispatchable("GetProperty", instance.TypeName, propertyName);` after the existing null/invalid checks.
- `SetProperty` (line 311): add `EnsurePropertyIsStaticallyDispatchable("SetProperty", instance.TypeName, propertyName);` after the existing null/invalid checks.
- `GetGlobalProperty<T>` (line 345): add `EnsureGlobalPropertyIsStaticallyDispatchable(propertyName);` as the first line.
- `SetGlobalProperty` (line 358): add `EnsureGlobalPropertyIsStaticallyDispatchable(propertyName);` as the first line.
- `CreateInstance` (line 549): add `EnsureClassIsStaticallyDispatchable("CreateInstance", className);` as the first line.
- `DestroyInstance` (line 565): add `EnsureClassIsStaticallyDispatchable("DestroyInstance", instance.TypeName);` inside the existing `if (instance != null && instance.IsValid)` block, before the unsafe call — this one is defense-in-depth only (see Task 5's note on why it's not analyzer-covered); in practice `instance.TypeName` will always be in the table if the instance was ever successfully created, so this should never actually fire, but keeps the guarantee airtight rather than assumed.

In `Assets/Scripts/O3DE.Core/Reflection/EBusHandlerRegistry.cs`, add a guard to `Register` (line 76), right after the existing null checks (after line 80, before the token-allocation logic at line 83):

```csharp
            NativeReflectionInternal.EnsureBusIsStaticallyDispatchable(busName);
```

This requires exposing a bus-only check from `NativeReflection` — add one more small guard method there:
```csharp
        [System.Diagnostics.Conditional("O3DE_HOST_NATIVEAOT")]
        internal static void EnsureBusIsStaticallyDispatchable(string busName)
        {
#if O3DE_HOST_NATIVEAOT
            if (!StaticEBusDispatch.BusExists(busName))
            {
                throw new NotSupportedException(
                    $"RegisterEBusHandler '{busName}' is not in this NativeAOT image's static " +
                    "dispatch table, built from reflection_data.json. Constant-fold the bus name, " +
                    "regenerate reflection_data.json if the bus is new, or ship the Mono backend.");
            }
#endif
        }
```
Make this `internal` (not `private`) on the `NativeReflection` class so `EBusHandlerRegistry` (same assembly, `O3DE.Reflection` namespace) can call it directly as `NativeReflection.EnsureBusIsStaticallyDispatchable(busName);` — adjust the call site in `Register` to that exact name (the `NativeReflectionInternal.` prefix above was illustrative; use `NativeReflection.EnsureBusIsStaticallyDispatchable(busName);`).

- [ ] **Step 4: Run test to verify it passes**

Same command as Step 2.
Expected: PASS

- [ ] **Step 5: Run the full O3DE.Core.Tests suite for regressions**

Run: `dotnet test Assets/Scripts/O3DE.Core.Tests/`
Expected: PASS — confirms the 9 new guard calls don't break any existing Coral-path behavior (they're `[Conditional]`-compiled-out entirely outside `O3DE_HOST_NATIVEAOT`, so this run shouldn't exercise them at all, but must confirm nothing else broke from the surrounding edits).

- [ ] **Step 6: Compile-verify under NativeAOT mode**

Run: `dotnet build Assets/Scripts/O3DE.Core/O3DE.Core.csproj -c Release -p:O3DESharpHostMode=NativeAot --nologo`
Expected: 0 errors — confirms the `#if O3DE_HOST_NATIVEAOT` guard bodies compile (referencing `StaticReflectionDispatch`/`StaticEBusDispatch.BusExists`, both from Task 4).

- [ ] **Step 7: Commit**

```bash
git add Assets/Scripts/O3DE.Core/Reflection/NativeReflection.cs Assets/Scripts/O3DE.Core/Reflection/EBusHandlerRegistry.cs Assets/Scripts/O3DE.Core.Tests/NativeReflectionAotTests.cs
git commit -m "Guard the 9 newly-wired dispatch call sites against non-constant names under NativeAOT"
```

---

### Task 7: Generated bindings get analyzer coverage, and final verification

**Files:**
- Modify: `Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Generation/ReflectionBindingGenerator.cs` (the `<ItemGroup>` in `EmitProjectFile`)
- Test: `Code/Tools/BindingGenerator.Tests/ReflectionGeneratorTests.cs` (new test)

**Interfaces:**
- Consumes: `O3DESharp.SourceGenerators.csproj` (unmodified project file — Tasks 4/5 only changed its source, not its own project structure).
- Produces: `O3DESharp.GeneratedBindings.csproj` now includes the SourceGenerators analyzer reference, matching `O3DE.Core.csproj`'s existing pattern exactly.

- [ ] **Step 1: Write the failing test**

Add to `Code/Tools/BindingGenerator.Tests/ReflectionGeneratorTests.cs`:

```csharp
    [Fact]
    public void GeneratedProject_ReferencesSourceGeneratorsAsAnalyzer()
    {
        var json = """
        {
            "classes": [
                { "name": "ClassA", "type_id": "{dddd1111-0000-0000-0000-000000000000}", "source_gem_name": "GemA", "methods": [], "properties": [] }
            ],
            "ebuses": [], "global_methods": [], "global_properties": []
        }
        """;
        var dir = GenerateFromJson(json);
        var csproj = File.ReadAllText(Path.Combine(dir, "O3DESharp.GeneratedBindings.csproj"));

        csproj.Should().Contain("O3DESharp.SourceGenerators.csproj");
        csproj.Should().Contain("OutputItemType=\"Analyzer\"");
        csproj.Should().Contain("ReferenceOutputAssembly=\"false\"");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Code/Tools/BindingGenerator.Tests/ --filter GeneratedProject_ReferencesSourceGeneratorsAsAnalyzer`
Expected: FAIL — the emitted csproj has no `O3DESharp.SourceGenerators.csproj` reference today.

- [ ] **Step 3: Add the analyzer reference**

In `Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Generation/ReflectionBindingGenerator.cs`, in `EmitProjectFile`, extend the existing `<ItemGroup>` block (the one containing the `O3DE.Core` `<Reference>`) to add a second item. Read the current exact surrounding lines first (they may have shifted slightly since the last read this session), then insert immediately after the `</Reference>` closing tag and before `</ItemGroup>`:

```csharp
            sb.AppendLine("    <ProjectReference Include=\"..\\..\\..\\Code\\Tools\\SourceGenerators\\O3DESharp.SourceGenerators.csproj\"");
            sb.AppendLine("                      OutputItemType=\"Analyzer\"");
            sb.AppendLine("                      ReferenceOutputAssembly=\"false\" />");
```

Use a relative path from the consolidated project's location (`<project>/Generated/CSharp/O3DESharp.GeneratedBindings.csproj`, per Task 2 of the earlier zero-config-gem-bindings plan) back to the gem's `Code/Tools/SourceGenerators/` directory — 2 levels up to the project root, then into `Code/Tools/SourceGenerators/`, matching the `..\..\..\` depth `O3DE.Core.csproj` uses only if `O3DE.Core.csproj` is at the same depth below the gem root; verify by checking `O3DE.Core.csproj`'s own location (`Assets/Scripts/O3DE.Core/O3DE.Core.csproj`, i.e. 3 levels below gem root) against `O3DESharp.GeneratedBindings.csproj`'s location (`<project>/Generated/CSharp/`, 2 levels below the O3DE *project* root, a different root entirely) — the correct relative path must be worked out from the actual gem root to `Code/Tools/SourceGenerators/`, not copy-pasted from `O3DE.Core.csproj`'s `..\..\..\` blindly. Compute it correctly for `O3DESharp.GeneratedBindings.csproj`'s actual emission location and verify with the build step in Step 5, not by assumption.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Code/Tools/BindingGenerator.Tests/ --filter GeneratedProject_ReferencesSourceGeneratorsAsAnalyzer`
Expected: PASS

- [ ] **Step 5: Real end-to-end build — zero `O3DESHARP1001` warnings on generated code**

Generate bindings for a real or synthetic multi-class `reflection_data.json` fixture (reuse or extend an existing fixture from `Code/Tools/BindingGenerator.Tests/`), then actually build the emitted `O3DESharp.GeneratedBindings.csproj`:
```bash
dotnet build <emitted-output-dir>/O3DESharp.GeneratedBindings.csproj -c Release --nologo -warnaserror:O3DESHARP1001
```
Expected: 0 errors, 0 `O3DESHARP1001` warnings — proves every call site the generator emits is still a literal by construction (per the spec's §3.4 assumption) even under the now-widened analyzer, AND proves the relative path from Step 3 actually resolves (an incorrect path would fail this build with "project file not found" or "analyzer assembly not found", not silently pass).

- [ ] **Step 6: Run the full test suites for final regression check**

Run:
```bash
dotnet test Code/Tools/BindingGenerator.Tests/
dotnet test Code/Tools/SourceGenerators.Tests/
dotnet test Assets/Scripts/O3DE.Core.Tests/
python -m pytest Editor/Tests/ -q -m unit
```
Expected: all PASS

- [ ] **Step 7: Commit**

```bash
git add Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Generation/ReflectionBindingGenerator.cs Code/Tools/BindingGenerator.Tests/ReflectionGeneratorTests.cs
git commit -m "Generated bindings reference SourceGenerators as an analyzer

Verified zero O3DESHARP1001 warnings on a real build - every call site
the reflection generator emits is a compile-time literal by construction,
now enforced rather than assumed."
```

---

## Plan Self-Review Notes

- **Spec coverage:** §3.1 (ABI extension) → Task 1. §3.2 (C++ wiring) → Task 2. §3.3 (C# wiring) → Task 3. §3.4 generator/analyzer/runtime-guard/generated-bindings-reference → Tasks 4, 5, 6, 7 respectively. §4 (testing) → a test in every task, plus real `dotnet build` compile-verification in Tasks 3, 6, 7 matching the spec's stated evidence bar. §5 (out of scope: introspection guarding, single-DLL packaging, native lookup logic, console/mobile) → none of Tasks 1-7 touch these, consistent.
- **Correction made during planning, not caught in the spec's own self-review**: the spec said "12 name-taking dispatch functions" needing guards; working through exact call-site argument shapes (Task 5/6) found only 9 have a call-site-checkable name argument (`InvokeInstanceMethod`/`GetProperty`/`SetProperty` only expose `methodName`/`propertyName` at the call site — `className` comes from `NativeObject.TypeName` at runtime, not a literal argument), plus `DestroyInstance` gets a body-only defense-in-depth check with no analyzer coverage, and `RegisterEBusHandler`'s guard lives in `EBusHandlerRegistry.cs`, not `NativeReflection.cs`. This doesn't change what gets protected (every checkable name still gets checked, at the point where it's actually knowable), just where each check physically lives.
- **Type/name consistency check**: `StaticReflectionDispatch`'s five method names (`ClassExists`, `MethodExists`, `PropertyExists`, `GlobalMethodExists`, `GlobalPropertyExists`) are defined once in Task 4 and consumed identically by Task 6's five new guard helpers. `StaticEBusDispatch.BusExists` (Task 4) is consumed by Task 6's `EnsureBusIsStaticallyDispatchable`. The 20 `NativeImports` field names (Task 1) are consumed identically in Task 2 (C++) and Task 3 (C#) — same names, same order, both derived directly from `ReflectionInternalCalls`' existing 20 field names (`NativeReflection.cs:30-75`), not invented.
