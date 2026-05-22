# FirstPersonController — C# Port Design

**Status:** Design accepted; ready for implementation planning.
**Author:** Mikael K. Aboagye (WD Studios Corp.), via collaborative brainstorm 2026-05-21.
**Source repo:** [Porcupine-Factory/FirstPersonController](https://github.com/Porcupine-Factory/FirstPersonController) (C++ original, MPL-2.0).
**Target:** `F:/O3DESharp/Gems/O3DE.FirstPersonController/` — standalone C# gem riding on O3DESharp v1.0.0.
**Scope:** Full behavior parity with the C++ component (PhysX-backed movement, sphere-cast grounded check, PID-driven crouch, eye-height camera coupling, full `FirstPersonControllerComponentRequestBus` surface). Single-player only; multiplayer / `NetworkFPCControllerNotificationBus` deferred to v1.1.

---

## 1. Motivation

The Porcupine-Factory FirstPersonController is the de-facto reference first-person character controller for O3DE — used by community tutorials, learning resources, and the O3DE Foundation's book. It is implemented entirely in C++ (~96% by line count) and depends on PhysX, StartingPointInput, and the Camera framework.

With Phase 18-E (managed handlers) shipped, C# in O3DESharp can now author full `[EBus]` handlers and call any reflected BehaviorContext API. That makes a C# port of FPC genuinely feasible — and a port is the highest-leverage demonstration that the gem is production-ready: it builds the canonical character-controller-on-PhysX example, validates the entire generated-bindings + managed-handler pipeline against a non-trivial consumer, and gives every future O3DESharp user a working first-person template to start from.

A port also stress-tests the Phase 18-E2 dispatch shim against a real ~90-method request bus surface, which is significantly larger than anything the existing test suite exercises.

---

## 2. Goals & non-goals

**Goals:**

- **Behavioral parity** with the C++ FPC: same movement model, same default tunings, same observable feel.
- **API parity** for the external request bus: existing Lua / ScriptCanvas / other-gem consumers of `FirstPersonControllerComponentRequestBus` keep working unchanged against the C# component.
- **Idiomatic C#** internals: properties not getter/setter pairs, enum-based state machine, `PidController1D` helper class, `[ExposedProperty]` inspector surface, zero per-frame allocations.
- **Standalone gem distribution**: drop-in for any O3DE project that has O3DESharp + the required binding gems installed.
- **Unit + integration tests** shipped from v1.

**Non-goals:**

- Multiplayer (the `#ifdef NETWORKFPC` block in the original). Spec'd as a separate v1.1 follow-up gem `O3DE.FirstPersonController.Multiplayer`.
- A C++ system component. The system-level state (defaults, registration) is collapsed into a static C# class with a `[ModuleInitializer]`.
- Translation of the upstream FPC's C++ tests. We write C# unit tests for the C# code rather than porting the upstream test suite line-by-line.
- Tracking every upstream FPC change automatically. The port is anchored against the upstream `main` HEAD as of 2026-05-21; future divergence is a manual port effort and that trade-off is accepted.

---

## 3. Architecture

### 3.1 Project layout

```
F:/O3DESharp/Gems/O3DE.FirstPersonController/
├── O3DE.FirstPersonController.csproj          # net9.0, refs O3DE.Core only
├── README.md                                   # install + usage + tuning reference
├── gem.json                                    # O3DE Project Manager discovery
│
├── Components/
│   ├── FirstPersonControllerComponent.cs       # main player component (~900 lines)
│   ├── CameraCoupledChildComponent.cs          # camera-entity coupling (~300 lines)
│   ├── FirstPersonExtrasComponent.cs           # lean, headbob, footstep (~200 lines)
│   └── FirstPersonControllerSystem.cs          # static init + shared defaults (~50 lines)
│
├── Internal/
│   ├── PidController1D.cs                      # scalar PID, anti-windup
│   ├── MovementState.cs                        # state enum + transition table
│   ├── GroundCheck.cs                          # sphere-cast wrapper
│   ├── InputAxisAccumulator.cs                 # named-event → axis-value buffer
│   └── MovementTuning.cs                       # tuning-parameter struct
│
├── Bindings/
│   └── DefaultInputBindings.inputbindings      # sample binding asset users can drop in
│
├── Tests/
│   ├── O3DE.FirstPersonController.Tests.csproj      # xUnit
│   ├── PidController1DTests.cs
│   ├── MovementStateTransitionTests.cs
│   ├── InputAxisAccumulatorTests.cs
│   ├── GroundCheckTests.cs
│   └── MANUAL_TESTS.md
│
└── SmokeTests/
    ├── O3DE.FirstPersonController.SmokeTests.csproj # console app
    └── Program.cs                                    # 1000-tick golden-trajectory test
```

The csproj is built directly via `dotnet build`. The accompanying `gem.json` exists for O3DE Project Manager's gem-discovery flow; no CMake is required for the C# side.

Distribution: ships from `F:/O3DESharp/` in v1. Migration to a sibling repo (`github.com/WatchDogStudios/O3DESharp.FirstPersonController`) is `git filter-repo` away if the gem stabilises and needs its own release cadence.

### 3.2 Component breakdown

#### `FirstPersonControllerComponent` — the brain

A `ScriptComponent` subclass. Owns all per-frame movement logic. Does NOT own the camera entity or the input binding asset — both are consumed through bus interfaces.

| Responsibility | Backed by |
|---|---|
| Read input axes + button states | `[EBus("InputEventNotificationBus")]` handlers (one per named event: Forward, Back, Left, Right, Yaw, Pitch, Sprint, Crouch, Jump). Each accumulates into an `InputAxisAccumulator` consumed once per frame in `OnUpdate`. |
| Drive PhysX character movement | `GenEBus.CharacterControllerRequestBus.AddVelocity(EntityId, Vector3)` per frame; ground state read via the same bus. |
| Run the state machine | `MovementState` enum (`Idle / Walking / Sprinting / Crouching / Jumping / Falling`) with transitions in `MovementState.cs`. |
| Drive crouch height | `PidController1D` member, tuned by inspector params. Target height swaps when crouch state changes. |
| Sphere-cast grounded detection | `GroundCheck.cs` helper; uses reflected PhysX scene-query APIs. |
| External query/control API | Mirrors C++ `FirstPersonControllerComponentRequestBus`: ~90 `[EBusHandler]` methods routing into backing fields. Phase 18-E2's dispatch shim handles JSON arg unmarshaling. |

Internal state lives as plain fields. The request-bus handlers are thin wrappers that read/write those fields — they never call into the state machine directly. This keeps the bus surface as an external contract layer without leaking it into per-frame logic.

**Inspector defaults**: every `[ExposedProperty]` on `FirstPersonControllerComponent` matches the upstream FPC default value at `main` HEAD as of 2026-05-21 (e.g. `WalkSpeed = 5.0f`, `Gravity = -30.0f`, `EyeHeight = 1.6f`, `JumpInitialVelocity = 6.0f`, `MaxGroundedAngleDegrees = 30.0f`, `CrouchDistance = 0.5f`, etc.). The full table of names + defaults + valid-range clamps is documented in the gem's README and as XML doc comments on each property; the spec deliberately doesn't enumerate them here to avoid a copy that drifts from the source.

#### `CameraCoupledChildComponent` — the camera follower

Lives on a child camera entity. Subscribes to its parent's `FirstPersonControllerComponentRequestBus` to read eye height + crouch state. Updates its local position each frame: `LocalPosition = (0, 0, parent.EyeHeight - crouchOffset)`. Pitch comes from accumulated mouse delta (read via the same input bus, separate event handler).

Depends on: the parent entity having a `FirstPersonControllerComponent`. Validates in `OnCreate`; logs `[FPC] CameraCoupledChildComponent: parent has no FirstPersonControllerComponent` and disables itself if not.

#### `FirstPersonExtrasComponent` — the polish

Optional add-on. Three independent sub-features, each toggleable via `[ExposedProperty]`:
- **Lean** — listens for a "Lean" axis input event, applies a small roll offset to the parent camera entity.
- **Headbob** — sine-wave Z offset on the camera entity, amplitude scaled by current movement speed.
- **Footstep notification** — emits `PlayerFootstepNotificationBus.OnFootstep(int footIndex)` at sprint/walk-tuned intervals; downstream gems play sounds without reaching into FPC.

Zero dependencies on the controller's internal state machine — reads everything via the request bus. Sub-features fail in isolation.

#### `FirstPersonControllerSystem` — static init

Static class with a `[ModuleInitializer]` that registers a single shared `MovementTuning` defaults instance and a `PlayerFootstepNotificationBus` declaration. Designers can override the defaults per-project via a `.setreg` entry without per-entity changes.

### 3.3 Dependency graph

```
                  ┌─────────────────────┐
                  │ FirstPersonController│
                  │   SystemComponent    │  static defaults +
                  └──────────┬───────────┘  bus declarations
                             │
                             ▼
   ┌──────────────────────────────────────────────────────────┐
   │  FirstPersonControllerComponent (ScriptComponent)         │
   │   - Internal: MovementState, PidController1D, GroundCheck,│
   │     InputAxisAccumulator                                  │
   │   - External: FirstPersonControllerComponentRequestBus    │
   └──────────────┬───────────────────────┬───────────────────┘
                  │ via request bus       │ via request bus
                  ▼                       ▼
   ┌──────────────────────┐   ┌──────────────────────────────┐
   │ CameraCoupledChild   │   │ FirstPersonExtrasComponent   │
   │     Component        │   │  (Lean, Headbob, Footstep)   │
   └──────────────────────┘   └──────────────────────────────┘
```

---

## 4. Data flow

### 4.1 Per-frame tick sequence

```
OnUpdate(dt)
  │
  ├─ 1. Drain input accumulator
  │     (events pushed since last tick: forward/back/left/right axes,
  │      yaw/pitch deltas, sprint/crouch/jump button states)
  │
  ├─ 2. Update MovementState
  │     transition table inputs: { grounded?, sprintHeld, crouchHeld,
  │       jumpPressed, anyMoveAxisNonzero, jumpHoldElapsed }
  │     fires onEnter/onExit hooks (e.g. enter Jumping → apply impulse,
  │       enter Crouching → set PID target = crouchedHeight)
  │
  ├─ 3. Run subsystems in fixed order:
  │     a. GroundCheck.Update(dt)     → updates m_grounded, m_slopeNormal
  │     b. UpdateRotation(dt)         → yaw on entity, pitch on camera child
  │     c. UpdateHorizontalVelocity   → accel toward target speed * direction
  │     d. UpdateVerticalVelocity     → integrate gravity OR apply jump impulse
  │     e. CrouchPid.Step(dt)         → eyeHeight ← PID toward target
  │     f. Apply final velocity       → CharacterControllerRequestBus.AddVelocity
  │
  ├─ 4. Publish state to bus consumers
  │     (camera child + extras read on next frame)
  │
  └─ 5. Reset per-frame input edge flags (jumpPressed, etc.)
```

Per-frame allocations: zero. Scratch vectors are member fields. PID controller is a struct member, no GC pressure.

### 4.2 MovementState transition table

```
state         → on
─────────────────────────────────────────────────────────
Idle          → Walking   if anyMoveAxisNonzero
              → Falling   if !grounded
              → Jumping   if jumpPressed && grounded

Walking       → Sprinting if sprintHeld && !crouchHeld
              → Crouching if crouchHeld
              → Falling   if !grounded
              → Jumping   if jumpPressed && grounded
              → Idle      if !anyMoveAxisNonzero

Sprinting     → Walking   if !sprintHeld || crouchHeld
              → (jump/fall: same as Walking)

Crouching     → Walking   if !crouchHeld && canStandUp
              → Falling   if !grounded
              (jump blocked while crouching)

Jumping       → Falling   when verticalVelocity < 0 || jumpHoldElapsed
              (state has its own held duration)

Falling       → Idle      when grounded && !anyMoveAxisNonzero
              → Walking   when grounded && anyMoveAxisNonzero
```

`canStandUp` is determined by a one-shot upward sphere-cast at crouch-exit attempt time; if the ceiling-clearance check fails, the state stays `Crouching` until the player releases the crouch input AND the clearance becomes available.

---

## 5. Error handling & failure modes

| Failure | Detection | Response |
|---|---|---|
| No `CharacterController` component on entity | `OnCreate` queries the bus, gets null | Log error, disable component (`OnUpdate` becomes a no-op early-return) |
| `InputEventNotificationBus` not reflected (no binding regen) | `Register` throws | Log warning, component falls back to `O3DE.Input.GetAxis` polling |
| Bus query returns unexpected type (numeric coercion edge case) | `try/catch` around each `BroadcastResultEBusEvent<T>` | Log warning, use last-known-good value for that frame |
| Camera child component on entity without `FirstPersonControllerComponent` parent | `OnCreate` walks parent chain | Log error, disable component |
| Extras component without a parent FPC | Same as above | Log error, disable each sub-feature independently |
| Designer sets `WalkSpeed ≤ 0` or any inspector value to a nonsensical extreme | Property setter clamps | Clamp to documented `[min, max]` range (table in README); log once at the entity-instance level if clamped (avoid per-frame spam) |
| Crouch PID Kp/Ki/Kd produce unstable response | None at runtime; tunable | Documented in README with sane defaults; `PidController1D` clamps integral term to prevent windup |

Every component logs with the `[FPC]` prefix so users can grep the editor log. No exceptions are allowed to escape `OnUpdate` — an outer `try/catch` logs and continues, preventing one bad frame from disabling the whole component for the rest of the session.

---

## 6. Testing

### 6.1 Unit tests (xUnit)

Project `Tests/O3DE.FirstPersonController.Tests.csproj`. Runs via the same test harness `BindingGenerator.Tests` already uses; picked up automatically by `.teamcity/settings.kts`.

| Test class | Coverage |
|---|---|
| `PidController1DTests` | Step response, anti-windup, sign correctness, stability at extreme Kp |
| `MovementStateTransitionTests` | Table-driven: every (state, input) pair produces the documented next state |
| `InputAxisAccumulatorTests` | Press/release event sequences produce correct axis values; edge events (`Pressed` / `Released` vs `Held`) flagged correctly |
| `GroundCheckTests` | Sphere-cast result interpretation (slope angle within limit → grounded; otherwise → falling); sphere-cast results are stubbed via a fake `IPhysicsQueryService` interface |

### 6.2 Integration test

`O3DE.FirstPersonController.SmokeTests.csproj` — a console-app that instantiates the components with mocked bus calls and runs 1000 ticks of synthetic input. Asserts velocity and position trajectories match a golden reference. Cheap regression gate for "the port still feels right" without spinning up the editor.

### 6.3 Manual editor test plan

`Tests/MANUAL_TESTS.md` with checkboxes:
- Spawn + first-frame stability
- Forward / back / strafe
- Sprint while moving
- Crouch (verify can't stand under low ceiling)
- Jump (single)
- Jump (double, if `DoubleJumpEnabled = true`)
- Camera follow during all of the above
- Lean (Q/E roll)
- Headbob amplitude scales with speed
- Footstep event cadence at walk vs sprint

Run before each release tag.

### 6.4 CI integration

`.teamcity/settings.kts` already runs every `*.Tests.csproj` in the repo. New test csprojs land automatically on next commit; no CI changes required.

---

## 7. Implementation milestones

Even though the port ships as one design, the implementation is broken into two milestones to keep each reviewable:

**M1 — Core (mandatory for v1):**
1. Project scaffolding: csproj + gem.json + folder structure
2. `MovementTuning`, `PidController1D`, `MovementState`, `InputAxisAccumulator`, `GroundCheck` (the pure-logic Internal/ classes) + their unit tests
3. `FirstPersonControllerComponent` minus the 90-method request bus surface — get walking, sprinting, jumping, falling, grounded, camera-coupling to a child entity working
4. Regen project bindings to include `InputEventNotificationBus`; verify the named-event input model is live
5. Smoke test passes

**M2 — Polish + parity (also v1):**
1. Crouch state + PID-driven height transition + `canStandUp` check
2. Full request-bus surface (~90 `[EBusHandler]` methods)
3. `CameraCoupledChildComponent` (extracts camera-follow code that was inline in M1 component)
4. `FirstPersonExtrasComponent` — lean / headbob / footstep
5. `FirstPersonControllerSystem` static init
6. README + manual-test doc + sample `.inputbindings` asset

Each milestone gets its own implementation plan (via the `writing-plans` skill). M2 only starts after M1's smoke test and unit tests are green.

---

## 8. Risk inventory

| Risk | Likelihood | Mitigation |
|---|---|---|
| `CharacterControllerRequestBus` per-frame call overhead (JSON envelope marshal × ~5 calls per tick) hurts frame time | Medium | Profile early; if hot, add direct internal-call shortcut in O3DESharp for the most-hit methods (`AddVelocity`, `IsGrounded`) bypassing the JSON envelope. |
| `InputEventNotificationBus` binding regen breaks other consumers | Low | Regen on a branch first; CI's smoke + xUnit suite catches breakage before merge |
| The 90-method request bus surface multiplies maintenance cost | Medium | Mitigated by the surface being thin wrappers (~1-2 lines each); changes to internal state shape only touch the wrappers' bodies, not the bus contract |
| Upstream FPC adds a new feature we don't track | High over time | Accepted trade-off (non-goal §2). Versioned as "port of FPC main as of 2026-05-21" in the README. |
| Multiplayer integration request from a user | Medium | Documented in §2 non-goals; spec'd as separate v1.1 follow-up gem |
| Crouch PID instability under low frame rate | Low–Medium | `PidController1D` includes step-size clamp and integral wind-up clamp; defaults conservatively tuned; documented in README |

---

## 9. Open questions

None. All design decisions resolved during brainstorming on 2026-05-21:
- Fidelity: full parity
- Location: standalone gem at `F:/O3DESharp/Gems/O3DE.FirstPersonController/`
- Input model: named events via `[EBus]` handler on `InputEventNotificationBus`
- Auxiliary components: Camera + Extras + System (multiplayer excluded)
- Implementation approach: Hybrid (idiomatic C# internals + bus-parity external API)
- Tests included in v1

---

## 10. Acceptance criteria

The port is considered complete when ALL of the following hold:

1. `O3DE.FirstPersonController.dll` builds clean (zero warnings, zero errors) under `dotnet build -c Release`.
2. `O3DE.FirstPersonController.Tests` passes 100% (all xUnit cases).
3. `O3DE.FirstPersonController.SmokeTests` runs 1000 synthetic ticks against the components with mocked buses, with trajectory output matching the golden reference within ±1% tolerance.
4. Manual test plan (`Tests/MANUAL_TESTS.md`) executed and signed off in the editor with at least one entity demonstrating each feature.
5. README documents install + setup + `.inputbindings` import + tuning reference.
6. The gem's `gem.json` is registerable via O3DE Project Manager.
7. CI (TeamCity Cloud) reports the new test csprojs green on every push to `main` and `development`.
