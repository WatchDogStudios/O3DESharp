# O3DE FirstPersonController — C# Port (v0.1, M1)

C# port of [Porcupine-Factory/FirstPersonController](https://github.com/Porcupine-Factory/FirstPersonController) riding on the [O3DESharp](../..) gem.

**Status — M1 (this release):** walking, sprinting, jumping, falling, grounded detection, PhysX-backed movement, named-event input.

**Coming in M2:** crouch with PID-driven height transition, the full ~90-method `FirstPersonControllerComponentRequestBus` surface, `CameraCoupledChildComponent`, `FirstPersonExtrasComponent` (lean / headbob / footstep).

**Not in v1:** Multiplayer / network replication.

---

## Requirements

- O3DE with O3DESharp v1.0.0+ enabled
- PhysX5 gem enabled (provides `CharacterControllerRequestBus`)
- StartingPointInput gem enabled (provides `InputEventNotificationBus` + the `.inputbindings` asset pipeline)
- .NET 9.0 SDK + Runtime

## Install

1. Clone or copy this gem next to the O3DESharp gem in your engine's gems folder.
2. Register the gem: `o3de register --gem-path Gems/O3DE.FirstPersonController`
3. Enable it on your project: `o3de enable-gem --gem-name O3DE.FirstPersonController --project-path <your project>`
4. Build the C# library: `cd Gems/O3DE.FirstPersonController && dotnet build -c Release`
5. The MSBuild target deploys `O3DE.FirstPersonController.dll` next to `O3DE.Core.dll` in your project's `Bin/Scripts/` folder.

## Usage

1. Create a player entity in your level.
2. Attach the following components:
   - **PhysX Character Controller** (must come before the CSharp Script component)
   - **CSharp Script**, with Script Class set to `O3DE.FirstPersonController.Components.FirstPersonControllerComponent`
3. Set up the input bindings: drop a `.inputbindings` asset on the entity (StartingPointInput's Input component) that maps keys → the following event names:
   - `Forward`, `Back`, `Left`, `Right` (analog/digital axes)
   - `Yaw`, `Pitch` (mouse/stick deltas)
   - `Sprint`, `Crouch` (hold buttons)
   - `Jump` (press)
4. Hit play.

## Tunables (inspector-editable)

| Property | Default | What it does |
|---|---|---|
| Walk Speed | 5.0 m/s | Top horizontal speed when not sprinting |
| Acceleration | 30.0 m/s² | Horizontal accel toward target speed |
| Deceleration | 1.5 /s | Horizontal damping factor per second |
| Sprint Multiplier | 1.5× | WalkSpeed × this while Sprint is held |
| Gravity | 30.0 m/s² | Downward acceleration (positive value) |
| Jump Initial Velocity | 6.0 m/s | Upward velocity applied on jump press |
| Jump Hold Distance | 0.8 m | m above start Z where jump-hold ends |
| Max Grounded Angle | 30° | Slope above which character cannot stand |
| Character Radius | 0.4 m | Used by the sphere-cast ground check |
| Eye Height | 1.6 m | Standing eye height; used by camera child in M2 |

Defaults match upstream FPC at main HEAD on 2026-05-21.

## Known Limitations (M1)

- **Input delivery:** `InputEventNotificationBus` is a multi-address bus (one address per event channel). The Phase 18-E source generator emits a single `Connect()` call per `[EBus]` attribute, subscribing to the broadcast channel. Depending on how your `.inputbindings` asset is configured, events may or may not arrive. A v0.2 work item extends the source generator to emit `MultiHandler`-style multi-address subscription.
- **Crouch:** `MovementState.Crouching` is in the state machine but the transition arms are not yet wired (need the overhead-clearance check and PID eye-height — both M2).
- **Camera:** No camera coupling in M1. Position a child camera entity manually at the desired eye height.
- **Request bus:** The `FirstPersonControllerComponentRequestBus` (~90 getter/setter pairs) is an M2 deliverable.

## Architecture

```
Components/
  FirstPersonControllerComponent.cs  — ScriptComponent subclass; lifecycle + update loop
Internal/
  PidController1D.cs         — scalar PID for crouch height (M2 wires this)
  MovementState.cs           — state enum + pure transition function
  InputAxisAccumulator.cs    — buffers [EBusHandler] input across a frame
  GroundCheck.cs             — sphere-cast grounded detection
  IPhysicsQueryService.cs    — physics abstraction (injectable in unit tests)
  MovementTuning.cs          — grouped tuning defaults
```

All `Internal/` types are unit-tested (xUnit + FluentAssertions). The `ScriptComponent` lifecycle is exercised by the `SmokeTests/` golden-trajectory console app.

## License

Apache-2.0 OR MIT, matching the parent O3DESharp gem.
