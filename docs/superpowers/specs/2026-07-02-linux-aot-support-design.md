# O3DESharp — Linux Support + Dual-Mode AOT — Design

**Date:** 2026-07-02
**Status:** Design accepted; ready for implementation planning.
**Author:** Mikael K. Aboagye (WD Studios Corp.), via collaborative brainstorm 2026-07-02.
**Target branch:** `development` → `main` (per-milestone releases; see Milestones).

---

## 1. Motivation & goals

Two maintainer-requested capabilities, designed as one combined spec because they
share portability plumbing and a hosting-abstraction seam:

1. **Full Linux parity** — Linux developers can author, build, hot-reload, and run
   C# scripts inside the O3DE Editor exactly like Windows, and Linux game launchers
   can run them.
2. **Dual-mode AOT** — the O3DE Editor keeps the current CoreCLR + Coral +
   reflection + hot-reload workflow, while **shipping** builds get an ahead-of-time
   compiled static path: **NativeAOT on desktop** (Windows/Linux/Mac) and
   **Mono-AOT + interpreter on consoles/mobile** (JIT-banned platforms).

The unifying idea (Approach A, selected): a **single frozen C ABI seam** between the
C++ engine and the managed scripting layer, so one C# codebase and one C++
integration serve three execution models. Only the *implementation behind* the ABI
and the *mechanism that wires it up* differ per runtime; the ABI shape itself never
changes.

Non-goal restated up front: this spec does **not** redesign the BehaviorContext /
EBus dispatch internals. That control surface is reserved for the maintainer's
separate **v1.3 refactor**. This spec defines only the *contract* the shipping AOT
build needs that refactor to satisfy (Section 5).

## 2. Locked decisions

Captured from the brainstorm so the plan doesn't relitigate them:

| Decision | Choice |
|----------|--------|
| Linux scope | **Full parity** — editor + runtime, authoring included. |
| Coral platform reach | **Desktop-only by design** — Coral hosts desktop CoreCLR (Windows/Linux/Mac); it is *not* the vehicle for consoles/mobile. Linux desktop hosting is therefore expected to work via the same cross-platform hostfxr path (verify in M1). |
| AOT meaning | **Dual-mode** — hot-reload editor + static shipping build. |
| AOT targets | **Both** — desktop NativeAOT **and** console/mobile Mono-AOT. |
| Spec scope | **One combined spec** for Linux + full dual-mode AOT, including a proposed static-dispatch approach coordinated with (feeding into) the v1.3 BehaviorContext refactor. |
| C++/C# seam architecture | **Approach A** — Frozen C ABI + build-mode codegen (see Section 4). |

## 3. Current hosting model (verified)

O3DESharp does not host .NET itself; it delegates all runtime bootstrapping to
**Coral**, a WatchDogStudios-owned fork of StudioCherno/Coral, pulled at configure
time via CMake `FetchContent` from `github.com/WatchDogStudios/Coral.git@main`
(`Code/CMakeLists.txt:48-49`) — not vendored, not pinned; `Coral.Native` is fetched
and is **not in this repo**.

The model is **CoreCLR-hosted, framework-dependent**: Coral boots an installed .NET 9
shared runtime through `nethost → hostfxr → hostpolicy`, driven by
`Coral.Managed.runtimeconfig.json` (`bin/Coral/Coral.Managed.runtimeconfig.json`; tfm
`net9.0`, `Microsoft.NETCore.App 9.0.0`, `rollForward LatestMinor`; no RID, no
self-contained, no AOT). The C++↔C# bridge already uses `delegate* unmanaged<...>`
static function-pointer fields populated by name via Coral's
`AddInternalCall`/`UploadInternalCalls` (`ScriptBindings.cpp:46-119`,
`InternalCalls.cs:21-108`). User scripts load as separately-compiled DLLs into
collectible `AssemblyLoadContext`s for hot-reload
(`CoralHostManager.cpp:245-249, 385-392, 622`).

**Load-bearing fact:** NativeAOT and CoreCLR-hosting are mutually exclusive *per build
artifact* — a NativeAOT image has no JIT and no hostfxr consumer, so nothing for
`nethost → hostfxr` to attach to. Nothing prevents shipping *two artifacts from one
codebase* (editor = CoreCLR/Coral; shipping = NativeAOT/Mono). That two-artifact split
is the whole design.

## 4. Architecture — the frozen ABI seam (Approach A)

### 4.1 The seam: two Coral-free structs

The only C++/C# boundary is two structs of C function pointers, exchanged once at init:

- **`NativeImports`** — pointers C++ exposes to managed (today's internal calls:
  `Entity_GetChildren`, transform ops, etc.). Already `delegate* unmanaged` in
  `InternalCalls.cs`; barely changed.
