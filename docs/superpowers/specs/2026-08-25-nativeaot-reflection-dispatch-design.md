# NativeAOT Reflection Dispatch — Design

**Date:** 2026-08-25
**Status:** Design accepted; ready for implementation planning.
**Author:** Mikael K. Aboagye (WD Studios Corp.), via collaborative brainstorm 2026-08-25.
**Parent context:** follows directly from `docs/superpowers/specs/2026-08-25-zero-config-gem-bindings-design.md`, whose §7 scoped "NativeAOT compatibility for generated bindings" as sub-project 2 and "single-DLL packaging" as sub-project 3. This spec covers sub-project 2, redefined (see §1) after deeper investigation showed the actual gap is larger than that framing suggested. Single-DLL packaging remains a separate, sequential follow-on spec.

---

## 1. Goal, and why the original framing was wrong

The zero-config gem-bindings spec framed sub-project 2 as "extend `StaticDispatchGenerator` to cover method/property call sites, the way it already covers EBus." Investigation for this spec found that framing understated the problem: **`NativeReflection`'s entire dispatch surface — every method generated bindings call, and every EBus call O3DE.Core's own code makes — is completely unwired under NativeAOT today, not just unverified.**

`ReflectionInternalCalls` (`Assets/Scripts/O3DE.Core/Reflection/NativeReflection.cs:24`) is a 19-function table of `delegate* unmanaged<...>` fields, populated *exclusively* by Coral's `AddInternalCall` at runtime (confirmed by its own doc comment: "Coral populates these function pointer fields at runtime via `AddInternalCall()`"). The frozen ABI built for the M3/M4 NativeAOT milestone (`Code/Source/Scripting/HostAbi.h:51-52`) documents explicitly: *"O3DE.Reflection.ReflectionInternalCalls is registered separately by GenericDispatcher and is deliberately NOT part of v1."* Under NativeAOT — no Coral, no `AddInternalCall` — every one of these 19 function pointers is null. `EnsureStaticallyDispatchable` (the existing `O3DESHARP1001`/`StaticEBusDispatch` machinery) only diagnoses whether a call's bus/event name is a compile-time constant; even a call that passes that check still reaches `ReflectionInternalCalls.Reflection_BroadcastEBusEvent`, a null pointer, and crashes. **This was a deliberate, documented scope cut in the earlier work, not a bug** — but it means the earlier NativeAOT milestone shipped compile-time diagnostics for a dispatch path that cannot actually run.

**Goal, restated:** make `NativeReflection`'s full dispatch surface (19 functions) actually function under NativeAOT, and extend the existing closed-world diagnostic/guard machinery from EBus-only to all 12 name-taking dispatch functions, so that once dispatch is technically possible, the NativeAOT desktop product guarantee — every dispatch call site is compile-time provable — still holds. Generated bindings (all gems, via `O3DESharp.GeneratedBindings.dll`) become NativeAOT-usable as a consequence, since every call site the generator emits is already a compile-time literal by construction (verified: `ReflectionBindingGenerator.cs`'s `EmitClass`/`EmitEBus`/`EmitGlobals` never emit a computed name).

## 2. Locked decisions

| Decision | Choice |
|---|---|
| Scope | Wire all 19 `ReflectionInternalCalls` functions into the frozen ABI (not just the 13 generated bindings/EBus need) — introspection functions (`GetClassNames` etc.) included for API parity between hosts. |
| C++ approach | No new C++ dispatch logic. The existing `GenericDispatcher.cpp` functions are already ABI-compatible with direct `NativeImports` assignment (same pattern the working 47-function table already uses for e.g. `Log_Info`) — just register the same function pointers a second time. |
| Closed-world guard | Extend to all 12 name-taking dispatch functions (11 dispatch + `RegisterEBusHandler`; `UnregisterEBusHandler` takes only a token, needs none), not just EBus. Wiring makes dynamic dispatch newly *possible*; the guard is what keeps it *disallowed* under NativeAOT by policy. |
| Relationship to generated bindings | Additive, not bespoke. Generated bindings need zero code changes to become NativeAOT-usable once this lands — every call site they emit is already a literal. The only touch point is making `O3DESharp.GeneratedBindings.csproj` reference the source-generator project so the (now-widened) diagnostic actually runs against it. |
| Relationship to single-DLL packaging | Sequential, not bundled. This spec's output is a prerequisite (no point merging bindings into a NativeAOT image whose calls crash), but the merge itself is a separate spec. |

