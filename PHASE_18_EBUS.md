# Phase 18 — Full EBus Support for O3DESharp

**Status:** spec — implementation pending sign-off.
**Scope:** A + B + C + D (everything: consume engine EBuses, write handlers, define new buses, ScriptCanvas / inspector surfacing).
**Estimated effort:** 10–15 working days across one large commit (per user preference), implementation broken into reviewable sub-phases internally even though they ship together.

---

## 1. Motivation

EBus is O3DE's primary inter-system messaging primitive. The current gem
gives C# scripts access to two narrow slices of it:

| Capability today | Where |
|---|---|
| Receive `OnTick` and `OnTransformChanged` callbacks from C++ scripts that derive from `ScriptComponent`. | Hardcoded in `CSharpScriptComponent::Activate`. |
| Dynamic invocation of "any" BehaviorContext method by name. | `NativeReflection.InvokeStaticMethod` / `InvokeInstanceMethod`. |

Both gaps:

* `NativeReflection.BroadcastEBusEvent(busName, eventName, args)` is
  documented in the README and surfaced to scripts, but the C++
  dispatcher (`GenericDispatcher::BroadcastEBusEvent`) is the
  hardcoded `{"error":"Not fully implemented"}` stub from the
  2026-05-15 audit (defect #14, partial fix in Phase 4 to *surface*
  the unimplemented dispatch but not implement it).
* There is no managed equivalent of `TransformNotificationBus::Handler::BusConnect`
  for arbitrary buses. A script can't be a handler for, say,
  `MyGame::PlayerDeathBus` without writing a new C++ component first.
* C# code can't define its own bus contracts. No way to say
  "here's an interface, any system (C++, Lua, ScriptCanvas, another
  C#) can implement or broadcast it."
* ScriptCanvas sees BehaviorContext-reflected types automatically;
  managed-defined buses don't exist there yet because nothing puts
  them in BehaviorContext.

Phase 18 closes all four gaps in one coordinated set of changes.

---

## 2. Current state vs. target

```
                         Today                         Phase 18
─────────────────────────────────────────────────────────────────────
Call engine bus event    NativeReflection.            EngineBus.Broadcast.Event(args)
                         BroadcastEBusEvent           (typed)
                         (stub, returns error)        + dynamic fallback works
─────────────────────────────────────────────────────────────────────
Receive engine bus       Subclass-only for           public class H : EBusHandler<TBus>
                         OnTick / OnTransformChanged + handler.Connect(addr)
─────────────────────────────────────────────────────────────────────
Define managed bus       Not possible                 [EBus] interface IMyBus
                                                      + auto-generated dispatcher
─────────────────────────────────────────────────────────────────────
Managed bus in           Not possible                 Bus auto-registers in
ScriptCanvas / Inspector                              BehaviorContext;
                                                      SC + Inspector pick it up
```

---

## 3. Architecture

Four sub-phases. All ship in one commit, but the internal structure is:

```
                      ┌──────────────────────────────┐
                      │  Phase 18-D  Editor surfaces │
                      │  (SC, inspector, autodoc)    │
                      └──────────────┬───────────────┘
                                     │ piggybacks
              ┌──────────────────────┴──────────────────────┐
              │                                             │
   ┌──────────▼──────────┐                       ┌──────────▼──────────┐
   │  Phase 18-B          │                       │  Phase 18-C          │
   │  managed handlers    │                       │  managed bus contracts│
   │  (consume native)    │                       │  (publish to native) │
   └──────────┬──────────┘                       └──────────┬──────────┘
              │                                             │
              └──────────────────────┬──────────────────────┘
                                     │
                          ┌──────────▼──────────┐
                          │  Phase 18-A          │
                          │  dispatch + marshal  │
                          │  (foundation)         │
                          └─────────────────────┘
```

A is the foundation. B and C build on it in parallel. D drops in once
B+C are merged.

### 3.A — Dispatch and marshaling foundation

**Goal:** `NativeReflection.BroadcastEBusEvent(busName, eventName, args)`
and `BroadcastResultEBusEvent<T>(...)` actually work.

