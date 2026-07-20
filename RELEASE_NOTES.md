# O3DESharp v1.3.0 — Release Notes (EXPERIMENTAL)

**Date:** 2026-07-15
**Status:** ⚠️ **Experimental pre-release.** Ships the Linux-support foundation
(M0 + M1) for community testing. The Linux paths have **not** been verified on a
real Linux O3DE build by the maintainers — please test and report. Windows is
unaffected and remains stable. `main` stays on v1.2.0; this is a pre-release off
`development`.

This release is the first milestone of the **Linux + dual-mode AOT** effort
(design: `docs/superpowers/specs/2026-07-02-linux-aot-support-design.md`). It is
Linux-portability plumbing only — no BehaviorContext, AOT, or dispatch changes.

## Highlights (experimental)

### Linux editor + runtime parity (M0 + M1)

- **Configure unblock (M0):** added the missing
  `Code/Platform/Linux/o3desharp_shared_files.cmake` stub whose absence was a
  fatal CMake `include()` error on Linux, and made the PAL support traits honest
  — Windows + Linux `TRUE`; Mac/Android/iOS `FALSE` to match `gem.json` (they
  were all `TRUE`, which would have attempted unsupported builds).
- **Portable editor tooling (M1):** a new `Editor/Scripts/csharp_platform_utils.py`
  (stdlib-only) centralizes the previously Windows-hardcoded bits:
  - `resolve_dotnet()` — finds `dotnet` via `$O3DESHARP_DOTNET_EXECUTABLE` →
    `PATH` → `$DOTNET_ROOT` → well-known per-OS locations, so an editor launched
    without the user's login `PATH` (common on Linux) still builds C#.
  - `open_in_default_app()` — `xdg-open` / `open` / `os.startfile` instead of the
    Windows-only `os.startfile`.
  - `render_vscode_launch_json()` — host-aware `launch.json` (`build/linux`, no
    `.exe`) instead of a hardcoded Windows launcher path.
- **Non-MSVC `O3DE.Core` build:** on Ninja/Make generators, `O3DE.Core.dll` is
  now built via `dotnet` (previously only the MSVC `include_external_msproject`
  path built it), with staging hardened for install/redistribution trees.
- **Rider discovery** on Linux/macOS JetBrains Toolbox layouts; corrected stale
  ".NET 8.0 SDK" prompts to 9.0.

## Tooling / tests

- New `csharp_platform_utils` unit tests plus stub-free AST guards (no bare
  `["dotnet", ...]` literals; every helper is imported where it's called;
  PAL-trait ↔ `gem.json` parity; host-aware `launch.json`). The Linux/macOS
  code branches are directly covered. **88 Python tests pass** on the existing
  Windows + Linux CI agents.
- Verified the M2 groundwork mechanism: a self-contained `linux-x64` publish
  produces a private `libcoreclr.so` + `libhostfxr.so` runtime (no machine-wide
  .NET) — see the M2 plan below.

## Known limitations