## 3. Architecture

### 3.1 ABI extension: `HostAbiVersion` 1 → 2

`NativeImports` (`Code/Source/Scripting/HostAbi.h`, mirrored in `Assets/Scripts/O3DE.Core/Interop/HostAbi.cs`) gains 19 new fields, one per `ReflectionInternalCalls` function, in the same declaration order on both sides (that ordering *is* the ABI — see the existing header comment). `HostAbiVersion` bumps to `2`; both sides' version constant must move together or the golden contract test fails.

New fields (grouped to match `ReflectionInternalCalls`' existing grouping):
- Reflection queries: `Reflection_GetClassNames`, `Reflection_GetMethodNames`, `Reflection_GetPropertyNames`, `Reflection_GetEBusNames`, `Reflection_GetEBusEventNames`, `Reflection_ClassExists`, `Reflection_MethodExists`
- Method invocation: `Reflection_InvokeStaticMethod`, `Reflection_InvokeInstanceMethod`, `Reflection_InvokeGlobalMethod`
- Property access: `Reflection_GetProperty`, `Reflection_SetProperty`, `Reflection_GetGlobalProperty`, `Reflection_SetGlobalProperty`
- EBus: `Reflection_BroadcastEBusEvent`, `Reflection_SendEBusEvent`, `Reflection_RegisterEBusHandler`, `Reflection_UnregisterEBusHandler`
- Object lifecycle: `Reflection_CreateInstance`, `Reflection_DestroyInstance`

### 3.2 C++ wiring

In `ScriptBindings.cpp`'s `MakeNativeImports`-equivalent block (the one already populating `imports.Log_Info` etc.), add 19 more assignments of the exact same shape:
```cpp
imports.Reflection_InvokeInstanceMethod = reinterpret_cast<void*>(&GenericDispatcher::InvokeInstanceMethod);
```
using `GenericDispatcher`'s existing, unmodified implementations (`Code/Source/Scripting/Reflection/GenericDispatcher.cpp`). The `AddInternalCall("O3DE.Reflection.ReflectionInternalCalls", "Reflection_InvokeInstanceMethod", ...)` registration these functions already have for Coral is untouched — same function, registered twice, exactly like the working 47-function table's own functions already are.

### 3.3 C# wiring

`NativeImportsWiring.cs` (compiled only under `O3DE_HOST_NATIVEAOT`) gains 19 more cast-assignment lines, one per field, mirroring its existing 47:
```csharp
ReflectionInternalCalls.Reflection_InvokeInstanceMethod = (delegate* unmanaged<NativeString, NativeString, long, NativeString, NativeString>)(void*)imports.Reflection_InvokeInstanceMethod;
```
`ReflectionInternalCalls` stays `internal` (`Assets/Scripts/O3DE.Core/Reflection/NativeReflection.cs`), so `NativeImportsWiring` needs `InternalsVisibleTo` access — already granted (`NativeReflection.cs:15`: `[assembly: InternalsVisibleTo("O3DE.Core.Tests")]` exists for tests; confirm/add the access `NativeImportsWiring` itself needs, since both live in `O3DE.Core` — same assembly, no `InternalsVisibleTo` needed at all, just direct access).

### 3.4 Closed-world guard extension

**Generator side** (`StaticDispatchGenerator.cs`): currently parses only `reflection_data.json`'s `"ebuses"` array into a `(busName, eventName) -> (arity, isBroadcast)` table (`StaticEBusDispatch`). Extend it to also parse `"classes"`, `"global_methods"`, and `"global_properties"` (the same JSON `ReflectionBindingGenerator` already reads, for generation rather than validation) into a sibling table — same source generator, wider output. Shape:
```csharp
internal static class StaticReflectionDispatch
{
    internal static bool ClassExists(string className);
    internal static bool MethodExists(string className, string methodName);      // covers both static and instance
    internal static bool PropertyExists(string className, string propertyName);
    internal static bool GlobalMethodExists(string methodName);
    internal static bool GlobalPropertyExists(string propertyName);
}
```
(`StaticEBusDispatch.TryGetShape` stays separate — it needs arity, which the others don't.)

**Analyzer side** (`DynamicDispatchAnalyzer.cs`): currently hardcodes exactly 4 method names it flags (`BroadcastEBusEvent`, `SendEBusEvent`, `BroadcastResultEBusEvent`, `SendResultEBusEvent`). Widen the set to the other 8 name-taking non-EBus functions: `InvokeStaticMethod`, `InvokeInstanceMethod`, `InvokeGlobalMethod`, `GetProperty`, `SetProperty`, `GetGlobalProperty`, `SetGlobalProperty`, `CreateInstance`, plus `RegisterEBusHandler`. Same `O3DESHARP1001` diagnostic ID and message shape, parameterized by which argument(s) are the checked name(s) for each method (one name for most; `className` + `methodName`/`propertyName` pairs for the class-member ones).

**Runtime guard side** (`NativeReflection.cs`): add an `EnsureStaticallyDispatchable`-equivalent `[Conditional("O3DE_HOST_NATIVEAOT")]` check at the top of each of the 12 name-taking public methods, checking against the matching `StaticReflectionDispatch`/`StaticEBusDispatch` table entry, throwing the same style of `NotSupportedException` naming the site and pointing at `O3DESHARP1001` on failure. `DestroyInstance`'s guard checks `className` (needed for the lookup even though it's releasing, not creating).

**Generated bindings wiring**: `ReflectionBindingGenerator.EmitProjectFile` (`Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Generation/ReflectionBindingGenerator.cs`) gains a `<ProjectReference Include="...\O3DESharp.SourceGenerators.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />` item, matching the pattern `O3DE.Core.csproj`/`Examples.csproj` already use. Since every generated call site is a literal by construction, this should produce zero `O3DESHARP1001` warnings on a clean build — the reference exists as a regression guard (if a future generator change ever emitted a computed name, this is what would catch it), not because generated code needs fixing today.

## 4. Testing

Same evidence bar as the M3/M4 work, given no live O3DE engine build is available in this environment:
- Golden ABI-layout test (`Editor/Tests/test_host_abi_contract.py`) extended to assert the 19 new fields, in order, on both the C++ and C# sides, and that `HostAbiVersion` matches across both.
- A real `dotnet build` of `O3DE.Core.csproj` under `-p:O3DESharpHostMode=NativeAot` compiling every new cast in `NativeImportsWiring.cs` — proves the delegate signatures actually match, the same verification method the original 47-field wiring used.
- `StaticDispatchGenerator`/`DynamicDispatchAnalyzer` unit tests with synthetic `reflection_data.json` fixtures: one asserting the new lookup tables produce correct `Exists`/`TryGetShape`-style answers, one asserting `O3DESHARP1001` now fires for a non-constant call to each of the 8 newly-covered method names (mirroring the existing EBus test shape), one asserting it stays silent for a constant-name call.
- A test generating bindings for a small synthetic `reflection_data.json` and running the (now `O3DESHARP1001`-eligible) `O3DESharp.GeneratedBindings.csproj` build, asserting zero `O3DESHARP1001` warnings — proves generated code stays clean under the widened analyzer.
- C++ side (`ScriptBindings.cpp`'s new `imports.Reflection_X = ...` lines) is not independently compile-verified in this environment (same limitation as the rest of the C++ ABI work this session) — flagged the same way prior C++-touching work in this repo already is ("NOT COMPILE-VERIFIED" in the commit message), reviewed by careful reading against the established pattern instead.

## 5. Out of scope

- **Single-DLL packaging** (O3DE.Core + GeneratedBindings + user scripts as one NativeAOT-published assembly) — separate, sequential follow-on spec. This spec's output is a prerequisite for it being useful, not part of it.
- **The actual native BehaviorContext lookup logic** — unchanged. This is pure wiring (make existing functions reachable from a second host) plus guards (keep dispatch provable), not a rewrite of how reflection dispatch works.
- **Console/mobile Mono backend** — different milestone (M5 per the earlier design docs), not touched here.
- **Introspection functions' closed-world guarding** — the 7 pure-discovery functions (`GetClassNames`, `GetMethodNames`, `GetPropertyNames`, `GetEBusNames`, `GetEBusEventNames`, `ClassExists`, `MethodExists`) are wired (§2 "all 19") but not guarded (§3.4 only covers 12 dispatch/handler functions). Querying "what's available" can't violate closed-world the way *calling* something unprovable can — the answer is always whatever's actually in the closed set, since nothing outside it could exist in a NativeAOT image.