- **`ManagedExports`** — pointers managed exposes to C++:
  - `CreateInstance(typeId) → handle`
  - `InvokeLifecycle(handle, which)` — the hot `OnCreate`/`OnTick`/`OnDestroy` path;
    **typed pointers, kept fast**.
  - `DispatchEBusEvent(handle, eventId, argBlob) → resultBlob` — the dynamic path;
    uses today's blob/JSON envelope.
  - `DestroyInstance(handle)`
  - `HotReloadSwap(...)` — **editor-only**, gated behind `SupportsHotReload()`.

The ABI struct carries a **version field**; host, editor build, and shipping build must
agree on it (enforced by a golden contract test, Section 7).

Critically: **`DispatchEBusEvent`'s signature is identical in every build.** Only its
*body* differs — reflection in the editor, source-generated static `switch` (plus an
interpreter tail on Mono) in shipping. That is what keeps the seam frozen while the two
ends diverge.

### 4.2 The C++ host abstraction

```
class IManagedHost {
    virtual CoralHostStatus Initialize(const NativeImports&) = 0;
    virtual const ManagedExports* GetExports() const = 0;
    virtual bool SupportsHotReload() const = 0;
    virtual void Shutdown() = 0;
};
```

Three implementations differ *only* in how the two structs are exchanged:

- **`CoralHost`** (editor) — `CoralHostManager` refactored to sit behind the interface.
  `ManagedExports` are `[UnmanagedCallersOnly]` statics resolved by name via Coral;
  `NativeImports` uploaded via `AddInternalCall`. Hot-reload re-resolves exports per ALC
  swap. **Behavior-preserving wrapping refactor, not a rewrite.**
- **`NativeAotHost`** (desktop shipping) — managed side is a NativeAOT native lib; C++
  `dlopen`/`LoadLibrary`s it and resolves the exported symbols directly (no Coral, no
  hostfxr). `NativeImports` handed in via an exported `Initialize`. **Inverted
  direction:** C++ imports exports rather than uploading calls.
- **`MonoAotHost`** (console/mobile shipping) — mono embedding resolves the same
  exports; the interpreter covers the dynamic tail.

### 4.3 The source generator as single source of truth

Extend the existing `Code/Tools/SourceGenerators` (`EBusHandlerGenerator` already
monomorphizes `UnmarshalArg<ConcreteType>`, so the pattern is proven). It emits the
`[UnmanagedCallersOnly]` export thunks for **all** builds, and per build config either:

- **Editor config** — registers the exports with Coral + wires `NativeImports`
  (today's mechanism, re-expressed through the ABI). No runtime behavior change.
- **Shipping config** — compiles the same thunks statically **plus a source-generated
  static dispatch table** replacing reflection, consuming the build-time manifest
  (Section 5).

### 4.4 Managed-side AOT readiness

Independent of the dispatch work, required before any NativeAOT publish:

- `<IsAotCompatible>` / trim analyzer enabled on `O3DE.Core.csproj`.
- `JsonSerializerContext` (source-generated) for the reflection-based `JsonSerializer`
  calls in `NativeReflection.cs`.
- `[DynamicallyAccessedMembers]` / ILLink roots for the live `ExposedProperty` reflection.
- Guard or remove the dormant `HotReloadManager.cs` reflection (`Assembly.GetType` +
  `Activator.CreateInstance`) so it is excluded from shipping images.

## 5. The v1.3 coordination contract & interpreter boundary

The **receive side** (C++→C# handler callbacks) is already AOT-shaped via
`EBusHandlerGenerator`. The hard part is the **send/invoke-by-name** surface reserved
for v1.3: `NativeReflection.InvokeStaticMethod` / `InvokeInstanceMethod` /
`InvokeGlobalMethod` / `GetProperty` / `SetProperty` / `BroadcastEBusEvent` /
`SendEBusEvent`, which today does open-world runtime reflection over BehaviorContext.

### 5.1 What this spec designs vs. what v1.3 designs

- **This spec designs:** the AOT-side generator that consumes the build-time manifest
  to emit static dispatch; the interpreter boundary; the closed-world diagnostic; and
  the *shape of the contract* below.
- **v1.3 designs:** the refactored dispatch internals, funneled through the single
  choke point the contract requires. This spec does **not** dictate those internals.

### 5.2 The contract — three requirements on the v1.3 dispatch surface

1. **A build-time manifest of the reflected surface.** Already exists —
   `reflection_data.json` (the dump the reflection binding backend produces). The AOT
   generator consumes that same file to emit a static dispatch table (name → concrete
   typed call). No new artifact needed.
2. **Dispatch funneled through one generatable choke point.** v1.3's refactor must route
   every dynamic invoke through a single interface / partial-method seam (rather than
   reflection scattered across call sites), so the generator can supply a static
   implementation of that one seam for shipping builds. This is the primary structural
   ask of v1.3.
3. **Compile-time-constant call sites resolve statically; dynamic ones fall to the
   boundary** (Section 5.3). Constant bus/event/method names → direct typed call.
   Runtime-computed names → interpreter (Mono) or hard diagnostic (NativeAOT desktop).

### 5.3 The interpreter boundary — an honest capability gradient

```
editor (CoreCLR, JIT)        ⊇  console/mobile (Mono-AOT)   ⊇  desktop (NativeAOT)
full dynamic dispatch           static hot path +               closed-world only;
everything works                interpreter tail for            unresolvable dynamic
                                runtime-dynamic names           name = build warning +
                                                                runtime hard error
                                                                naming the exact site
```

NativeAOT desktop is intentionally the most restrictive, and the one restriction is
**loudly diagnosable at build time** — never a silent degrade. A desktop shipping game
needing runtime-dynamic BehaviorContext calls either constant-folds the names or ships
the Mono backend. The spec documents this so it is a *designed* constraint.

## 6. Milestones

Ordered so each is independently shippable and the v1.3-coupled work is quarantined last.
Everything through **M4 plus M2 is independent of v1.3.**

### M0 — Linux configure unblock *(trivial)*
- Add empty `Code/Platform/Linux/o3desharp_shared_files.cmake` (matches the
  Windows/Mac stub); its absence is a fatal `include()` error at
  `Code/CMakeLists.txt:480-482`.
- Fix support-matrix trait drift: `Platform/{Mac,Android,iOS}/PAL_*.cmake:9` set
  `PAL_TRAIT_O3DESHARP_SUPPORTED TRUE`, contradicting `gem.json` (Linux+Windows) and
  the docs. Make traits honest for the true target set.
- **Exit:** a clean Linux CMake configure.

### M1 — Linux full parity (editor + runtime)
- CMake: build `O3DE.Core` + generated gem csprojs via `${DOTNET_EXECUTABLE}` /
  `ExternalProject` on non-MSVC (the mechanism already used for the binding generator,
  `Code/CMakeLists.txt:290-301`), replacing the `if(MSVC)`-gated
  `include_external_msproject` (`:840`).
- Python tooling: resolve `dotnet` via `shutil.which` + `<build>/.dotnet` / `DOTNET_ROOT`
  fallback (6 sites); shared `open_in_default_app()` replacing `os.startfile`
  (`csharp_editor_tools.py:559,1024`); host-detected `launch.json` template
  (`csharp_project_manager.py:307,319-320`); fix stale ".NET 8.0" strings; Rider Linux
  Toolbox path.
- Cross-repo: verify/patch the Coral fork's Linux desktop CLR hosting
  (`libcoreclr.so` / hostfxr). Preserve `Coral::String` UTF-16/UTF-8 routing
  (`O3DESharpSystemComponent.cpp:274-288`) in any new marshaling.
- **Exit:** author → build → hot-reload → run a C# script in the Editor on Linux.

### M2 — Self-contained deployment *(cheap, Coral-compatible)*
- Self-contained publish bundling a private CoreCLR per desktop RID (win-x64,
  linux-x64, osx-x64/arm64) so players need no .NET install. Small Coral change to
  resolve the bundled/apphost runtime instead of a machine-wide install.
- **Untrimmed first** — the reflection/JSON dispatch fights the trimmer; trimming is a
  later, separate risk.
- No dispatch changes; rides on today's model.
- **Exit:** a self-contained desktop launcher runs C# scripts on a machine with no .NET
  installed.

### M3 — Frozen ABI seam + build-mode split *(architectural core, behavior-preserving)*
- Define the ABI (`NativeImports`, `ManagedExports`, version field).
- Introduce `IManagedHost`; refactor `CoralHostManager` into `CoralHost` behind it
  (editor path re-expressed through the ABI, behavior-preserving).
- Extend the source generator to emit the ABI adapter for the editor build.
- Land the Section 4.4 managed AOT-readiness groundwork.
- **Exit:** editor runs unchanged through the new `CoralHost` + ABI; golden ABI contract
  test passes.

### M4 — Desktop NativeAOT shipping build
- `NativeAotHost` (dlopen/LoadLibrary + resolve `[UnmanagedCallersOnly]` exports — the
  inverted direction).
- Generator emits static dispatch tables + exports for the shipping config, consuming
  `reflection_data.json`.
- Publish `O3DE.Core` + a sample game's scripts as NativeAOT (win-x64 + linux-x64).
- Closed-world diagnostic: any dynamic path the generator couldn't see is a build
  warning + runtime hard error naming the site.
- **Exit:** a sample game's scripts run in a desktop NativeAOT launcher via
  `NativeAotHost`; the closed-world diagnostic fires on a deliberately-dynamic call.

### M5 — Console/mobile Mono-AOT + interpreter
- `MonoAotHost` via mono embedding; AOT + interpreter tail covers the open-world
  dispatch that monomorphization can't.
- Structured so each vendor/NDA console port is a thin backend behind the same ABI.
- Validated on a **desktop Mono-AOT proxy** (real console SDKs unavailable in this
  environment); per-console SDK ports are out of scope to build/test here.
- **Exit:** the parity suite passes against `MonoAotHost` on the desktop Mono-AOT proxy,
  including a runtime-dynamic dispatch case handled by the interpreter tail.

### M-1.3 (coordination, gated on the v1.3 refactor) — Full static BehaviorContext dispatch
- The open-world static registry replacing the reserved `NativeReflection.Invoke*` /
  `Broadcast/SendEBusEvent` reflection, funneled through v1.3's single choke point.
- This spec supplies the contract (Section 5); the internals land in / with v1.3.

**Dependency summary:** M0 → M1 foundational; M2 rides after M1 independently; M3 is the
pivot for all AOT; M4 needs M3; M5 needs M3 + M4 and coordinates with v1.3; full
open-world static dispatch is gated on v1.3.

## 7. Testing strategy across three runtimes

- **Editor / managed unit tests:** existing `O3DE.Core.Tests` (xUnit),
  `BindingGenerator.Tests` (xUnit), Python editor tests — add a **Linux CI job** running
  all of them.
- **Golden ABI contract test:** asserts `NativeImports`/`ManagedExports` struct layout +
  version, shared by all three hosts, so ABI drift fails the build.
- **Headless C++ harness:** drives each `IManagedHost` implementation through one script
  lifecycle (create → lifecycle → dispatch → destroy).
- **Cross-runtime parity suite:** the same behavioral cases run against all three hosts,
  asserting identical results on the closed-world subset.
- **NativeAOT desktop:** publish a sample gem's scripts as NativeAOT in CI (win-x64 +
  linux-x64); assert the closed-world diagnostic fires on a deliberately-dynamic call.
- **Mono console:** validated on a **desktop Mono-AOT proxy** (no console SDKs here);
  the per-console port is structured-for, not built/tested in this environment.

## 8. Risks & mitigations

1. **(Biggest) Open-world static dispatch may be intractable without an interpreter.**
   Mitigated by the capability gradient (Section 5.3): Mono interpreter tail for the
   open-world case; NativeAOT desktop closed-world + hard diagnostic. We explicitly do
   **not** promise full dynamic dispatch on NativeAOT desktop.
2. **ABI churn** between editor/shipping/host → versioning + golden contract test.
3. **Coral Linux hosting unknown (cross-repo).** M1 is gated on it; the maintainer owns
   the fork. Open question (Section 10).
4. **v1.3 coupling.** Full open-world static dispatch is gated on v1.3; M0–M4 + M2 are
   independent, so most value ships without waiting.
5. **Trimming fragility.** Self-contained ships untrimmed first; trimming is deferred.
6. **Console NDA toolchains.** M5 backend is structured-for; per-console ports are out
   of scope to build here.

## 9. Non-goals / out of scope / deferred

- **No redesign of BehaviorContext / EBus dispatch internals** — reserved for v1.3. This
  spec defines only the AOT contract (Section 5).
- **No trimming in M2** — untrimmed self-contained first.
- **No hot-reload on shipping AOT builds** — editor-only by design.
- **No per-console SDK port** — M5 is structured-for, not built.
- **No FPC controller / gameplay work.**
- **Clang binding-backend Linux support deferred** — it is win-x64-only and hardcodes
  Windows parse defines (`BindingConfig.cs:41-44`), but the **default reflection
  backend** is unaffected, so default binding generation is not blocked on Linux. Noted
  follow-up only.

## 10. Open questions

1. **Coral Linux desktop hosting status.** "Desktop-only by design" implies Linux desktop
   is supported in principle, but tested status of `WatchDogStudios/Coral@main` on Linux
   is unconfirmed from this repo. Resolved during M1 by the maintainer (fork owner).
2. **Exact shape of the v1.3 choke point.** The contract (Section 5.2) requires a single
   generatable dispatch seam; its precise interface is settled when the v1.3 refactor is
   designed, against this contract.
