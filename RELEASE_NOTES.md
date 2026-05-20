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