**C++ side** — replace the stubs in
`Code/Source/Scripting/Reflection/GenericDispatcher.cpp` lines 1248–1318:

1. Look up the bus by name in `BehaviorContext::m_ebuses`.
2. Look up the event by name in the bus's `m_events` map. Each
   `BehaviorEBusEventSender` has the `Broadcast`, `Event`, `Queue*`
   variants pre-populated.
3. Build a `BehaviorArgument[]` from the JSON-encoded args the
   managed side sent, using a new
   `Convert::JsonValueToBehaviorParameter(rapidjson::Value&, const
   BehaviorParameter&)` helper. Parallel to EditorPythonBindings'
   `Convert::PythonToBehaviorValueParameter`; same set of supported
   types (primitives, math, EntityId, strings, BehaviorObject handles).
4. Decide between `Broadcast` and `Event` based on whether the first
   arg is a bus id (heuristic) **or** explicit API split (preferred —
   see managed API below).
5. Invoke and marshal the result back into a JSON value.

**Managed side** —
`Assets/Scripts/O3DE.Core/Reflection/NativeReflection.cs`:

```csharp
public static class NativeReflection
{
    // Broadcast - sends to every connected handler on the bus.
    public static void BroadcastEBusEvent(
        string busName, string eventName, params object[] args);

    public static T BroadcastResultEBusEvent<T>(
        string busName, string eventName, params object[] args);

    // Event - sends only to handlers connected at the specified bus id.
    public static void SendEBusEvent<TBusId>(
        string busName, string eventName, TBusId busId, params object[] args);

    public static T SendResultEBusEvent<T, TBusId>(
        string busName, string eventName, TBusId busId, params object[] args);
}
```

Old `BroadcastEBusEvent(busName, eventName, entityId, args)` keeps
working as a deprecation-period shim that calls into `SendEBusEvent`.

**Typed wrappers** are emitted by the ClangSharp generator (see §6).
Hand-written `NativeReflection` API is the "I don't have generated
bindings yet" escape hatch.

### 3.B — Managed handlers

**Goal:** any C# class can be a handler for any reflected EBus.

```csharp
public class MyTransformWatcher : EBusHandler<TransformNotificationBus>
{
    // Method names match the reflected event names on the bus.
    public override void OnTransformChanged(Transform local, Transform world)
    {
        Debug.Log($"Entity {EntityId} moved to {world.Position}");
    }
}

public override void OnCreate()
{
    m_watcher = new MyTransformWatcher();
    m_watcher.Connect(this.EntityId);   // ById bus
    // ...or m_watcher.Connect() on a Single bus
}

public override void OnDestroy()
{
    m_watcher?.Disconnect();
    m_watcher = null;
}
```

**Implementation strategy** — reuse the same plumbing that
`EditorPythonBindings::PythonProxyNotificationHandler` uses for Python
EBus handlers. That class wraps a `Coral::BehaviorEBusHandler` derived
type created via `bus->m_createHandler` and installs a generic hook
that forwards each invocation back to script code (Python in EPB's
case, managed C# in ours).

Concrete pieces:

1. **`O3DE.EBusHandler<TBus>` (managed):** abstract base in
   `O3DE.Core`. Stores a native `IntPtr` to the underlying
   `BehaviorEBusHandler*`. Connect / Disconnect / IsConnected / Bus
   accessor.

2. **`O3DESharpManagedEBusHandler` (C++):** a `BehaviorEBusHandler`
   subclass that owns a managed `Coral::ManagedObject` for the handler
   instance. Its `InstallGenericHook` callback marshals incoming
   `BehaviorArgument[]` into a `BehaviorArgument[]` representation
   that crosses the C# boundary (same `Convert::*` helpers as 18-A,
   reused in the reverse direction), invokes the matching method on
   the managed handler by name, marshals the return value back.

3. **Lookup table:** bus name → managed virtual-method slot indices.
   For dynamic dispatch (no codegen) we resolve method-by-name on
   first call and cache per `(bus, methodIndex)`. For typed wrappers
   (codegen) we use compile-time slot indices.