- **Linux is unverified by the maintainers.** The non-MSVC CMake build, the
  Coral fork's Linux CLR hosting (`libcoreclr.so`/hostfxr), and the end-to-end
  author→build→hot-reload→run loop need validation on a real Linux O3DE
  checkout. That is exactly what this experimental release asks testers to
  exercise (see the checklist in PR #14).
- **Coral desktop-only.** Consoles/mobile are not supported (that is the later
  Mono-AOT milestone).
- **No "ship without installing .NET" yet.** Players still need .NET 9 installed.
  That is **M2 (self-contained deployment)**, planned and pre-validated:
  `docs/superpowers/plans/2026-07-15-m2-self-contained-deploy.md`.

## Upgrade notes

- No breaking changes for existing Windows users; the Linux work is additive.
- Building on Linux: see the new "Building on Linux" section in `README.md`
  (.NET 9 SDK on `PATH` / `DOTNET_ROOT` / `O3DESHARP_DOTNET_EXECUTABLE`, or the
  CMake auto-install into `<build>/.dotnet`).

## Acknowledgements

Implementation by Mikael K. Aboagye (WD Studios Corp.). Built against
Coral.Managed (WD Studios fork of StudioCherno/Coral) and O3DE main.

---

# O3DESharp v1.2.0 — Release Notes

**Date:** 2026-07-02
**Branch:** development → main
**Compatibility:** O3DE main (tested against profile + debug; the
release config has pre-existing /WX breaks unrelated to this release).

This release is a UX/UI and performance audit pass across the whole
gem: runtime hot paths, editor tooling, the binding generator, and
docs. No new scripting features ship in v1.2.0 — the focus was closing
correctness bugs (including one ship-blocking math bug), removing perf
overhead on frequently-hit paths, fixing editor UX papercuts that could
freeze the Editor UI, and hardening the binding generator's
diagnosability. There was no v1.1.0 release.

## Highlights

### Ship-blocking fix: `Quaternion.LookRotation` returned the inverse rotation

The Shepperd trace-based matrix-to-quaternion conversion had every
antisymmetric off-diagonal term's subtraction order flipped relative to
the convention `RotatePoint` itself uses, so `LookRotation` silently
returned the inverse of the intended rotation for any non-trivial
direction — turrets, cameras, and characters facing a target all turned
backwards. Only the trivial forward-equals-up case was unaffected. Now
covered by regression tests asserting
`LookRotation(dir).RotatePoint(Vector3.Forward) == dir` for several
non-axis-aligned directions.

### Runtime performance

- **`StackAllocator`** (BehaviorContext arg marshaling) now backs a real
  bump arena (`alignas(16)` inline buffer) instead of issuing a heap
  `new` per marshaled argument.
- **`Entity.GetChildren()`** was O(n²) — one native call per child index
  lookup. Replaced with a single bulk internal call
  (`Entity_GetChildren`) that fills a native buffer in one round trip.
- **`ForwardEventToManaged`** now caches the resolved `Coral::Type*` on
  the `ManagedEBusProxy` instead of re-resolving it on every single
  dispatched event.
- **`GenericDispatcher`**'s per-call diagnostic `contextLabel` string is
  now built lazily — only formatted if an error path actually needs it,
  instead of unconditionally on every BehaviorContext call.

### Editor UX: Project Manager no longer freezes the Editor

**Build** and **Build All** in the C# Project Manager ran `dotnet build`
synchronously on the UI thread, freezing the whole Editor for the
duration of the build. Both now run on background `QThread` workers
with duplicate-build guards and a module-level keep-alive registry
(closing the dialog mid-build could previously destroy a running
`QThread` and crash the whole Editor process with a Qt fatal error —
fixed as part of the same change). The script-picker "Clear Selection"
action is also fixed: it previously looked identical to "user hit
Cancel" and silently no-opped instead of clearing the field.

### Binding generator diagnosability + determinism

- Retargeted to **net9.0** (matching `O3DE.Core` and the rest of the
  gem).
- A malformed `binding_config.json` used to fall back to defaults
  while still printing a success-looking `Configuration: {path}` line.
  It now prints an unambiguous `WARNING: failed to parse ...` and a
  `Load` overload reports whether the load actually succeeded.
- Unknown config keys (typos like `requireExportAttrib`) and unresolved
  `${VAR}` environment references are now flagged with a `WARNING`
  instead of silently no-opping.
- A zero-bindings result now explains *why* (aggregate skip-reason
  counters — filtered by name vs. no bindable public members) instead
  of leaving the user to re-run with `--verbose` to find out.
- Header file discovery and parsed class/function/enum ordering are now
  sorted deterministically, so generated output — and which declaration
  wins a name collision — no longer depends on filesystem enumeration
  order or which machine/OS ran the generator.
- The clang backend (`--source clang`, opt-in) now shares one
  `CXIndex` per gem instead of creating/disposing one per header file,
  cutting per-file libclang session setup cost. Full PCH/prelude-reuse
  caching (a larger follow-on optimization) is scoped out of this
  release — see Known limitations.

## Bug fixes

### `Quaternion.LookRotation` conjugate rotation

See Highlights above — this is the release's one ship-blocking fix.

### `Vector2`/`Vector3`/`Quaternion.Equals` violated the hash code contract

`Equals` used an epsilon tolerance while `GetHashCode` hashed exact
float bits, violating the .NET contract that equal objects must report
equal hash codes — this silently broke `Dictionary`/`HashSet` lookups
for near-equal keys. `Equals` now compares components exactly, matching
`System.Numerics.Vector3`'s convention. See Upgrade notes below.

### `Debug.Log` / `LogWarning` / `LogError` / `LogDebug` crashed on bad format strings

A malformed format string (or a `ToString()` override that throws) took
down the calling script instead of logging a diagnosable error. Now
wrapped in a `SafeFormat` helper that reports the formatting failure
instead of propagating the exception.

### `Debugger.WaitForAttach(TimeSpan.Zero)` waited forever instead of not waiting

An off-by-one comparison (`<=` instead of `<`) meant a caller explicitly
asking for a zero-length wait got an infinite wait instead.

### `NativeReflection` silently stringified unsupported argument types

Passing an argument type the marshaling table didn't recognize used to
fall back to `arg.ToString()`, producing a value that looked plausible
but was semantically wrong on the native side. Now throws
`NotSupportedException` so the mismatch surfaces immediately instead of
corrupting data silently downstream.

### Use-after-free in `ManagedHandlerTable::Register`'s rollback path

`Register` took its `AZStd::unique_ptr<ManagedEBusProxy>` proxy
parameter by value; a duplicate-token rejection destroyed the proxy
inside `Register`'s own stack frame before the caller's rollback
`Disconnect()` ran against it. Changed the parameter to a non-const
reference so ownership stays with the caller until `Register` actually
commits it.

### Numeric coercion in `BroadcastResultEBusEvent` / `SendResultEBusEvent`

A C# script reading a native `float` result (e.g.
`TickRequestBus.GetTickDeltaTime()`) hit an `InvalidCastException` every
frame: the JSON wire format has no way to distinguish `float` from
`double`, so the strict `raw is T` check failed on every numeric
result. Both result-returning EBus call paths now go through a shared
`CoerceEBusResult<T>` helper that falls back to `Convert.ChangeType` for
numeric promotions/demotions before giving up, matching the tolerance
`EBusHandlerRegistry.UnmarshalArg<T>` already had on the inbound side.

