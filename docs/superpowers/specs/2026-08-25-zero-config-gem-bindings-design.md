# Zero-Config Gem Bindings (Coral) — Design

**Date:** 2026-08-25
**Status:** Design accepted; ready for implementation planning.
**Author:** Mikael K. Aboagye (WD Studios Corp.), via collaborative brainstorm 2026-08-25.
**Scope:** Sub-project 1 of 4 in the "zero-legwork C# for gems" effort. This spec covers only
the default Coral (JIT) path. NativeAOT compatibility for generated bindings, single-DLL
packaging (O3DE.Core + gem bindings + user scripts), and a title deploy/export tool are
separate follow-on specs — see §7.

---

## 1. Goal

Today, using a gem's reflected API from C# requires manual legwork even though the reflection
backend already auto-discovers every gem with zero configuration: a developer must run the
binding generator by hand, `cd` into its output and run `dotnet build` themselves, and add a
`<Reference>` to the resulting DLL in their game script project. None of that is automated.

This spec closes that gap: enabling a gem should be the *only* thing a developer does. Its C#
bindings should exist, be built, be deployed, and be usable from game scripts with no manual
steps — generation, compilation, and referencing all happen automatically.

## 2. Current state (verified against the code, 2026-08-25)

- The **reflection backend** (`--source reflection`, the CLI default) already discovers every
  gem present in `reflection_data.json` with no `binding_config.json` gate — see
  `ReflectionBindingGenerator.Generate()`. Only the clang backend requires per-gem
  `"enabled": true` entries.
- `${gem_name}.GenerateBindings` (`Code/CMakeLists.txt`) already runs the generator
  automatically as part of a normal build (default `O3DESHARP_AUTO_GENERATE_BINDINGS=ON`), using
  the reflection backend's default path resolution (`<project>/Generated/reflection_data.json`,
  via `ReflectionDataPathResolver`). But it **only generates** `.g.cs` files — nothing ever
  compiles the emitted per-gem `.csproj` into a DLL.
- `reflection_data.json` itself is written by `O3DESharpSystemComponent::AutoExportReflectionData()`,
  called once from `Code/Source/Clients/O3DESharpSystemComponent.cpp` right after
  `BehaviorContextReflector::ReflectFromContext` runs. This component is a **Clients**-module
  component, not Editor-only — it runs in every launch context, including a shipping Release
  launcher.
- Today's generator emits **one `.csproj` per gem** (`PhysicsGem.csproj`, `AudioGem.csproj`, ...),
  each independently buildable, each producing its own DLL.
- The user game-script `.csproj` template references `O3DE.Core` via a fixed `<Reference
  HintPath="...">`. There is no equivalent for gem bindings today — a developer adds one by hand
  per gem, per the Generated Bindings Guide.

## 3. Locked decisions

| Decision | Choice |
|---|---|
| Trigger | Two entry points, one shared build step (§4): an Editor-driven auto-loop, and an explicit CMake/solution target. |
| Gating | Auto-build only fires in development contexts (Editor, and Debug/Profile game launches) — never in a shipping Release launcher, which must not shell out to `dotnet build` at startup. |
| Gem scope | All gems in `reflection_data.json` by default. A denylist (not an allowlist) opts specific gems out. |
| Output shape | **One consolidated project**, `O3DESharp.GeneratedBindings.csproj`, covering every non-denied gem — not one project per gem. |
| Referencing | Implicit — the user script `.csproj` template gets an MSBuild glob reference over the deployed bindings directory. No manual `<Reference>` ever. |
| Backend | Reflection backend only. The clang backend stays manual/opt-in for the "type not yet reflected" escape hatch it already serves — it is not part of the zero-config story. |

## 4. Architecture

```
                          ┌─────────────────────────────────────────┐
  Editor/Debug/Profile    │ O3DESharpSystemComponent::Activate()     │
  launch                  │   -> ReflectFromContext                  │
                          │   -> AutoExportReflectionData()          │
                          │        writes <project>/Generated/       │
                          │        reflection_data.json               │
                          └──────────────────┬────────────────────────┘
                                             │ (dev contexts only)
                                             ▼
                          ┌─────────────────────────────────────────┐
                          │  SyncGeneratedBindings (shared step)     │
                          │  1. generate  --source reflection        │
                          │     -> Assets/Scripts/GeneratedBindings/ │
                          │        O3DESharp.GeneratedBindings.csproj│
                          │        + one .g.cs tree per gem           │
                          │  2. dotnet build -c Release                │
                          │  3. stage into bin/GeneratedBindings/     │
                          └──────────────────┬────────────────────────┘
                                             │
                       ┌─────────────────────┴─────────────────────┐
                       ▼                                           ▼
        ly_add_target_files deploys to               O3DESharpHotReloadBus /
        Bin/Scripts/ (alongside Coral.Managed.dll,    ReloadUserAssemblies -
        O3DE.Core.dll)                                same pipeline Phase 16a
                                                       already uses for user DLLs
```

The **same** generate → build → stage sequence is invoked from two places, never duplicated:

- **CMake/solution target** — `${gem_name}.BuildGeneratedBindings` (extends the existing
  `${gem_name}.GenerateBindings`, which today stops after generation) runs it once against
  whatever `reflection_data.json` is already on disk. Covers CI, packaged/headless builds, and
  first-time setup after an Editor session has produced the file.