4. **Registration:** the bus must already carry `Handler<T>()` in its
   BehaviorContext reflection. Most engine buses do (TransformBus,
   TickBus, ShapeComponentRequestBus, etc.). The audit-era list of
   buses we know are reflected lives in the generator's `KnownBuses`
   table; we generate wrappers only for those, and the dynamic API
   refuses to handle unreflected buses with a clear error.

5. **Threading:** O3DE EBus events fire on the thread the broadcaster
   uses. Most engine buses are main-thread. Coral's managed dispatch
   acquires the CoreCLR GIL implicitly. We document that handlers run
   on the broadcasting thread and must not call back into managed
   long-running operations from non-main threads.

6. **Lifecycle:** managed handler instances pin themselves via a
   strong GC handle while connected; the C++ side stores the
   `void*` from that handle. Disconnect releases the handle. Drop on
   `ScriptComponent.OnDestroy` is automatic via a tracking list on
   the base class. Hot reload disconnects all handlers in
   `OnBeforeUserAssemblyReload` and reconnects in `OnAfterUserAssemblyReload`
   on the new types (handled by the existing Phase 13 plumbing,
   extended).

### 3.C — Managed-defined bus contracts

**Goal:** C# code can publish a bus that other C#, C++, ScriptCanvas,
or Lua can implement and broadcast on.

```csharp
[EBus(
    Name = "MyGame.PlayerEventsBus",
    Handler = HandlerPolicy.Multiple,
    Address = AddressPolicy.ById,
    BusIdType = typeof(ulong))]
public interface IPlayerEvents
{
    void OnPlayerSpawned(string name);
    void OnPlayerDied(string reason, int score);

    // Result-returning events are allowed; first defining handler wins
    // unless [EBus(Multiple = true)] uses an aggregator.
    int GetCurrentScore();
}

// Broadcast / Event helpers are generated alongside the interface:
PlayerEvents.Broadcast.OnPlayerDied("fell off cliff", 1234);
int score = PlayerEvents.Broadcast.GetCurrentScore();
PlayerEvents.Event(playerEntityId).OnPlayerSpawned("Alice");

// Handle by implementing the interface and registering:
public class ScoreTracker : EBusHandler<IPlayerEvents>, IPlayerEvents
{
    public void OnPlayerSpawned(string name) { ... }
    public void OnPlayerDied(string reason, int score) { ... }
    public int GetCurrentScore() => m_score;
}
```

**Implementation strategy:**

A managed-defined bus needs to live in `BehaviorContext` so that
ScriptCanvas, Lua, Python, and native C++ code can see it. We can't
just keep the bus state in managed land. So:

1. **C# Source Generator** (Roslyn analyzer + generator) runs at
   `dotnet build` time on user projects:
   - Finds every `interface ... : IBus` (marker interface) or
     `[EBus]`-decorated interface.
   - Emits a partial class:
     - `<Interface>Broadcast` static dispatcher.
     - `<Interface>Event(busId)` factory.
     - A `<Interface>Registration` class with a `[ModuleInitializer]`
       that registers the bus in BehaviorContext on assembly load.

2. **C++ bus shim:** a templated `O3DESharpManagedEBus<TInterface>`
   on the C++ side that:
   - Inherits from `AZ::EBusTraits` with the configured handler /
     address policies.
   - Has virtual methods that delegate to the interface's managed
     definition via reflection at registration time.
   - Provides `BehaviorContext::EBus<>` registration that exposes the
     bus to all reflection consumers (SC, Lua, Python, BehaviorContext
     introspection).

3. **Static-side dispatcher:** the generated `<Interface>Broadcast`
   static class translates each interface method into a call through
   the C++ shim's `EBus::Broadcast<&IInterface::OnMethod>(args)`.
   Compatible with multi-handler buses (the EBus library handles
   broadcast fan-out) and result aggregation buses.

4. **Bus-by-name lookup** for callers without the generated wrapper:
   `NativeReflection.BroadcastEBusEvent("MyGame.PlayerEventsBus", ...)`
   works the same as for engine buses — it's just BehaviorContext
   metadata.