## Documentation

- `README.md`, `GENERATED_BINDINGS_GUIDE.md`, and `SCRIPTING_GUIDE.md`
  described the ClangSharp header-parser backend as canonical when the
  actual (and recommended) default is the reflection backend
  (`--source reflection`, reading `reflection_data.json`). Added a
  "Which backend should I use?" callout to each doc and made every
  example command explicit about `--source`.
- `README.md` referred to a nonexistent **Tools > C# Script Manager**
  menu. The dialog is actually titled **C# Project Manager**; menu
  registration in `csharp_editor_bootstrap.py` is currently a stub that
  only logs Python-console usage hints, so the doc now shows the actual
  working path (open via the Python console) instead of a menu item
  that doesn't exist yet.
- Added `Guid` (for `AZ::Uuid`) to the EBus handler arg-marshaling
  coverage lists in `README.md` and `SCRIPTING_GUIDE.md` —
  `EBusHandlerRegistry` already supported it, the docs just hadn't
  caught up.

## Tooling / CI

- CI migrated from GitHub Actions to TeamCity Cloud.
- Windows CI steps and the Ubuntu 24.04 pytest install fixed.
- New `O3DE.Core.Tests` xUnit project (45 tests) covering the math,
  reflection, and `Debug`/`Debugger` fixes in this release —
  `O3DE.Core` had no dedicated test project before this release.