- **Editor-driven hook** — after `AutoExportReflectionData()` writes a *new or changed*
  `reflection_data.json`, the same sequence runs on a background thread (the `QThread` pattern
  already used for Build/Build All in the C# Project Manager, so it can't freeze the Editor UI —
  see the v1.2.0 fix this reuses), then deploys and fires the existing hot-reload path so open
  game scripts pick up new gem types without an Editor restart.

Both entry points call into one script/tool rather than each re-implementing "generate, then
build, then stage" — most naturally an extension of the existing `O3DESharp.BindingGenerator`
CLI (e.g. a `--build` flag on `generate`, or a thin wrapper), so there is exactly one place that
knows how to go from "gem enabled" to "DLL deployed."

## 5. Components

1. **Generator change**: `ReflectionBindingGenerator` gains a denylist read from a new top-level
   `reflectionBackendExcludedGems: string[]` key in `binding_config.json`, kept deliberately
   separate from the clang backend's existing `gems` map (that map's `"enabled"` flag is an
   *allowlist* for clang; reusing it for the reflection backend's *denylist* would give the same
   field opposite default semantics depending on which backend reads it — a new key avoids that
   confusion). The generator then emits **one** `O3DESharp.GeneratedBindings.csproj` covering
   every non-denied gem's `.g.cs` output, instead of one project per gem. `InternalCalls.g.cs`
   name collisions across gems need a per-gem namespace/class qualifier if not already present —
   verify during implementation.
2. **CMake**: add a new `${gem_name}.BuildGeneratedBindings` target that depends on the existing
   `${gem_name}.GenerateBindings` (which keeps doing exactly what it does today — generate only)
   and `dotnet build`s the consolidated project, staging its output into `bin/GeneratedBindings/`.
   This mirrors the existing `StageCoral`/`StageO3DECore` pattern (`BYPRODUCTS` included, per the
   Ninja lesson from issue #3) and deploys via `ly_add_target_files` into `Bin/Scripts/`.
3. **C++/Editor hook**: a new call site after `AutoExportReflectionData()` succeeds, gated to
   development contexts, that shells out to the same build step on a background thread and then
   notifies the existing hot-reload bus. Needs a "don't do this in a shipping Release launcher"
   guard — likely the same build-config check `CoralHostConfig::enableHotReload` already uses.
4. **User script template**: replace/augment the fixed `O3DE.Core` `<Reference>` with an MSBuild
   `<ItemGroup>` glob over `bin/GeneratedBindings/*.dll` (in practice one DLL, but glob keeps the
   template correct if that ever changes), so no per-project edits are needed as gems come and go.

## 6. Error handling

- **Generation failure** (e.g. a gem's reflected surface trips a generator bug): logged clearly
  with the offending gem named, does not take down the Editor, and does not block the *previous*
  successfully-built `GeneratedBindings.dll` from continuing to be used — same "degrade, don't
  crash" posture the rest of the binding generator already follows (malformed config, zero-binding
  results, etc.).
- **Build failure** (`dotnet build` errors on the consolidated project): same — logged, previous
  DLL stays in place, Editor stays usable. This is a background operation, not a blocking one.
- **First run on a fresh checkout**: before any Editor session has ever run, there is no
  `reflection_data.json` and therefore nothing to generate. The CMake target degrades gracefully
  (matches the existing "binding generator source not found" / "O3DE.Core.dll not found" graceful
  warnings elsewhere in `Code/CMakeLists.txt`) rather than failing configure.

## 7. Out of scope (future specs)

- **NativeAOT compatibility for generated bindings** (sub-project 2). Reflection-generated wrapper
  classes call `NativeReflection.InvokeInstanceMethod`/`GetProperty`/etc. with compile-time-constant
  method names — in principle as closed-world-friendly as the EBus calls `StaticDispatchGenerator`
  already handles, but that generator only covers EBus dispatch today. Extending it to method/property
  call sites is real, separate work.
- **Single-DLL packaging** across O3DE.Core + GeneratedBindings + user scripts (sub-project 3).
  This spec deliberately stops at "one DLL for all gem bindings," not "one DLL for everything" —
  merging further is coupled to the NativeAOT work above (NativeAOT publish already needs to
  operate on one project).
- **Title deploy/export tool** (sub-project 4), which needs sub-project 3's output artifact to
  exist first.
- **The clang backend's zero-config story**. It stays a manual, explicitly-invoked escape hatch —
  see Generated Bindings Guide.

## 8. Testing

- Unit: the denylist is honored (`ReflectionBindingGenerator` includes/excludes the right gems),
  the consolidated project's emitted `.csproj` references what it should, no per-gem name
  collisions in the merged `InternalCalls`/wrapper namespace.
- CMake/build: a `test_stage_targets_ninja_safe.py`-style regression pinning `BYPRODUCTS` on the
  new/extended staging target, following the same pattern as `StageCoral`/`StageO3DECore`.
- Integration (maintainer-verified, needs a real O3DE Editor run — same boundary every AOT/Coral
  integration test in this repo already has): enable a gem, launch the Editor, confirm
  `GeneratedBindings.dll` appears in `Bin/Scripts/` with no manual step, and that a script
  referencing a type from that gem compiles and hot-reloads.
