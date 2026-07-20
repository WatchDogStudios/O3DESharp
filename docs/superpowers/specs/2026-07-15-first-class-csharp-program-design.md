# O3DESharp — C# as a First-Class Language — Program Design

**Date:** 2026-07-15
**Status:** Program-level design accepted; sub-projects to be specced individually.
**Author:** Mikael K. Aboagye (WD Studios Corp.), via collaborative brainstorm 2026-07-15.
**Scope:** This is a **program**, not a single implementation spec. It decomposes the work into
sequenced sub-projects; each gets its own `spec → plan → implementation` cycle.

---

## 1. Vision

Make C# a first-class language in the WD Studios O3DE fork, meaning:

- **(a) Primary gameplay authoring language** — C# classes are *real* components: they appear in
  the Add-Component menu individually, render natively in the inspector, round-trip through
  prefabs under their own type, and hot reload reliably.
- **(b) Editor tooling written in C#** — the ~7,000 lines of Python editor tooling
  (`Editor/Scripts/`) is replaced by C#, so gameplay and tooling share one language and API surface.

## 2. Locked decisions

| Decision | Choice |
|---|---|
| Product boundary | **The fork is the product.** The engine-integrated build is the reference, fully-first-class experience. |
| End-user story | The gem **ships instructions + patches** so an end user can bring their own engine fork to the same first-class support. |
| Gem on unpatched engines | Must still **work, degrading gracefully** — never hard-fail. |
| User assembly delivery | **Asset Processor owns it** (SP-6). M2 self-contained deploy is scoped strictly to the *private .NET runtime bundle* and must not grow into assembly delivery. |
| Codebase strategy | **One gem, capability-gated.** Do *not* fork the gem's code per track. |
| Engine patch style | **Strictly additive** (new methods / new files / new registry entries) — never edits to existing logic. |

## 3. Current state (verified 2026-07-15)

### 3.1 The urgent problem: two diverging copies of the gem

`F:\engine\Gems\O3DESharp` is a **vendored copy that has drifted badly** from `F:\O3DESharp`:

- Version `1.0.0` in the engine tree vs `1.3.0` in the gem repo.
- **109 commits behind**, **40 files dirty**, roughly **+6,489 lines** of local edits concentrated in
  `CoralHostManager.cpp`, `GenericDispatcher.cpp`, `BehaviorContextMarshaling.*`, the four Python
  editor scripts, and the whole `Code/Tools/BindingGenerator/` tree — **none of which exist in the
  gem repo's history.**
- Registered as a `160000` gitlink with **no `.gitmodules`**, so a recursive clone of the engine
  produces an **empty directory**.

Nothing else in this program is safe to build until this is reconciled. It is SP-0.

### 3.2 C# today is a guest wearing one native component

- The engine core contains **zero** .NET/Coral/hostfxr/CoreCLR code. `F:\engine\engine.json:65`
  lists `Gems/O3DESharp` as a discoverable external subdirectory, but no project, registry, or
  template enables it.
- **Every** user C# class shares the single `CSharpScriptComponent` TypeId. Prefabs therefore store
  a *class-name string*, not a component type.
- **Every** exposed property is stringly-typed: the inspector surface is a
  `PropertyHandler<unordered_map<string,string>, QWidget>`
  (`Code/Source/Tools/Components/CSharpExposedPropertiesHandler.h:43-44`).
- **No** asset-pipeline participation at all (a repo-wide grep for
  `AssetBuilderSDK|AssetHandler|Data::Asset<` in `Code/` returns nothing). Hot reload is a Qt
  file-watcher (`Code/Source/Tools/CSharpAssemblyWatcher.cpp:40,74`) that tears down and rebuilds
  the entire ALC (`CoralHostManager.cpp:350`).
- Goal (b) is currently **inverted**: the C# tooling is *written in Python*, with C++ trampolining
  in via `EditorPythonRunnerRequestBus::ExecuteByString`
  (`Code/Source/Tools/O3DESharpEditorSystemComponent.cpp:297,661,680,699`).

### 3.3 The decisive finding: O3DE's registry layer supports runtime registration

Roughly **70% of "first-class" is reachable from the gem with no engine changes**:

- `SerializeContext::RegisterType(TypeId, ClassData&&, CreateAnyFunc)` is **non-templated**
  (`SerializeContext.h:212`, impl `.cpp:551`).