- `BindingGenerator.Tests` grew from 104 to 122 tests (120 passing + 2
  skipped integration tests that require a real O3DE engine checkout),
  covering the config-loading, determinism, and clang-backend fixes
  above.

## Known limitations

Carried forward from v1.0.0 (still true):

- **Managed-defined bus contracts** (Phase 18-C): a C#
  `[EBus] interface IMyBus { ... }` that other C++, Lua, ScriptCanvas,
  or C# can implement and broadcast on. Not yet implemented; tracked in
  `PHASE_18_EBUS.md` §3.C.
- **Handler param marshaling for large/non-trivial types**: `Transform`,
  `Vector4`, `Color`, `Aabb`, `Matrix3x3` / `Matrix4x4`, and arbitrary
  user-defined structs in handler signatures still arrive as
  `default(T)` with a console warning.
- **macOS / iOS / Android / consoles**: `gem.json` declares Linux +
  Windows only.

New in this release:

- **`CoralHostManager` user/core `AssemblyLoadContext` separation**:
  investigated as part of this audit (every hot-reload currently
  rebuilds all of `O3DE.Core`, not just the changed user assembly). A
  proper fix needs either an unverifiable Coral API or changes to the
  separate Coral.Managed repo — documented in `CoralHostManager.cpp`
  as a known limitation rather than attempting an unverified workaround.
  Tracked as its own follow-up ticket.
- **BindingGenerator clang-backend PCH/prelude caching**: the
  small/low-risk half of this optimization (sharing one `CXIndex` per
  gem) shipped in this release; the larger prelude precompiled-header
  caching layer did not, and is deferred to a follow-up ticket. Clang
  backend only — `--source reflection` (the default) is unaffected
  either way.
- **`GenericDispatcher`'s BehaviorContext dispatch overhead**: this
  release's perf fixes addressed marshaling/allocation hot spots around
  dispatch (`StackAllocator`, `contextLabel` formatting, cached
  `Coral::Type*`), but the dispatch mechanism itself is unchanged.
  Broader BehaviorContext control/perf work is planned for a 1.3
  refactor.

## Upgrade notes

- **`Vector2`/`Vector3`/`Quaternion.Equals` is now exact**, not
  epsilon-tolerant. Code relying on near-equal values comparing equal
  (e.g. after a lossy round-trip) should switch to
  `Vector3.Distance(a, b) < epsilon` (or `Quaternion.Angle` for
  rotations) instead of `Equals`/`==`.
- **BindingGenerator now requires the .NET 9 SDK** to build (retargeted
  from net8.0, matching `O3DE.Core`).

## Acknowledgements

Implementation by Mikael K. Aboagye (WD Studios Corp.). Built against
Coral.Managed (WD Studios fork of StudioCherno/Coral) and O3DE main.

---

# O3DESharp v1.0.0 — Release Notes

**Date:** 2026-05-19
**Branch:** development → main
**Compatibility:** O3DE main (tested against profile + debug; the
release config has pre-existing /WX breaks unrelated to this release).

This is the first production-ready cut of O3DESharp. It closes the
loop on EBus support so C# is functionally on par with Lua and
ScriptCanvas as an O3DE scripting language: scripts can both *send*
events to engine buses and *receive* events from them as first-class
handlers. The Roslyn source generator now authors the Connect /
Disconnect / dispatch glue from a couple of attributes, and the
underlying reflection plumbing has been hardened against the most
common crash modes.

## Highlights

### First-class EBus handlers — `[EBus]` / `[EBusHandler]`

Decorate any partial `ScriptComponent` subclass with `[EBus("BusName")]`
and individual methods with `[EBusHandler("EventName")]`; the
`O3DESharp.SourceGenerators` Roslyn analyzer emits the
`ConnectTo<BusName>` / `DisconnectFrom<BusName>` / dispatch glue at
compile time.