5. **Handler discovery:** when C# code implements the interface and
   subclasses `EBusHandler<T>`, the runtime resolves the C++ shim
   via the interface's TypeId, instantiates the managed-side handler
   that 18-B already builds, and connects via the standard
   `BehaviorEBusHandler::Connect(busId)`.

6. **Code-generation hook into the build:** add a NuGet reference
   `O3DESharp.SourceGenerators` to `O3DE.Core.csproj` and the user
   csproj template. Build-time only — no runtime cost.

**Trade-off:** a true managed-defined bus that doesn't go through
BehaviorContext would be slightly faster (no native ↔ managed
marshaling for managed-only callers) but invisible to non-managed
consumers. The Phase 18 design accepts the marshaling cost in
exchange for universal visibility, which is the whole point of
"define a bus."

### 3.D — Editor surfaces

Once 18-A through 18-C are in, ScriptCanvas / inspector pickup is
*free* because both consume `BehaviorContext`. Only adds:

1. **Doc auto-generation** in the binding generator: when emitting
   the typed C# wrapper for an EBus, scrape Doxygen comments from the
   bus's header and copy them onto the generated method.

2. **EBus picker** in the C# Script Component inspector — a drop-down
   showing every bus the script subscribes to (introspected from the
   `EBusHandler<T>` base classes the script type derives from). Purely
   informational; no behavior change.

3. **`Tools → C# Scripting → Inspect EBuses`** dialog: lists every
   bus + the C# wrappers available + which buses are reflected vs
   not. Helps users discover what's available without having to
   `dotnet build` to find out.

---

## 4. Marshaling table

All cross-boundary traffic uses the BehaviorContext marshaler. The
following table is the truth table for what works:

| C# type | BehaviorContext type | Marshaling |
|---|---|---|
| `bool / sbyte / byte / short / ushort / int / uint / long / ulong / float / double` | matching `AZ::*` primitive | direct copy |
| `string` | `AZStd::string` | `Coral::String::operator std::string()` (UTF-16 → UTF-8) |
| `Vector3` | `AZ::Vector3` | blittable (`[StructLayout(LayoutKind.Sequential)]`) — direct memcpy |
| `Quaternion` | `AZ::Quaternion` | blittable, direct memcpy |
| `EntityId` | `AZ::EntityId` | blittable (ulong wrapper), direct |
| `Transform` | `AZ::Transform` | blittable (rotation+translation+scale), direct |
| Any `[Reflected]` POD struct | matching reflected class | per-field marshaling, generated at compile time |
| Class handles (`O3DE.Entity`, etc.) | `BehaviorObject` (raw pointer + TypeId) | opaque ptr, lifetime managed by C++ side |
| `byte[]` / `int[]` / etc. | `AZStd::vector<T>` (when T is blittable) | length-prefixed contiguous copy |
| `List<T>` / `Dictionary<K,V>` | not supported in v1 | error at marshaling, with a clear message pointing at workarounds |
| `delegate` / `Func<>` | not supported | "use an explicit `EBusHandler<T>` subclass" error |

`Convert::JsonValueToBehaviorParameter` (C++ side) and `Convert::BehaviorParameterToJson` are the central choke points. Both already exist for the EPB Python flow; we hoist them out of EPB into a shared utility under `Code/Source/Scripting/Marshaling/` so our codepath doesn't depend on EditorPythonBindings being loaded.

---

## 5. Threading and lifecycle

* EBus events fire on the broadcaster's thread. Most engine buses are
  main-thread but `AZ::SystemTickBus` can be any thread; physics buses
  often fire from the physics simulation thread.
* Coral's CoreCLR doesn't require the GIL but managed code running on
  arbitrary threads needs to be careful with non-thread-safe APIs.
* **Rule we document:** treat EBus handlers like Unity's
  `MonoBehaviour` lifecycle methods — assume main-thread unless the
  bus's documentation says otherwise.