- `ComponentApplication::RegisterComponentDescriptor` **re-runs `Reflect()` against already-live
  contexts** (`ComponentApplication.cpp:1234-1255`).
- `AZ::ComponentDescriptor` is designed to be subclassed by hand (`Component.h:535-636`, blessed at
  `:372`); discovery is by runtime UUID via `ComponentDescriptorBus::Handler::BusConnect(uuid)`
  (`:657`).
- `Serialize::IObjectFactory` (`SerializeContext.h:1011`) and `IRttiHelper` (`RTTI.h:34-49`) are
  pure-virtual interfaces; `ClassElement` members are public and offset-based (`:882-924`).

### 3.4 The single true engine blocker

`EditContext` exposes only `template<class T> Class(...)` (`EditContext.h:207-208`), with private
storage (`:527`) and a private `ClassBuilder` constructor (`:263-271`) — despite its body doing
nothing but a UUID lookup (`:559-593`). A gem cannot work around this.

**The patch is ~30 additive lines:** a non-templated
`ClassBuilder Class(const AZ::TypeId&, const char*, const char*)` overload, with `Class<T>`
refactored to forward `AzTypeInfo<T>::Uuid()`. A companion non-templated
`DataElement(offset, typeId, ...)` is probably also required (existing overloads at `:357-396` all
take member pointers — *unconfirmed*).

This one patch is the entire difference between a **Lua-parity inspector** (gem) and a **true
native inspector** (fork).

## 4. Architecture — one capability-gated gem

**All C# functionality lives in `F:\O3DESharp`, always.** The engine-integrated version is *the same
gem plus the additive engine patch* — not a different build, and never a copy.

1. **Compile-time capability probe.** The gem's CMake runs a `try_compile` against `EditContext.h`
   for the non-templated `Class(TypeId, ...)` overload, defining
   `O3DESHARP_HAS_EDITCONTEXT_TYPEID_CLASS`. C++ cannot runtime-detect a method signature, so this
   must be compile-time. Same pattern for any other patched entry point (e.g. an
   `EditorRequests::LaunchCSharpEditor` sibling to `LaunchLuaEditor`, `ToolsApplicationAPI.h:830`).
2. **Two implementations behind one interface.** `IManagedInspectorBackend`, with a
   `SetDynamicEditDataProvider` implementation (always compiles) and an EditContext implementation
   (compiled only when the probe succeeds). On an unpatched engine the gem loses the native
   inspector and the "open in IDE" button; **everything else stays intact.**
3. **`gem.json` declares the truth.** Populate `compatible_engines` + `engine_api_dependencies` so
   `compatibility.py:210` refuses cleanly instead of failing 40 minutes into a build. These are
   empty today, which short-circuits the check entirely (`compatibility.py:222`) — the
   highest-leverage, lowest-cost fix available.
4. **The engine consumes the gem, never copies it** (submodule or subtree, `.gitmodules` present).

**Precedent:** the fork already does exactly this shape of change — commit `e0259c08aa` made the
source-control subsystem provider-agnostic at core level and pushed the implementation into a gem.

## 5. Distribution strategy

| Layer | Mechanism |
|---|---|
| Gem + templates | **Remote gem repo** — `repo.json`, `o3de repo --refresh`. Already supported end-to-end by the CLI *and* Project Manager (download, SHA-256 validation, manifest cross-check). `gem.json` `repo_uri` is empty today. |
| Registration / wiring | **Python installer calling the `o3de` package API** (`register_gem_path`, `enable_gem_in_project`) — never hand-edited JSON. Idempotent, backup-before-touch, `--uninstall`, hard `dotnet --list-runtimes` precheck. Runs under O3DE's bundled Python. |
| Engine-invasive changes | **A published branch users merge** — git does conflict resolution with history, revert, and bisect. |
| Fallback only | `git format-patch` series generated from that branch. |
| Gate everything | `compatible_engines` / `engine_api_dependencies`. |

**Explicitly rejected: overlay zips over engine-owned files.** Silent clobbering, no backup, no
version telemetry, pins users to your revision of an engine file forever, and on Windows partially
fails if the Editor or AssetProcessor is running. Acceptable *only* for purely additive,
engine-unowned paths.