```csharp
[EBus("TickBus")]
public partial class GameClock : ScriptComponent
{
    public override void OnCreate()  { ConnectToTickBus(); }
    public override void OnDestroy() { DisconnectFromTickBus(); }

    [EBusHandler("OnTick")]
    private void HandleTick(float deltaSeconds, ulong frameId)
    {
        Debug.Log($"frame {frameId}: dt={deltaSeconds}");
    }
}
```

Wire-shape coverage on the marshaling side: primitives (bool, integer
types, float, double), string, `Guid` for `AZ::Uuid`, `Vector2/3`,
`Quaternion`, and EntityId-shaped IDs (as `ulong`). Multi-bus
subscriptions emit one Connect/Disconnect/dispatch trio per bus. See
`SCRIPTING_GUIDE.md` §9 — *Receiving EBus Events* for the full
authoring reference.

### Reflection dispatcher — actually dispatches

`BroadcastEBusEvent`, `SendEBusEvent`, `BroadcastResultEBusEvent<T>`,
`SendResultEBusEvent<T>`, `InvokeStaticMethod`, `InvokeInstanceMethod`,
and `InvokeGlobalMethod` are no longer stubs. The C++ side resolves
each call through `BehaviorContext`, marshals args via the existing
`BehaviorContextMarshaling` table, dispatches, and returns the result
back through the JSON envelope. Property accessors (`GetProperty` /
`SetProperty` for instance and global) also dispatch through the same
path.

### NativeObject handle table — real instance lifetime

`NativeReflection.CreateInstance` returns an opaque `int64` handle
backed by a thread-safe table keyed by monotonic id, not a raw pointer.
Stale handles error cleanly instead of dereferencing freed memory.
`InvokeInstanceMethod` / `GetProperty` / `SetProperty` / `DestroyInstance`
all route through the table.

### Marshaling-table widening

Round-trip support for `Vector2`, `Vector4`, `Color`, `Aabb`, `Crc32`,
`Uuid`, `Matrix3x3`, `Matrix4x4`, and the existing `Vector3` /
`Quaternion` / `Transform` / `EntityId` / `AZStd::string` /
primitives. Math types travel as flat float arrays (length disambiguates
type on the unmarshal side), `Uuid` travels as its string form, `Crc32`
as a `uint`.

### Generator: addressed-bus codegen + per-gem assemblies

When the reflected bus has an `AddressType`, the C# binding generator
emits an addressed-event wrapper alongside the broadcast variant
(`.Event(entityId)` builder). Each gem's bindings now compile into a
standalone `<Gem>.dll` instead of being piled into a single
`O3DESharp.dll`, with `source_gem_name` populated in the reflection
data exporter so the generator knows where each type came from.

### Hot reload survives generator regen

Re-running the generator no longer breaks hot reload — the editor
picks up the new wrappers on the next assembly load without a restart.
Generated csprojs auto-build after each regeneration so the
`Bin/Scripts/` mirror stays consistent.

### Inspector ExposedProperty edits are live

Editing `[ExposedProperty]` field values in the inspector pushes the
new value to the underlying runtime instance on the next tick, no
re-Activate required. `AutoAttachOnPlay = Off` is also now respected
for explicit-Off settings (previously fell through to the global
default).

## Bug fixes

### Crash: result-returning EBus events null-deref'd in AzCore

`BroadcastResultEBusEvent` and `SendResultEBusEvent` for any reflected
event returning a value (e.g. `TickRequestBus::GetTickDeltaTime`)
crashed in `AZ::SetResult::Set` with a null write at `0x0`. The
default-constructed result `BehaviorArgument` had `m_value = nullptr`;
the dispatcher's `operator=` reads through that pointer.