* For non-main-thread buses, we provide
  `O3DE.Dispatcher.RunOnMainThread(Action a)` (already implicit in
  Phase 4's single-Tick-per-frame work) so handlers can marshal back.

Connect / Disconnect lifecycle:

* `EBusHandler<T>` constructor allocates a native handle (lazy: first
  Connect call instantiates).
* Connect calls the underlying `BehaviorEBusHandler::Connect(busId)`.
* Disconnect calls the underlying `Disconnect()`.
* `ScriptComponent.OnDestroy` walks a tracking list of handlers
  created by `this` and disconnects each.
* Hot reload: `O3DESharpHotReloadNotificationBus::OnBeforeUserAssemblyReload`
  disconnects every handler in the gem; `OnAfter` re-binds via the
  new types. Handlers without a corresponding type in the new
  assembly are dropped (with a warning) instead of silently leaking.

---

## 6. Code generation

The ClangSharp binding generator (Code/Tools/BindingGenerator) gains
two new emit passes:

### 6.1 — Typed engine-bus wrappers (foundation for 18-A and 18-B)

For every `AZ::EBus<TInterface, TTraits>` declaration in a parsed
header, emit:

```csharp
// Generated from AzCore/Component/TransformBus.h
public static class TransformBus
{
    public static class Broadcast
    {
        public static void SetLocalTranslation(Vector3 newTranslation)
            => NativeReflection.BroadcastEBusEvent(
                "TransformBus", nameof(SetLocalTranslation), newTranslation);
        public static Vector3 GetWorldTranslation()
            => NativeReflection.BroadcastResultEBusEvent<Vector3>(
                "TransformBus", nameof(GetWorldTranslation));
        // ... one method per bus event
    }

    public static EventDispatcher Event(EntityId entityId) => new(entityId);
    public struct EventDispatcher  // generated per ById bus
    {
        private readonly EntityId _busId;
        // ... method-per-event, dispatching via SendEBusEvent with _busId
    }

    public abstract class Handler : EBusHandler<TransformBus>
    {
        public virtual void OnTransformChanged(Transform local, Transform world) { }
        public virtual void OnParentChanged(EntityId oldParent, EntityId newParent) { }
        // generator walks the bus's events; each maps to a virtual method
        // whose signature is the reflected event signature
    }
}
```

### 6.2 — Managed-bus C++ shims (18-C)

For every `[EBus]`-decorated interface in a user project, emit:

```cpp
// Generated alongside <Project>.dll into <Project>.cpp + .h
namespace MyGame
{
    struct PlayerEventsBusTraits : public AZ::EBusTraits
    {
        static const AZ::EBusHandlerPolicy HandlerPolicy = ...;
        static const AZ::EBusAddressPolicy AddressPolicy = ...;
        using BusIdType = ...;
    };
    class PlayerEventsRequests
    {
    public:
        virtual void OnPlayerSpawned(const AZStd::string&) = 0;
        // ... mirror each interface method
    };
    using PlayerEventsBus = AZ::EBus<PlayerEventsRequests, PlayerEventsBusTraits>;
}
```

Plus a registration call from a CMake-generated `ModuleInit` block.

### 6.3 — Where the generators live

* C++ → C# (existing) lives in
  `Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/`. Phase 18
  extends `Generation/CSharpGenerator.cs` to emit EBus wrappers when
  the parser hits an `EBus<>` declaration.
* C# → C++ shim is **new**: a Roslyn source generator under
  `Code/Tools/BindingGenerator/O3DESharp.SourceGenerators/`. Runs as
  part of `dotnet build` on user projects via a NuGet package
  reference.

---

## 7. Test plan

### Unit (gtest, runs in CI)

* `Convert::JsonValueToBehaviorParameter` round-trips for every
  marshaling-table entry. Already covered for the Python direction by
  `EditorPythonBindings/Code/Tests/PythonMarshalTests.cpp`; we extend
  it.
* `BroadcastEBusEvent` against a fixture bus with each parameter
  arity (0, 1, 2, 3, N) and both `Broadcast` and `Event` shapes.
* Handler lifecycle: Connect, fire event, Disconnect, fire event,
  expect zero callbacks after disconnect.
* Hot reload disconnects + reconnects without leaking handlers.

### Managed (xUnit, runs in CI alongside the C# generator tests)

* Source generator output snapshot tests for representative
  `[EBus]` interfaces.
* Compile-time errors when an interface has unsupported parameter
  types.

### Integration (editor smoke test, runs locally with the editor)

* A script subscribes to `TransformNotificationBus`, moves the
  entity, verifies `OnTransformChanged` was called.
* A script defines `[EBus] interface IFoo`, another script subscribes
  to it and broadcasts; verify the handler fired.
* A C++ test broadcasts on a managed-defined bus; verify the C# handler
  received the event.
* ScriptCanvas: drag the C#-defined bus into a graph, fire from a
  game-mode session, verify the SC node received it.

---

## 8. Risk inventory

| Risk | Likelihood | Mitigation |
|---|---|---|
| Bus dispatch threading bugs (handlers running on physics thread blowing up the managed runtime). | Medium | Document the rule; add a debug-build assertion that fires when a managed handler runs on a non-main thread without explicitly opting in. |
| Hot reload + active handlers = dangling pointers if reload mis-orders disconnect/reconnect. | Medium | Phase 13 plumbing already handles the script-instance side. Extend to the handler tracking list with the same Before/After pattern. Add a leak detector that fires in test builds if any handler outlives its owning script. |
| Source generator compile-time cost on large user projects. | Low | Generator only emits for `[EBus]`-decorated interfaces. Empty projects pay zero cost. |
| Roslyn source generator API churn between .NET versions. | Low–Medium | Target `Microsoft.CodeAnalysis.CSharp >= 4.5` (compatible with everything from .NET 6 onward). |
| User defines `[EBus]` on an interface with unsupported parameter types (delegate, generic List). | Medium | Generator emits a compile error with a clear message pointing at the unsupported parameter. Whitelist of supported types per §4. |
| Engine bus lacks `Handler<T>()` reflection — we can't auto-generate a wrapper. | High | Emit a `static class` with `Broadcast` / `Event` only (no handler base). Log on first use. Document that a small number of buses are receiver-only. |
| `BehaviorContext::m_ebuses` ordering changes between editor sessions and breaks our cached lookups. | Low | Lookups are always by string name, never by index. No caching of indices across editor lifecycle. |
| Marshaling layer becomes a bottleneck for hot-path event fan-out. | Low–Medium | Phase 18 ships the correctness layer. Optimization (blittable struct fast path, AOT-friendly delegates, per-event JIT'd marshaling) is a Phase 18.1 follow-up. |

---

## 9. Implementation order (within the single big commit)

Internal sub-phases, in dependency order:

1. **18-A.1** Hoist `Convert::PythonToBehaviorValueParameter` etc. out
   of EditorPythonBindings into a shared
   `Code/Source/Scripting/Marshaling/BehaviorContextMarshaling.{h,cpp}`.
   Wire EPB to use the shared utility (no behavior change there).

2. **18-A.2** Implement
   `GenericDispatcher::BroadcastEBusEvent / SendEBusEvent`.
   Add `BroadcastResultEBusEvent` + `SendResultEBusEvent`. Wire up
   the matching managed `NativeReflection` API.

3. **18-A.3** Extend ClangSharp generator to detect `AZ::EBus<>`
   declarations and emit typed `Broadcast` / `Event` wrappers.

4. **18-B.1** Add `O3DE.EBusHandler<T>` abstract base + native
   `O3DESharpManagedEBusHandler` class.

5. **18-B.2** Extend ClangSharp generator to emit the
   per-bus `Handler` nested type with virtual methods.

6. **18-B.3** Hook `ScriptComponent.OnDestroy` to auto-disconnect
   handlers. Hook hot-reload notifications to disconnect/reconnect
   across reload cycles.

7. **18-C.1** Author the `O3DESharp.SourceGenerators` Roslyn project.

8. **18-C.2** Generator emits the static `Broadcast` / `Event`
   dispatchers + `[ModuleInitializer]` BehaviorContext registration
   for `[EBus]`-marked interfaces.

9. **18-C.3** Add the `O3DESharpManagedEBus<TInterface>` C++ shim
   template + registration helper.

10. **18-D.1** ScriptCanvas pickup happens automatically. Add the
    `Inspect EBuses` editor menu item.

11. **18-D.2** Doc comment carryover in the ClangSharp generator.

12. **Tests** for each sub-phase as it lands. Final commit runs all
    of them.

---

## 10. API surface summary

After Phase 18, the public surface a script author sees:

```csharp
namespace O3DE
{
    // 18-A: untyped dynamic dispatch (escape hatch).
    public static class NativeReflection
    {
        public static void BroadcastEBusEvent(string busName, string eventName, params object[] args);
        public static T    BroadcastResultEBusEvent<T>(string busName, string eventName, params object[] args);
        public static void SendEBusEvent(string busName, string eventName, object busId, params object[] args);
        public static T    SendResultEBusEvent<T>(string busName, string eventName, object busId, params object[] args);
    }

    // 18-B: managed handlers for any reflected bus.
    public abstract class EBusHandler<TBus> : IDisposable
    {
        public bool IsConnected { get; }
        public void Connect();
        public void Connect<TBusId>(TBusId busId);
        public void Disconnect();
        public void Dispose();
    }

    // 18-C: define managed buses.
    [AttributeUsage(AttributeTargets.Interface)]
    public sealed class EBusAttribute : Attribute
    {
        public string Name { get; set; }
        public HandlerPolicy Handler { get; set; } = HandlerPolicy.Multiple;
        public AddressPolicy Address { get; set; } = AddressPolicy.Single;
        public Type? BusIdType { get; set; }
    }

    public enum HandlerPolicy { Single, Multiple, MultipleAndOrdered }
    public enum AddressPolicy { Single, ById, ByIdAndOrdered }
}

// 18-A typed wrappers (generated per engine bus):
namespace O3DE.AzCore       // or wherever the generator places them
{
    public static class TransformBus
    {
        public static class Broadcast { ... }
        public static TransformBusEventDispatcher Event(EntityId id) => new(id);
        public abstract class Handler : EBusHandler<TransformBus> { ... }
    }
    // ... one per discovered EBus<>
}

// 18-C typed wrappers (generated per [EBus]-marked user interface):
namespace MyGame
{
    public static partial class PlayerEvents   // generated counterpart to IPlayerEvents
    {
        public static class Broadcast { ... }
        public static PlayerEventsDispatcher Event(EntityId id) => new(id);
    }
}
```

---

## 11. Out of scope (call out)

* **Queued events** (`QueueBroadcast`, `QueueEvent`). Less commonly
  used; add in Phase 18.1 if needed.
* **Async / Task-based handler methods.** Today's EBus dispatch is
  synchronous; introducing `async` introduces re-entrancy concerns
  that deserve their own design pass.
* **Cross-process / network EBuses** (replication). Out of scope —
  belongs to a future Multiplayer integration phase.
* **Performance tuning** (per-event JIT'd marshaling, NativeAOT
  compatibility for blittable fast paths). Phase 18.1.

---

## 12. Open questions for the maintainer

Before implementation starts, three decisions still need a yes/no:

1. **`O3DESharp.SourceGenerators` as a NuGet package or as a local
   ProjectReference?** NuGet is the canonical Roslyn distribution
   path but means a publish step. Local ProjectReference is simpler
   for in-gem development. The csproj template can add either; the
   choice affects how user projects pick up generator updates.

2. **Managed bus IDs other than primitives** (e.g. a struct `BusId`).
   Allowed in O3DE for native code. Phase 18 ships with primitives +
   `EntityId` only. Custom struct bus IDs would need additional
   marshaling work; we can accept them in v1 with a runtime error or
   silently restrict.

3. **`EBusHandler<TBus>` as `class` or `struct`?** Class is the
   natural choice for the inheritance pattern. A struct version with
   `ref` returns could be lower-allocation for hot-path subscribers,
   but the pattern is uglier. Recommendation: class for v1, revisit
   for Phase 18.1 if profiling demands it.

Defaults if not answered: (1) NuGet via a local feed in
`Code/Tools/BindingGenerator/nuget/`, (2) primitives + EntityId only,
(3) class.