**Honest failure modes:** baseline fragmentation across `o3de/o3de development`, point releases, and
users' own forks is **unsolvable** — version gating only makes mismatch *legible*. `template.json`
enumerates files explicitly with no globs, so payload files not in the manifest are silently not
copied (add a CI check). The .NET 9 SDK+runtime prerequisite cannot be solved by any distribution
mechanism; the installer must hard-fail loudly.

## 6. Decomposition

Ordering principle: each sub-project ships standalone value; nothing later depends on speculation.

### Phase 0 — Stable ground

- **SP-0 · Single source of truth** — *S–M, both tracks.* Reconcile `F:\engine\Gems\O3DESharp`
  against `F:\O3DESharp` (decide per-file authority for the ~6.5k divergent lines), convert to a
  real submodule/subtree, populate `compatible_engines` / `engine_api_dependencies` / `repo_uri`.
  **Blocks everything.** No dependencies.
- **SP-1 · Marshaling core** — *L, gem.* The **planned 1.3 BehaviorContext dispatch refactor, pulled
  forward and scope-expanded** with a C# delegate/callback binding path (analogous to
  `EditorPythonBindings/CustomTypeBindingBus.h`) so managed callbacks cross the boundary with correct
  lifetime. **This is the load-bearing beam** — SP-3, SP-8, and SP-9 all sit on it. Depends: SP-0.