Fix: a `ComputeResultStorageRequirements` helper covering every
primitive + math/identifier type plus a `BehaviorClass` fallback for
reflected user types. The result block now allocates storage via
`BehaviorArgument::m_tempData` for small types (≤32 bytes) or a
heap-backed buffer for `Matrix3x3` / `Matrix4x4` / `Transform`-sized
returns, zero-initialises the buffer so a no-handlers-connected event
marshals back a deterministic zero, and surfaces a clean error when
the result type has unknown storage requirements instead of crashing
inside AzCore.

### Dispatcher noise: result `m_typeId` could be `CreateNull`

`BehaviorMethod::Call` doesn't always populate the result
BehaviorArgument's `m_typeId`. Float-returning events were marshaling
back the catch-all error path with a `0x{0000…}` TypeId. Fix:
pre-populate `m_typeId` / `m_name` / `m_traits` / `m_azRtti` on the
result from the reflected `BehaviorParameter` before the Call.

## Documentation

- `SCRIPTING_GUIDE.md` §9 expanded with the receive-side handler
  authoring pattern, the marshaling table for handler args, and
  thread-safety notes.
- `README.md` "Known Limitations" replaced the "EBus Handlers
  receive-side not supported" entry with an accurate description of
  which arg types currently marshal vs fall back to `default(T)`.
- `PHASE_18_EBUS.md` carries a 2026-05-19 implementation note
  explaining the deviation from the original `EBusHandler<TBus>`
  base-class design to the shipped attribute + source-generator
  approach.

## Tooling / CI

- CI now also runs on the `development` branch (was main-only).
- CI builds `O3DESharp.SourceGenerators.csproj` and the new
  `Code/Tools/SourceGenerators.Tests/SourceGenerators.Smoke.csproj`
  consumer end-to-end so any regression to the generator's emit shape
  fails the build at PR review time.
- 104 BindingGenerator unit tests (xUnit) + Python editor tests
  continue to pass.

## Known limitations

These are tracked and intentionally out of scope for v1.0.0:

- **Managed-defined bus contracts** (the spec's Phase 18-C): a C#
  `[EBus] interface IMyBus { ... }` that other C++, Lua, ScriptCanvas,
  or C# can implement and broadcast on. Not yet implemented; tracked
  in `PHASE_18_EBUS.md` §3.C.
- **Handler param marshaling for large/non-trivial types**: `Transform`,
  `Vector4`, `Color`, `Aabb`, `Matrix3x3` / `Matrix4x4`, and arbitrary
  user-defined structs in handler signatures currently arrive as
  `default(T)` with a console warning. Extend
  `EBusHandlerRegistry.UnmarshalArg<T>` and `BehaviorContextMarshaling`
  to widen.
- **Release-config /WX build**: three pre-existing unused-parameter
  warnings (in code that pre-dates this release) get promoted to
  errors under `/WX`. Profile + debug builds are clean. CI runs C#
  only so this doesn't block CI.
- **macOS / iOS / Android / consoles**: gem.json declares Linux +
  Windows only. The other platforms need NativeAOT-friendly bindings
  + platform Coral hosting, neither of which exists yet.

## Upgrade notes

- Projects whose csprojs predate Phase 16b should run
  Tools → C# Scripting → **Migrate C# Project Files** once to pick up
  the `DeployToBinScripts` MSBuild target.
- Projects on .NET 8 must move to .NET 9 — `O3DE.Core` and the user
  csproj template both target net9.0 with `rollForward: LatestMinor`.
- Existing `class MyBus::Handler` C# code (if any) does not exist in
  prior versions, but anything written against the previous spec's
  proposed `EBusHandler<TBus>` base class will need rewriting to the
  attribute-driven shape. See `PHASE_18_EBUS.md` for the deviation
  note.

## Acknowledgements

Implementation by Mikael K. Aboagye (WD Studios Corp.). Built against
Coral.Managed (WD Studios fork of StudioCherno/Coral) and O3DE main.
