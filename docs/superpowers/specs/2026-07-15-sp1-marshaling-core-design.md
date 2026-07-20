# SP-1 — Marshaling Core — Design

**Date:** 2026-07-15
**Status:** Design accepted; ready for implementation planning.
**Author:** Mikael K. Aboagye (WD Studios Corp.), via collaborative brainstorm 2026-07-15.
**Parent program:** `docs/superpowers/specs/2026-07-15-first-class-csharp-program-design.md` (SP-1)
**Starting point:** the rescued "1B" work on branch `feat/1b-native-trampoline-rescue` (`8a00627`),
quarantined under `Rescued/1b-native-trampoline/` — 2,758 lines across 9 files.

---

## 1. Goal

Build one correct, extensible dispatch layer between Coral and `AZ::BehaviorContext` that removes
the per-call reflection overhead in **both** directions, and that doubles as the **static-dispatch
foundation dual-mode AOT requires**.

This is the v1.3 BehaviorContext refactor, pulled forward because SP-3 (native C# components),
SP-8/SP-9 (C# editor tooling), and the AOT milestone all sit on it.

## 2. Locked decisions

| Decision | Choice |
|---|---|
| Scope | **Dispatch performance + AOT foundation only.** The deterministic sim-lane framing in the rescued comments is explicitly dropped. |
| Coral access | **Add a proper public function-pointer API to the `WatchDogStudios/Coral` fork.** Delete the independent hostfxr re-initialization workaround. |
| `native_symbol` source | **Accept the libclang coupling.** Generate the manifest and trampolines on Windows; **commit the artifacts** so Linux/CI consume them without libclang. |
| Relationship to `GenericDispatcher` | **Additive fast paths.** `GenericDispatcher` / `BehaviorMethod::Call` remain the correctness baseline and universal fallback. Not a rewrite. |
| Ordering | **Half A (native→managed thunks) first**, then Half B (managed→native trampolines). |

## 3. Two verified constraints this design is built around

Both were established by the rescued work and independently confirmed while reading it. They are
facts about O3DE and Coral, not implementation choices, and the design is shaped by them.

### 3.1 Coral's call directions are asymmetric

The **managed→native** direction is already a zero-per-call pinned thunk: `AddInternalCall` plus
`delegate* unmanaged<...>` fields (see `ScriptBindings.cpp` / `InternalCalls.cs`).

The **native→managed** direction has exactly one path — `Coral::ManagedObject::InvokeMethod<T>(
AZStd::string_view methodName, ...)` — which performs a managed-side **string lookup plus .NET
reflection dispatch on every call**. No Coral API hands native code a raw function pointer into a
managed method.

The remedy is a standard .NET hosting primitive, not a Coral hack:
`hostfxr_get_runtime_delegate(ctx, hdt_load_assembly_and_get_function_pointer, ...)` yields a
`load_assembly_and_get_function_pointer_fn` which, given an assembly-qualified type name and an
`[UnmanagedCallersOnly]` static method, returns a raw native-callable pointer with **zero managed
lookup per call thereafter**. Coral's own `HostInstance` already uses exactly this to bootstrap
`Coral.Managed`.

### 3.2 `BehaviorContext` does not retain the native C++ symbol

`AZ::BehaviorContext` / `BehaviorMethodImpl` stores **no** native symbol. The functor backing a
reflected method captures a raw function pointer inside a type-erased `AZStd::function`; nothing in
the runtime object exposes that pointer or the qualified C++ name that produced it. Only the
reflected *script* name survives.

Therefore `native_symbol` **can never** be recovered by walking a live `BehaviorContext`. It requires
a second, independent, offline pass: a libclang parse of each gem's reflection `.cpp` extracting the
`&C::Method` expression from `->Method("name", &C::Method)`, joined to the runtime data on
`(className, reflected script-name)`.

This is the sole reason Half B has an offline component at all.

## 4. Architecture

`GenericDispatcher` and `BehaviorMethod::Call` are **unchanged** and remain the correctness baseline.
SP-1 adds two independent, opt-in fast paths, each of which degrades to that baseline.

```
                    ┌──────────────────────────────┐
   native  ──────►  │  Half A: pinned thunks       │  ──────►  managed
                    │  (Coral fork fn-ptr API)     │
                    └──────────────────────────────┘
                    ┌──────────────────────────────┐
   managed ──────►  │  Half B: generated           │  ──────►  native
                    │  trampolines + BindingRegistry│
                    └──────────────┬───────────────┘
                                   │ unresolved / rejected
                                   ▼
                    ┌──────────────────────────────┐
                    │  BehaviorMethod::Call        │  (baseline, always correct)
                    └──────────────────────────────┘
```

A gap in the fast path is therefore a **performance** regression, never a correctness bug. That
property is load-bearing given `native_symbol` recovery is a heuristic join across two independent
passes.

## 5. Half A — native→managed pinned thunks

**Lands first.** Smaller, self-contained, and unblocks SP-3.

- **Coral fork (`WatchDogStudios/Coral`), cross-repo:** add a public function-pointer API — either
  expose the existing private `HostInstance::LoadCoralManagedFunctionPtr`, or add a cleaner
  `HostInstance::GetFunctionPointer(assemblyQualifiedTypeName, methodName)`. Prefer the latter: a
  named, documented API rather than widening an internal.
- **Gem:** rebuild `CoralNativeThunkHost` on that API and **delete the independent
  `hostfxr_initialize_for_runtime_config` re-initialization entirely.** That workaround existed only
  because an earlier task forbade touching anything outside `Gems/O3DESharp`; that constraint no
  longer applies. Removing it also removes a subtle long-term reliance on
  `Success_HostAlreadyInitialized` semantics for a second init against a running CLR.
- **Managed:** `[UnmanagedCallersOnly]` static entry points in `O3DE.Core`, following the existing
  `InternalCalls` blittable-argument convention.
- **Unblocks:** SP-3 component lifecycle (`Activate` / `Deactivate` / `OnTick`) at pinned-thunk cost.

## 6. Half B — managed→native trampolines

Replaces `BehaviorMethod::Call`'s `virtual → AZStd::function → per-arg RTTI ConvertTo` path with a
direct generated trampoline, for the conservatively-selected subset that can be bound safely.

### 6.1 The manifest is built by two passes and joined offline

1. **Runtime pass — C++, `NativeBindingManifest`.** Owns the runtime-observable half: owning class
   type id / size / align, reflected name, per-argument `ArgStorageClass`
   (`Value` / `Pointer` / `Reference` / `ConstReference` / `Unknown`), return type, arity.
2. **Offline pass — `ReflectionCallSiteParser.cs`.** libclang over each gem's reflection `.cpp`,
   recovering `&C::Method` per §3.2.
3. **Join — `NativeBindingGenerator.cs`,** keyed on `(className, reflected script-name)`, emitting
   the trampolines and the `BindingRegistry` table (stable binding-id string → function pointer,
   e.g. `"Vector3::GetLength"`).

This split is deliberate and preserved from the rescued design: `BehaviorContextReflector` answers
"what got reflected"; this layer answers "of that, what can be bound natively, and to what symbol."
Existing consumers (`GenericDispatcher`, `ReflectionDataExporter`) stay untouched.

### 6.2 The v1 classifier is deliberately conservative

A method is **not** bound when any of these hold (the existing `NonBindableReason` enum):
`Overloaded`, `ReflectedViaLambda`, `OnDemandTemplateType`, `EBusAddressedById`,
`UnresolvedNativeSymbol`, `UnsupportedArgStorage`, `NoNativeSideCounterpart`.

Every one of these falls back to `BehaviorMethod::Call`. Widening the bound set is a later,
evidence-driven change — not a v1 goal.

### 6.3 Artifacts are generated on Windows and committed

The libclang backend is win-x64-only and is not the default binding backend. Rather than block on
making it cross-platform, the manifest and generated trampolines are produced by a **Windows-only
build step and committed as artifacts**, so Linux and CI builds consume them with no libclang
dependency. This is what makes §6.1's offline pass compatible with the Linux parity just shipped.

## 7. Safety: load-time manifest validation is mandatory

**This is the highest-risk element of the design and is not optional.**

A committed artifact can go stale against an engine whose reflection changed. A stale entry means
invoking a function pointer with a mismatched signature — **memory-unsafe**, not merely incorrect.

Therefore, at load, every manifest entry is cross-checked against the **live `BehaviorContext`**:
owning class type id, arity, per-argument type ids and storage classes, and return type. Any entry
that does not match exactly is **rejected into the fallback path and logged loudly**. A rejected
entry is a normal, survivable outcome; a silently trusted stale entry is not.

The runtime-observable half of the manifest (§6.1 pass 1) exists precisely to make this check
possible — it is not redundant with the offline half.

## 8. Verification strategy

| Concern | Method |
|---|---|
| Classifier correctness | Unit tests per `NonBindableReason`, on fixtures — each reason maps 1:1 to a check. |
| Offline join correctness | Unit tests over reflection-`.cpp` fixtures, including deliberate mis-join bait (same script name on two classes; same C++ name reflected under a different script name). |
| **Trampoline correctness** | **Differential testing** — invoke the same method through the trampoline and through `BehaviorMethod::Call`, assert identical results, across the whole bound surface. Generator unit tests alone cannot establish this. |
| Stale-manifest safety | Tests that deliberately corrupt a manifest entry (wrong arity, wrong type id) and assert it is rejected into fallback, not called. |
| **Silent degradation** | **Binding-coverage telemetry** — record and assert the bound fraction in CI, so a regression that quietly drops everything to the slow path fails the build instead of being noticed months later. |
| Half A correctness | Round-trip tests through pinned thunks; verify no per-call managed allocation/lookup remains. |

## 9. Risks

1. **The `(className, script-name)` join is heuristic.** Mis-joins are possible in principle.
   Mitigated by §7 load-time validation (a mis-join almost always produces a signature mismatch) and
   by §8's mis-join bait tests.
2. **Manifest staleness.** Mitigated by §7 — the mandatory mitigation, not a nice-to-have.
3. **Dual-path drift.** Two dispatch paths can diverge in behaviour over time. Mitigated by §8's
   differential testing being run over the full bound surface, not a sample.
4. **Cross-repo dependency.** Half A needs the Coral fork API landed; the gem side cannot be
   completed until it is. Sequence them together.
5. **Coverage collapse.** An engine reflection change could silently drop the bound fraction toward
   zero. Mitigated by §8's coverage telemetry.

## 10. Non-goals

- **Determinism / deterministic sim lane.** Explicitly dropped. The rescued code's references to
  `ISimSystem` / `SimSystemBase` / tiering-lock are historical context; that second half was never
  written and is not in scope.
- **Replacing `GenericDispatcher`.** It remains the baseline. A future consolidation is a separate
  decision.
- **Widening the bound set beyond the conservative v1 classifier.**
- **Making the libclang pass cross-platform** (deferred; §6.3 sidesteps it).
- **SP-3 component work, AOT publishing, consoles/Mono.**

## 11. Open questions

1. **The missing "1B spec".** The rescued sources cite a *"1B spec §3 point 3 / §6.3"* containing the
   original verified analysis. It does not exist in either repo. If recoverable, it should be added —
   it may contain manifest-schema decisions this design would otherwise rediscover. Not a blocker.
2. **Exact Coral API shape.** Whether to expose `LoadCoralManagedFunctionPtr` or add
   `GetFunctionPointer` is settled at implementation time against the fork's actual `HostInstance`
   surface; this design prefers the latter but does not mandate the signature.
3. **How much of the rescued code survives verbatim.** The rescued files are unfinished and have
   never been compiled. The implementation plan must budget for the possibility that parts are
   rewritten rather than integrated; the rescue preserved *intent and analysis*, which is the durable
   value, not necessarily the exact lines.