- **SP-2 · Linux parity (M1)** — *M/L, gem.* Already in flight (PR #14). Lands before SP-6 so the
  asset builder is authored dual-platform rather than retrofitted. Depends: SP-0.

### Phase 1 — Real components

- **SP-3 · Managed type registry** — *L, **gem-only***. Every user C# class becomes a real
  `AZ::Component` with its own stable TypeId: hand-rolled `ComponentDescriptor` +
  `ComponentDescriptorBus::Handler::BusConnect(uuid)` + `RegisterComponentDescriptor` +
  `SerializeContext::RegisterType` with hand-built `ClassElement` arrays and an `IObjectFactory`;
  "is-a Component" via `IRttiHelper` or the `FLG_BASE_CLASS` fallback
  (`SerializeContext.cpp:1908-1918`); services as runtime CRC32; a native trampoline component
  forwarding `Init/Activate/Deactivate` to Coral.
  **Unlocks the single biggest jump in perceived first-classness** — per-class Add-Component
  entries, real prefab `$type` round-trip, service dependency resolution. Depends: SP-0, SP-1.
- **SP-4 · Reload teardown safety** — *M/L, gem.* Ordered teardown so unregistering a live managed
  type doesn't crash: quiesce entities holding the type, invalidate RPE `InstanceDataHierarchy`
  before `UnregisterType`, then `Unreflect`; preserve/restore serialized state across the swap.
  Recon rates this **higher risk than the engine patch** — keep it out of SP-3. Depends: SP-3.

### Phase 2 — Native inspector

- **SP-5a · Inspector fallback (minimal)** — *S–M, gem.* `SetDynamicEditDataProvider`
  (`EditContext.h:440`, Lua's exact pattern at `ScriptEditorComponent.cpp:1099-1105`) replacing the
  stringly-typed handler. **Deliberately minimal** — this is graceful degradation for unpatched
  engines, not a polished tier (per the "fork is the product" decision). Depends: SP-3.
- **SP-5b · Inspector, native (engine patch)** — *M, **engine***. The ~30-line additive `EditContext`
  overload + the gem-side property-block design that gives `ClassElement::m_offset` something real
  to address. **This is the product target.** Depends: SP-3, SP-5a.

### Phase 3 — Real assets

- **SP-6 · Assemblies as assets** — *XL, gem.* A `CSharpAssemblyBuilder` system component tagged
  `ComponentTags::AssetBuilder` (template: `LuaBuilderComponent.cpp:26-50`, which lives in a **gem**),
  pattern `*.csproj`, `CreateJobs` emitting `.cs` source dependencies, `m_analysisFingerprint` =
  SDK/TFM/generator version, `ProcessJob` shelling `dotnet build` via `ProcessWatcher`
  (precedent: `AzslCompiler.cpp:30`). Emits **two** products: the assembly and a **metadata product**
  (declared classes + property schema) — the metadata is what buys the inspector and script picker
  without loading the DLL. **Deletes `CSharpAssemblyWatcher`.** Depends: SP-2, SP-4.
- **SP-7 · Self-contained deploy (M2)** — *M, gem.* Already planned
  (`docs/superpowers/plans/2026-07-15-m2-self-contained-deploy.md`). **Scope boundary: the private
  .NET runtime bundle only.** User assemblies belong to SP-6. Depends: SP-2.

### Phase 4 — Tooling in C#

- **SP-8 · C# editor logic tier** — *M, gem.* Port the non-UI ~60–70% of the Python tooling
  (`csharp_project_manager.py` ~1,766 lines, `gem_dependency_resolver.py` ~870, the generators):
  file I/O, settings registry, templating, and MSBuild invocation — upgrading from subprocess
  parsing to `Microsoft.Build.Locator`/`MSBuildWorkspace`. Standalone value immediately.
  Depends: SP-1.
- **SP-9 · C# editor UI host** — *L, gem.* A C++-owned generic panel host driven by a C#-supplied
  schema, registered via `RegisterCustomViewPane` (`ToolsApplication.cpp:414`), plus a C#
  ActionManager shim mirroring `Gems/EditorPythonBindings/Code/Source/ActionManager/`, plus a
  main-thread marshaling helper. **Deletes the `ExecuteByString` trampolines** — the milestone where
  the Python dependency disappears. Depends: SP-1, SP-5b.

### Phase 5 — Distribution

- **SP-P · Patch & distribution kit** — *M, both.* The end-user path to parity: published engine
  branch, remote gem repo (`repo.json` + `repo_uri`), `o3de`-API-based Python installer, C#-first
  project template, and CI that diffs the template payload tree against `template.json`.
  Elevated to a **first-class deliverable** by the "ship patches to end users" decision; must land
  close behind SP-5b so the patch it distributes actually exists. Depends: SP-5b.

## 7. Decisions deferred to their sub-projects

- **Component identity (SP-3)** — explicit `[ComponentTypeId("{GUID}")]` vs name hash. TypeId is
  persisted in prefabs *twice* (UUID and `Crc32(name)`, `SerializeContext.cpp:551-558`). MVID/fresh
  GUID orphans prefabs every rebuild; name-derived orphans them on rename; explicit GUID is
  rename-safe but worse ergonomics — and note the descriptor dedupe path compares *names*, so a
  rename with a stable GUID trips `Component.h:285-288`. **Permanent and unmigratable once users
  ship prefabs.**
- **Binding strategy (SP-1/SP-3)** — keep offline ClangSharp/reflection generation, or move toward
  Lua-style total runtime binding (`ScriptContext::BindTo`)? Runtime binding gives Lua's ergonomics;
  offline generation is what makes **dual-mode AOT possible at all**. Near mutually exclusive.
- **Editor UI ceiling (SP-9)** — schema-driven panels (bounded, recommended) vs a Qt-for-.NET
  binding (uncapped, but a permanent ABI-matched maintenance liability against the engine's exact
  Qt build). There is no C# equivalent of PySide2/shiboken2.

## 8. Immediate actions

1. **SP-0** — reconcile the vendored gem. Nothing else is safe first.
2. **Three spikes**, all load-bearing and all left *unconfirmed* by recon:
   - `ClassElement::m_offset` semantics against a native property block backing managed fields.
   - `InstanceDataHierarchy` / RPE assumptions about `m_azRtti != nullptr`, and `azrtti_typeid`
     usage on `AZ::Component*` across AzToolsFramework (the "every managed instance shares one C++
     class" misidentification risk).
   - `ReflectedPropertyEditor` API adequacy for schema-driven panels (SP-9's foundation).
3. **Populate `gem.json` `engine_api_dependencies`** — a one-line change that converts the most
   common support failure into a clear refusal.

## 9. Non-goals

- Not rewriting engine subsystems in C# — C++ remains the engine/perf layer.
- Not a Qt-for-.NET binding (unless SP-9's decision goes that way).
- Not upstreaming to `o3de/o3de` as a dependency of this program (may be attempted separately).
- Not consoles/mobile here — that remains the Mono-AOT milestone in the Linux+AOT design
  (`docs/superpowers/specs/2026-07-02-linux-aot-support-design.md`).
