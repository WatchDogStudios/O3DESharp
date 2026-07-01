# O3DESharp — C# Scripting Guide

This guide covers everything you need to write C# scripts in O3DE, from setting up
your first project to accessing components, physics, the reflection API, and more.

---

## Table of Contents

1. [Quick Start](#1-quick-start)
2. [Writing Script Components](#2-writing-script-components)
3. [Debug Logging](#3-debug-logging)
4. [Entity & Transform](#4-entity--transform)
5. [Math Types — Vector3 & Quaternion](#5-math-types--vector3--quaternion)
6. [Time](#6-time)
7. [Physics (Raycasting)](#7-physics-raycasting)
8. [Accessing Any O3DE Component via Reflection](#8-accessing-any-o3de-component-via-reflection)
9. [Calling EBus Events from C#](#9-calling-ebus-events-from-c)
10. [Input (Current State)](#10-input-current-state)
11. [Hot Reload](#11-hot-reload)
11b. [Debugging C# Scripts](#11b-debugging-c-scripts)
12. [Binding Generator](#12-binding-generator)
13. [Building & Deploying](#13-building--deploying)
14. [Project Export](#14-project-export)
15. [Known Limitations](#15-known-limitations)

---

## 1. Quick Start

### Prerequisites

- **.NET 9.0 SDK** installed (`dotnet --list-sdks` should show a 9.x).
- **.NET 9.0 Runtime** installed (`dotnet --list-runtimes` should show
  `Microsoft.NETCore.App 9.0.x`). Coral.Managed's runtimeconfig pins to
  net9.0 with `rollForward: LatestMinor`, so a machine with only
  .NET 10 installed will fail to host.
- O3DE Editor built with the **O3DESharp** Gem enabled.

### Create a C# Project

**Recommended:** Tools → C# Scripting → **Create C# Project…**. The
editor's project manager generates the csproj from a template that
already declares `net9.0`, references `O3DE.Core` correctly, and
includes the Phase 16b `DeployToBinScripts` MSBuild target that
auto-deploys to `<ProjectPath>/Bin/Scripts/` on every build.

**Manual (if you must):** lay out the project under
`<YourProject>/Gem/Source/CSharp/<MyGame>/` (canonical, matches the
template) and use this csproj:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <OutputPath>bin/$(Configuration)</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <!-- Phase 16b deploy target -->
    <O3DEDeployPath Condition="'$(O3DEDeployPath)' == ''">$(MSBuildProjectDirectory)\..\..\..\..\Bin\Scripts</O3DEDeployPath>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="O3DE.Core">
      <HintPath>$(O3DEDeployPath)\O3DE.Core.dll</HintPath>
    </Reference>
    <Reference Include="Coral.Managed">
      <HintPath>$(O3DEDeployPath)\Coral\Coral.Managed.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

  <Target Name="DeployToBinScripts" AfterTargets="Build">
    <MakeDir Directories="$(O3DEDeployPath)"/>
    <Copy SourceFiles="$(TargetPath)"
          DestinationFolder="$(O3DEDeployPath)"
          SkipUnchangedFiles="true"
          ContinueOnError="true"/>
    <Copy SourceFiles="$(TargetDir)$(AssemblyName).pdb"
          DestinationFolder="$(O3DEDeployPath)"
          SkipUnchangedFiles="true"
          ContinueOnError="true"
          Condition="Exists('$(TargetDir)$(AssemblyName).pdb')"/>
  </Target>

</Project>
```

> If you created the csproj before Phase 16b shipped, just run
> Tools → C# Scripting → **Migrate C# Project Files** to add the deploy
> target. The migration writes a `*.pre-deploy-target.bak` backup first.

### Build

```bash
cd <YourProject>/Gem/Source/CSharp/MyGame
dotnet build -c Debug
```

The `DeployToBinScripts` target copies `MyGame.dll` + `MyGame.pdb` to
`<YourProject>/Bin/Scripts/` automatically — no manual copy step. The
editor's file watcher (Phase 16a) then picks up the change and dispatches
a reload.

Register the assembly with the runtime via a setreg under
`<ProjectPath>/Registry/`:

```json
{ "O3DE": { "O3DESharp": { "UserAssemblies": [
    { "AssemblyName": "MyGame.dll" }
]}}}
```

The runtime resolves each entry against `<ProjectPath>/Bin/Scripts/`, so
the `AssemblyName` field is just the filename. Multiple assemblies can
be listed — they all share one assembly load context and can reference
each other.

### Add a Script to an Entity

1. In the O3DE Editor, select an entity.
2. **Add Component → Scripting → C# Script Component**
3. Type the full class name (e.g. `MyGame.GameScript`) into the **Script Class**
   field, or click **Browse Scripts...** to pick from discovered classes.
4. Enter Game Mode (**Ctrl+G**).

---

## 2. Writing Script Components

Every C# script that attaches to an entity inherits from `O3DE.ScriptComponent`:

```csharp
using O3DE;

namespace MyGame
{
    public class PlayerController : ScriptComponent
    {
        // Called once when the component activates
        public override void OnCreate()
        {
            Debug.Log($"PlayerController created on entity {Name} (ID: {EntityId})");
        }

        // Called every frame
        public override void OnUpdate(float deltaTime)
        {
            // Move forward at 5 units/sec
            Transform.Translate(Transform.Forward * 5f * deltaTime);
        }

        // Called when the entity's transform changes
        public override void OnTransformChanged()
        {
            Debug.Log($"Position is now {Transform.Position}");
        }

        // Called when the component deactivates
        public override void OnDestroy()
        {
            Debug.Log("PlayerController destroyed");
        }
    }
}
```

### Lifecycle Methods

| Method | When It's Called |
|--------|-----------------|
| `OnCreate()` | Component activation (game start, entity spawn) |
| `OnUpdate(float deltaTime)` | Every frame |
| `OnTransformChanged()` | Entity's transform was modified |
| `OnEnable()` | Component re-enabled after being disabled |
| `OnDisable()` | Component disabled |
| `OnDestroy()` | Component deactivation (game stop, entity destroyed) |

### Built-in Properties

| Property | Type | Description |
|----------|------|-------------|
| `Entity` | `Entity` | The entity this script lives on |
| `Transform` | `Transform` | Shortcut to the entity's transform |
| `EntityId` | `ulong` | Raw native entity ID |
| `Name` | `string` | Entity display name |
| `IsActive` | `bool` | Whether the entity is active |

### Convenience Methods

```csharp
Log("hello");               // same as Debug.Log(...)
LogWarning("careful");
LogError("oops");
HasComponent("MeshComponent"); // check if entity has a native component
ActivateEntity();
DeactivateEntity();
```

---

## 3. Debug Logging

The `Debug` class writes to the O3DE console and log file:

```csharp
using O3DE;

Debug.Log("This is info");
Debug.LogWarning("Something unexpected");
Debug.LogError("Something failed");

Debug.Log("Player {0} scored {1} points", playerName, score);  // formatted
Debug.LogException(ex);           // logs type, message, and stack trace
Debug.Assert(health > 0, "Health must be positive");  // debug-only assertion

Debug.LogDebug("Only in debug builds");               // stripped in Release
Debug.LogWithCaller("auto-includes file/line/method"); // source-location logging
```

---

## 4. Entity & Transform

### Entity

```csharp
Entity entity = Entity;                      // the entity this script is on
bool valid    = entity.IsValid;
string name   = entity.Name;
entity.Name   = "Player";
bool active   = entity.IsActive;

entity.Activate();
entity.Deactivate();

// Look up another entity by ID
Entity? other = Entity.FromId(someEntityId);

// Check for native components
if (entity.HasComponent("PhysicsRigidBodyComponent"))
{
    Debug.Log("This entity has physics!");
}
```

### Transform

O3DE uses a **right-handed coordinate system**: **X = Right, Y = Forward, Z = Up**.

```csharp
Transform t = Transform;

// Position (world space)
Vector3 pos = t.Position;
t.Position  = new Vector3(10, 0, 5);

// Local position (relative to parent)
Vector3 local = t.LocalPosition;
t.LocalPosition = Vector3.Zero;

// Rotation
Quaternion rot = t.Rotation;
t.Rotation = Quaternion.FromEuler(0, 90, 0);  // degrees

Vector3 euler = t.EulerAngles;                 // read as degrees
t.EulerAngles = new Vector3(0, 45, 0);

// Scale
t.LocalScale    = new Vector3(2, 2, 2);
t.UniformScale  = 1.5f;

// Direction vectors
Vector3 fwd   = t.Forward;   // +Y
Vector3 right = t.Right;     // +X
Vector3 up    = t.Up;        // +Z

// Movement helpers
t.Translate(new Vector3(1, 0, 0));            // world-space offset
t.TranslateLocal(new Vector3(0, 5, 0));       // local-space (forward)

// Rotation helpers
t.Rotate(new Vector3(0, 0, 45));              // yaw 45° (degrees)
t.RotateAround(Vector3.Up, 90);              // rotate around axis
t.LookAt(new Vector3(100, 200, 0));          // face a position

// Space conversion
Vector3 worldPt  = t.TransformPoint(localOffset);
Vector3 localPt  = t.InverseTransformPoint(worldPosition);
Vector3 worldDir = t.TransformDirection(Vector3.Forward);

// Hierarchy
Entity? parent = t.Parent;
t.SetParent(otherEntity);
t.ClearParent();
```

---

## 5. Math Types — Vector3 & Quaternion

### Vector3

```csharp
var v = new Vector3(1, 2, 3);

// Statics
Vector3.Zero;              // (0, 0, 0)
Vector3.One;               // (1, 1, 1)
Vector3.Forward;           // (0, 1, 0)   — Y axis
Vector3.Right;             // (1, 0, 0)   — X axis
Vector3.Up;                // (0, 0, 1)   — Z axis

// Properties
float len  = v.Magnitude;
float sqr  = v.SqrMagnitude;
Vector3 n  = v.Normalized;

// Operations
float d     = Vector3.Dot(a, b);
Vector3 c   = Vector3.Cross(a, b);
float dist  = Vector3.Distance(a, b);
Vector3 mid = Vector3.Lerp(a, b, 0.5f);
float angle = Vector3.Angle(a, b);         // degrees
Vector3 p   = Vector3.Project(a, normal);
Vector3 r   = Vector3.Reflect(direction, normal);
Vector3 cl  = Vector3.ClampMagnitude(v, 10f);

// Arithmetic
Vector3 sum  = a + b;
Vector3 diff = a - b;
Vector3 neg  = -a;
Vector3 sc   = a * 2f;        // scalar multiply
Vector3 comp = a * b;         // component-wise multiply
```

### Quaternion

```csharp
Quaternion q = Quaternion.Identity;

// Create from euler angles (degrees)
q = Quaternion.FromEuler(pitch, yaw, roll);
q = Quaternion.FromEuler(new Vector3(0, 90, 0));

// Create from axis + angle
q = Quaternion.AngleAxis(45f, Vector3.Up);

// Direction-based
q = Quaternion.LookRotation(forwardDirection);
q = Quaternion.FromToRotation(Vector3.Forward, targetDir);

// Interpolation
q = Quaternion.Slerp(a, b, t);       // smooth rotation blend (0..1)

// Combine rotations
Quaternion combined = parentRot * childRot;

// Rotate a point
Vector3 rotated = q * point;          // same as q.RotatePoint(point)

// Read back as euler (degrees)
Vector3 angles = q.EulerAngles;

// Angle between two rotations (degrees)
float diff = Quaternion.Angle(a, b);
```

---

## 6. Time

```csharp
using O3DE;

float dt    = Time.DeltaTime;         // seconds since last frame
float total = Time.TotalTime;         // seconds since app start
ulong frame = Time.FrameCount;        // current frame number
float fps   = Time.FPS;               // approx frames per second

// Time scale (slow-mo / pause)
Time.TimeScale = 0.5f;                // half speed
float scaled   = Time.ScaledDeltaTime; // DeltaTime * TimeScale

Time.Pause();                          // TimeScale = 0
Time.Resume();                         // TimeScale = 1
bool paused = Time.IsPaused;

// Utility
float smooth = Time.SmoothDamp(current, target, speed);
float osc    = Time.PingPong(speed);
bool elapsed = Time.HasElapsed(startTime, duration);
float pct    = Time.Progress(startTime, duration);   // 0..1
```

---

## 7. Physics (Raycasting)

```csharp
using O3DE;

// Basic raycast
RaycastHit hit = Physics.Raycast(origin, direction, maxDistance: 100f);
if (hit.Hit)
{
    Debug.Log($"Hit at {hit.Point}, normal {hit.Normal}, dist {hit.Distance}");
    Entity? hitEntity = hit.GetEntity();
}

// Quick boolean check
if (Physics.RaycastCheck(origin, direction))
    Debug.Log("Something is in the way");

// Out-parameter style
if (Physics.Raycast(origin, direction, out RaycastHit result))
    Debug.Log($"Hit: {result.Point}");

// Line-of-sight between two points
if (Physics.HasLineOfSight(pointA, pointB))
    Debug.Log("Clear line of sight");

// Between two entities
if (Physics.HasLineOfSight(entityA, entityB))
    Debug.Log("Entities can see each other");

// Ground check — casts a ray downward from a position
RaycastHit ground = Physics.GroundCheck(position, maxDist: 50f);
if (ground.Hit)
    Debug.Log($"Ground at Z={ground.Point.Z}");

if (Physics.GetGroundHeight(position, out float groundZ))
    Debug.Log($"Ground height: {groundZ}");

// Cast forward from an entity's facing direction
RaycastHit fwdHit = Physics.RaycastForward(Entity, maxDistance: 20f);

// Distance to nearest obstacle
float dist = Physics.DistanceToNearest(origin, direction, maxDist: 100f);
```

### RaycastHit Fields

| Field | Type | Description |
|-------|------|-------------|
| `Hit` | `bool` | Whether the ray hit anything |
| `Point` | `Vector3` | World-space hit position |
| `Normal` | `Vector3` | Surface normal at the hit |
| `Distance` | `float` | Distance from ray origin |
| `EntityId` | `ulong` | The hit entity's native ID |
| `GetEntity()` | `Entity?` | Wrapped entity (null if invalid) |

---

## 8. Accessing Any O3DE Component via Reflection

The **Reflection API** (`O3DE.Reflection.NativeReflection`) gives you runtime
access to *every* class, method, property, and EBus that O3DE has reflected to its
`BehaviorContext`. This means you can interact with components you don't have
direct bindings for — meshes, audio, AI, custom Gem components, etc.

### Discovering What's Available

```csharp
using O3DE.Reflection;

// List all reflected class names
IReadOnlyList<string> classes = NativeReflection.GetClassNames();
foreach (string cls in classes)
    Debug.Log(cls);  // "TransformComponent", "MeshComponent", "AudioTriggerComponent", ...

// Check if a class exists
if (NativeReflection.ClassExists("RigidBodyComponent"))
    Debug.Log("Physics is available");

// List methods on a class
IReadOnlyList<string> methods = NativeReflection.GetMethodNames("AudioTriggerComponent");
foreach (string m in methods)
    Debug.Log($"  method: {m}");

// List properties on a class
IReadOnlyList<string> props = NativeReflection.GetPropertyNames("MeshComponent");
foreach (string p in props)
    Debug.Log($"  property: {p}");
```

### Creating and Using Native Objects

```csharp
// Create an instance of a reflected class
using NativeObject obj = NativeReflection.CreateInstance("Vector3", 1.0f, 2.0f, 3.0f);

// Invoke methods on it
object? result = obj.InvokeMethod("GetLength");
Debug.Log($"Length: {result}");

// Read/write properties
float? x = obj.GetProperty<float>("x");
obj.SetProperty("y", 10.0f);

// Static methods
object? dotResult = NativeReflection.InvokeStaticMethod("Vector3", "CreateAxisX", 5.0f);
```

### Accessing Components on an Entity

Because O3DE exposes components through EBuses, the typical pattern is to use
EBus events to interact with components on a specific entity:

```csharp
// Get the world position of entity 42 via TransformBus
object? pos = NativeReflection.SendEBusEvent(
    "TransformBus",         // EBus name
    "GetWorldTranslation",  // event name
    EntityId                // target entity ID
);
Debug.Log($"Position via EBus: {pos}");

// Set an entity's position
NativeReflection.SendEBusEvent(
    "TransformBus",
    "SetWorldTranslation",
    EntityId,
    new Vector3(10, 20, 30)   // the new position
);
```

### Calling Global / Free Functions

```csharp
// Some gems register free functions on the BehaviorContext
object? result = NativeReflection.InvokeGlobalMethod("SomeFreeFunction", arg1, arg2);
```

### Supported Marshalling Types

Values are marshalled as JSON between C# and C++. Supported types:

| C# Type | Notes |
|---------|-------|
| `bool`, `int`, `long`, `float`, `double` | Primitive types |
| `string` | |
| `Vector3` | Serialized as `{x, y, z}` |
| `Quaternion` | Serialized as `{x, y, z, w}` |
| `Entity` (as `ulong` entity ID) | |
| `NativeObject` | Wraps a native pointer |

### Performance Notes

The Reflection API uses JSON serialization for every call. For high-frequency
operations (e.g. every frame), prefer the direct APIs (`Transform`, `Entity`,
`Debug`, `Time`, `Physics`) which use raw unmanaged function pointers with zero
marshalling overhead.

Use Reflection for:
- One-time setup / configuration
- Infrequent queries
- Accessing components that don't have direct bindings
- Prototyping before writing optimized bindings

---

## 9. Calling EBus Events from C#

O3DE's **Event Bus (EBus)** system is how components communicate. From C# you can
**broadcast** (all handlers) or **send** (to a specific entity).

### Discover Available EBuses

```csharp
using O3DE.Reflection;

// List all EBuses
IReadOnlyList<string> buses = NativeReflection.GetEBusNames();
foreach (string bus in buses)
    Debug.Log(bus);  // "TransformBus", "PhysicsSystemRequestBus", ...

// List events on a specific EBus
IReadOnlyList<string> events = NativeReflection.GetEBusEventNames("TransformBus");
foreach (string evt in events)
    Debug.Log($"  event: {evt}");
```

### Broadcast (to all handlers)

```csharp
// Call an event on ALL handlers of a bus
NativeReflection.BroadcastEBusEvent(
    "PhysicsSystemRequestBus",
    "SetGravity",
    new Vector3(0, 0, -9.81f)
);
```

### Send to a Specific Entity

```csharp
// Call an event on the handler connected to a specific entity
NativeReflection.SendEBusEvent(
    "TransformBus",
    "SetWorldTranslation",
    EntityId,                       // target entity ID (ulong)
    new Vector3(100, 0, 50)
);

// Or with an Entity object
NativeReflection.SendEBusEvent(
    "TransformBus",
    "GetWorldTranslation",
    Entity                          // Entity object
);
```

### Common EBus Examples

```csharp
// ---- TransformBus ----
object? worldPos = NativeReflection.SendEBusEvent("TransformBus", "GetWorldTranslation", EntityId);
NativeReflection.SendEBusEvent("TransformBus", "SetLocalTranslation", EntityId, new Vector3(0,5,0));

// ---- MeshComponentRequestBus ----
NativeReflection.SendEBusEvent("MeshComponentRequestBus", "SetVisibility", EntityId, false);

// ---- AudioTriggerComponentRequestBus ----
NativeReflection.SendEBusEvent("AudioTriggerComponentRequestBus", "ExecuteTrigger", EntityId, "Play_Explosion");

// ---- TagComponentRequestBus ----
object? hasTag = NativeReflection.SendEBusEvent("TagComponentRequestBus", "HasTag", EntityId, "Enemy");
```

### Receiving EBus Events — `[EBus]` / `[EBusHandler]`

C# can also **handle** EBus events as a first-class consumer. The
authoring shape is class-level attributes that a Roslyn source generator
(`O3DESharp.SourceGenerators`) processes at compile time to emit the
`Connect` / `Disconnect` / dispatch glue. End result: you write the
handler methods, the generator handles the BehaviorEBus plumbing.

```csharp
using O3DE;

[EBus("TickBus")]
public partial class GameClock : ScriptComponent
{
    public override void OnCreate()
    {
        ConnectToTickBus();      // generated by O3DESharp.SourceGenerators
    }

    public override void OnDestroy()
    {
        DisconnectFromTickBus();  // generated
    }

    [EBusHandler("OnTick")]
    private void HandleTick(float deltaTime, ulong frameId)
    {
        Debug.Log($"frame {frameId}: dt={deltaTime}");
    }
}
```

Rules:

* The class **must be `partial`** so the generator can add Connect /
  Disconnect / dispatch members to it.
* `[EBus("...")]` is the bus name as reflected into BehaviorContext
  (run `NativeReflection.GetEBusNames()` to discover what's available).
  Multiple `[EBus]` attributes on one class subscribe it to multiple
  buses — each gets its own `ConnectTo<BusName>` / `DisconnectFrom<BusName>`
  pair.
* `[EBusHandler("EventName")]` on a method matches it to the named
  event on whichever bus the class is registered for. Method signature
  must match the event's reflected parameter types in order; arguments
  are unmarshaled from the C++ side via the same marshaling table that
  Broadcast / Send use (primitives, math types, EntityId, strings).
* The method can be public or private; the source generator emits a
  dispatch shim that accesses it from the partial.

**Addressed buses** (TransformNotificationBus, anything keyed on an
EntityId) take the address as an argument to `ConnectTo`:

```csharp
[EBus("TransformNotificationBus")]
public partial class FollowParent : ScriptComponent
{
    public override void OnCreate()
    {
        ConnectToTransformNotificationBus(EntityId);
    }

    public override void OnDestroy()
    {
        DisconnectFromTransformNotificationBus();
    }

    [EBusHandler("OnTransformChanged")]
    private void HandleTransformChanged(Vector3 local, Quaternion world)
    {
        Debug.Log($"transform changed: world rot {world}");
    }
}
```

**What's marshalled across the boundary:** primitives (`bool`, integer
types, `float`, `double`), `string`, `ulong`-shaped IDs (`EntityId`),
and the math types `Vector2` / `Vector3` / `Quaternion`. Other types
fall back to `default(T)` with a warning in the editor console — extend
the marshal table in `EBusHandlerRegistry.UnmarshalArg<T>` if you need
more. The C++-side marshal table is in
`Code/Source/Scripting/Marshaling/BehaviorContextMarshaling.cpp`.

**Diagnostics:** the source generator runs at every build; if the bus
name is misspelled or the handler arity is wrong, you get a normal
compile error pointing at the attribute. The runtime side
(`EBusHandlerRegistry`) also logs a warning to the editor console if
the C++ side ever packs fewer args than the method expects, so a
reflection-data desync degrades gracefully rather than crashing.

**Lifecycle:** if you forget to call `DisconnectFrom...` in
`OnDestroy`, the managed-side registry holds the entry until the
process exits but the C++ side cleans up on assembly reload, so leaked
handlers manifest as duplicate dispatch on the next reload — easy to
catch in dev. The codegen template above is the recommended shape.

> **Limit:** Handlers run on whatever thread the bus dispatcher invokes
> from. Most engine buses (TickBus, TransformNotificationBus) fire on
> the main thread; physics buses fire from the physics simulation
> thread. Treat handlers like Unity's `MonoBehaviour` callbacks:
> assume main-thread unless the bus's documentation says otherwise.

---

## 10. Input (Current State)

The direct Input API bindings in `InternalCalls` exist but are currently
**placeholder stubs** that return defaults. For now, use the Input component
through the Reflection API:

```csharp
// Example: reading input via O3DE's Input system through Reflection
// This depends on having an InputComponent configured on your entity or globally

// Alternative: bind an O3DE Input event in the Editor and read the action value
// through the appropriate EBus. The exact bus name depends on your project's
// input configuration.
```

When the direct Input API is completed, usage will look like:

```csharp
// (Planned — not yet fully functional)
if (Input.IsKeyDown(KeyCode.W))
    Transform.TranslateLocal(Vector3.Forward * speed * Time.DeltaTime);

if (Input.IsMouseButtonDown(0))
    Debug.Log("Left click");

Vector3 mousePos = Input.MousePosition;
Vector3 mouseDelta = Input.MouseDelta;
float axis = Input.GetAxis("Horizontal");
```

---

## 11. Hot Reload

In **Debug** and **Profile** builds, O3DESharp supports hot-reloading C# assemblies
without restarting the editor.

### How It Works

1. Rebuild your C# project (`dotnet build`).
2. The system detects the changed DLL and triggers a reload.
3. **Serializable fields** (primitives, value types, arrays/lists of primitives) are
   automatically saved and restored across the reload.
4. Fields marked `[NonSerialized]` or compiler-generated fields are skipped.

### Programmatic Control

```csharp
using O3DE.Core.HotReload;

var mgr = HotReloadManager.Instance;

// Subscribe to reload events
mgr.AssemblyUnloading += (sender, args) =>
{
    Debug.Log($"Assembly unloading: {args.AssemblyName}");
    // Save any custom state here
};

mgr.AssemblyLoaded += (sender, args) =>
{
    Debug.Log($"Assembly loaded: {args.AssemblyName}, gen {args.Generation}");
    // Recreate caches, re-register handlers, etc.
};

// Check the reload generation (increments on each reload)
int gen = mgr.ReloadGeneration;
```

### Tips

- Keep frame-critical state in simple serializable fields for automatic
  preservation.
- Use `OnCreate()` or `OnEnable()` to reinitialize transient resources (file
  handles, network connections, caches) after a reload.
- Hot reload is **not available** in Release builds.

---

## 11b. Debugging C# Scripts

Coral hosts the .NET runtime in-process with the O3DE editor (or game
launcher), so any standard managed-mode debugger — Rider, Visual Studio,
VS Code with the C# extension — can attach to `Editor.exe` and hit
breakpoints in your script code. No special debug build of the engine is
required.

### Prerequisites

- Your C# project must produce **portable PDBs**. New csprojs from the
  Tools → C# Scripting → Create C# Project template already include:
  ```xml
  <DebugType>portable</DebugType>
  <DebugSymbols>true</DebugSymbols>
  ```
  Existing csprojs: add those two lines inside `<PropertyGroup>`. The
  Phase 16b deploy target (`DeployToBinScripts`) already copies the
  `.pdb` alongside the `.dll`, so the runtime loader picks up symbols
  automatically.
- Build in **Debug** config (`dotnet build -c Debug`). Release optimizes,
  which strips locals and merges step boundaries — breakpoints still
  bind, but stepping is jumpy.

### One-click attach (Phase 17a / 17b)

Tools → C# Scripting → **Attach Debugger** is a submenu with five
entries, listed in order of automation:

| Menu item | What it does | Clicks per session |
|---|---|---|
| **Trigger JIT Debugger** | Spawns `vsjitdebugger.exe -p <pid>` on Windows. OS picker lists every registered managed debugger (Rider, Visual Studio, etc.); one click attaches. Cross-IDE, no detection needed. | ~3 |
| **Attach with Rider** | Locates `rider64.exe` and runs `rider64.exe attach-to-process <pid>`. Rider pops to front already attached. | 1 |
| **Attach with VS Code** | Opens the project folder in VS Code; press F5 to launch the bundled `O3DESharp: Attach to Editor` configuration. | 2 |
| **Copy Debugger Attach Info** | Drops the PID + a step-by-step hint string onto the clipboard. The original fallback for IDEs without one-click support. | ~6 |
| **Auto-attach on Game Mode** | Cycles `Off → JIT → Rider → VS Code → Off`. When set, the editor invokes the selected attach method *before* Ctrl+G actually starts play, so by the time `OnCreate` runs the debugger is bound. | 0 (after toggling once) |
| **Run with Debugger** *(Phase 17c)* | Spawns `<Project>.GameLauncher.exe` from `build/.../bin/<config>/` and auto-attaches your configured `Auto-attach on Game Mode` method (or JIT picker fallback) to the freshly-launched runtime. Use this to debug runtime-only paths that the editor never exercises (e.g. server launchers, exported player builds). Build the launcher target first via CMake. | 1 |

Plus a separate gate that pauses script activation until a debugger
attaches:

> ☑ **Wait For Debugger On Script Activate** — every
> `CSharpScriptComponent::Activate` blocks before user `OnCreate` runs
> until a managed debugger attaches, with a 60-second timeout. Implemented
> in `O3DE.ScriptComponent._O3DESharpWaitForAttachIfRequested`, gated on
> the `/O3DE/O3DESharp/WaitForDebuggerOnActivate` registry key and the
> mirrored `O3DESHARP_WAIT_FOR_DEBUGGER` environment variable so external
> tooling can force it.

**Recommended "zero-effort" combination** (for a debugger-heavy session):
1. Attach Debugger → Auto-attach on Game Mode → click until it says
   **Rider** (or **VS Code**, or **JIT**).
2. Attach Debugger → ☑ Wait For Debugger On Script Activate.
3. Hit Ctrl+G. The editor triggers your IDE's attach automatically;
   scripts pause at `Activate` until the debugger binds; your breakpoints
   fire as soon as play actually starts.

### Manual fallback (PID + Attach to Process)

If none of the one-click options fit your workflow:

1. **Tools → C# Scripting → Attach Debugger → Copy Debugger Attach
   Info**. PID lands on the clipboard.
2. In your IDE: **Attach to Process…** and paste the PID into the filter
   field. Pick the editor entry, and on Windows make sure the IDE is
   attaching the .NET CoreCLR / managed-mode debugger (not the native
   debugger — that one breaks on every Coral P/Invoke).
3. Set breakpoints in your `.cs` files. Enter game mode (Ctrl+G) or
   trigger your script some other way. Breakpoints fire.

### Editing while attached

The full hot-reload chain (Phase 16) keeps working with the debugger
attached:

1. Edit C# in your IDE.
2. Build (Rider's hammer / VS's Ctrl+Shift+B / `dotnet build`). The
   MSBuild `DeployToBinScripts` target copies to `Bin/Scripts/`. The
   editor's file watcher reloads the assembly.
3. The debugger reattaches automatically to the new
   `AssemblyLoadContext` on most IDEs; if your IDE drops the breakpoints
   on reload (it might say "module unloaded"), reattach by Ctrl+Shift+F5
   or your IDE's equivalent.

### Pausing before a breakpoint with `O3DE.Debugger.WaitForAttach`

The hardest thing to debug is `OnCreate` — by the time the IDE is
attached, the call has already returned. `O3DE.Debugger.WaitForAttach`
blocks the script thread until you attach (or until a timeout):

```csharp
using O3DE;

public class MyScript : ScriptComponent
{
    public override void OnCreate()
    {
        // Block here up to 30s waiting for the IDE to attach. Once
        // IsAttached flips true (you've hit Attach), execution resumes
        // and breakpoints later in OnCreate will fire normally.
        Debugger.WaitForAttach(TimeSpan.FromSeconds(30));

        // ... your normal OnCreate code, breakpointable from here on.
    }
}
```

Returns `true` if a debugger attached, `false` if the timeout elapsed.
On a player's Release machine where IsAttached can never become true,
the timeout prevents a forgotten call from freezing the game forever.
Other helpers in the same class:

| Helper | What it does |
|---|---|
| `Debugger.IsAttached` | Pass-through to `System.Diagnostics.Debugger.IsAttached`. |
| `Debugger.Launch()`   | Triggers the OS's JIT debugger picker if one is registered. Silent no-op when already attached or when launch isn't supported. |
| `Debugger.Break()`    | Issues a managed breakpoint, but only when a debugger is attached — safe to leave in shipped code. |

### Mixed-mode (C# + C++) debugging on Windows

Visual Studio's "Mixed (CLR and Native)" attach mode lets you step from
your C# script through Coral's P/Invoke into the C++ runtime. Useful
when chasing through an internal-call:

1. Attach to Process… → check **both** Managed (.NET Core, .NET 5+)
   AND Native under "Attach to".
2. Set the breakpoint in your `.cs` file as usual. When it hits, F11
   into the engine call and the debugger switches to native.

Rider and VS Code don't currently support mixed-mode managed/native
attach; use VS for this.

### Troubleshooting

| Symptom | Likely cause |
|---|---|
| "The breakpoint will not currently be hit. No symbols have been loaded for this document." | The `.pdb` isn't next to the `.dll` in `Bin/Scripts/`. Rebuild — `DeployToBinScripts` copies the PDB too. If you bypass MSBuild, copy it by hand. |
| Breakpoints bind but never hit | You attached the *native* debugger, not the managed one. Detach and reattach with the managed/.NET runtime selected. |
| Stepping skips lines | The DLL was built in Release. Switch to Debug or drop `<Optimize>` from your Release config. |
| Editor freezes after `WaitForAttach` and never resumes | The timeout was set to `Timeout.InfiniteTimeSpan` (or a negative `TimeSpan`) and no debugger attached, or `IsAttached` never flipped. Restart the editor. Use a finite timeout in production. Note: `TimeSpan.Zero` (and therefore the no-args call `WaitForAttach()`) means "check `IsAttached` once and return immediately" — it does **not** wait forever. |
| IDE loses breakpoints on hot-reload | Some IDEs reset breakpoints when the assembly load context recycles. Click Attach again or use Ctrl+Shift+F5. |
| **Visual Studio**: "Could not attach to the process. Value does not fall within the expected range. The error code is E_INVALIDARG, or COR_E_ARGUMENT, or WIN32_ERROR_INVALID_PARAMETER, or 0x80070057." | Three common causes: <br>1. VS doesn't have the **.NET Core debugging components** installed. Open Visual Studio Installer → Modify → Individual Components → check ".NET Framework 4.x debugger" *and* ".NET Compiler Platform" *and* the runtime debug component for your target. <br>2. VS picked the wrong **Attach to** engine. In "Attach to Process", set "Attach to:" to **Managed (.NET Core, .NET 5+)** explicitly — the auto-detect heuristic sometimes guesses native-only when Coral is between init and first-script-run. <br>3. **Privilege mismatch** — VS is running elevated and the editor isn't (or vice versa). Run both as the same user/elevation. |
| **Rider**: "Process is still running and does not respond" when triggering attach | The Rider attach action uses the `jetbrains://rider/attach-to-process?pid=<pid>` URL protocol (Phase 17c). If a Rider instance is already up, the URL is routed to it; if the URL handler isn't registered (older Toolbox install or sandbox setup), the launch falls through. Re-run JetBrains Toolbox or Rider's installer to refresh the protocol handler. |
| **VS Code**: F5 launches the launcher but doesn't break | Your `launch.json` is still pointing at the wrong launcher exe name. Edit the `${input:launcherExeName}` prompt or hard-code your `<Project>.GameLauncher.exe`. The template ships with a placeholder of `GameLauncher.exe` which is intentionally wrong so the prompt fires once per workspace. |
| "Run with Debugger" logs `No GameLauncher.exe found` | You haven't built the launcher target yet. From your project root: `cmake --build build/windows --target <Project>.GameLauncher --config profile`. |

---

## 12. Binding Generator

O3DESharp includes an **automated binding generator** that can produce type-safe C#
wrappers from O3DE's BehaviorContext reflection data.

### What It Generates

For each reflected class, EBus, method, property, and enum, the generator emits:

- Strongly-typed C# wrapper classes with XML documentation
- Property getters/setters
- Method overloads with default parameters
- Fluent `WithX()` builder methods
- Enum types matching C++ enums
- Metadata files (`Metadata.g.cs`, `metadata.json`)

### Configuration (`binding_config.json`)

```json
{
  "global": {
    "cSharpNamespace": "O3DE",
    "cSharpOutputPath": "Assets/Scripts/{GemName}",
    "cppOutputPath": "Code/Source/Scripting/Generated",
    "incrementalBuild": true,
    "requireExportAttribute": false
  },
  "gems": {
    "O3DESharp": {
      "enabled": true,
      "headerPatterns": ["Code/Include/**/*.h", "Code/Source/Scripting/**/*.h"],
      "excludePatterns": ["**/Platform/**", "**/Tests/**"],
      "requireExportAttribute": false
    }
  }
}
```

### Running the Generator

```bash
# From the engine or gem directory:
dotnet run --project Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/ -- \
  --config binding_config.json \
  --output Assets/Scripts/Generated
```

Or via the Python editor integration:

```python
import azlmbr.bus as bus
# The binding generator can be triggered from editor scripts
```

### Incremental Builds

The generator uses SHA256 hashing (`.binding_cache.json`) and only regenerates
files that have changed, making subsequent runs fast.

### Optional C++ Export Attribute

If `requireExportAttribute` is `true`, only classes/methods annotated with
`O3DE_EXPORT_CSHARP` will be exported:

```cpp
#include <Scripting/ExportAttributes.h>

class O3DE_EXPORT_CSHARP MyComponent : public AZ::Component
{
    // ...
};
```

---

## 13. Building & Deploying

### Assembly Deployment Layout

The engine expects this structure in your project directory:

```
<YourProject>/
└── Bin/
    └── Scripts/
        ├── Coral/
        │   ├── Coral.Managed.dll              ← .NET host library
        │   ├── Coral.Managed.runtimeconfig.json
        │   └── Coral.Managed.deps.json
        ├── O3DE.Core.dll                      ← Core scripting API
        ├── O3DE.Core.deps.json
        ├── MyGame.dll                         ← Your compiled C# project (was "GameScripts.dll" pre-Phase 16)
        └── MyGame.pdb                         ← Symbols for debugger attach
```

### Automatic Deployment

Three pieces of automatic deployment cooperate, listed in the order they run:

1. **CMake staging** (`O3DESharp.StageCoral`, `O3DESharp.StageO3DECore`). When
   the engine builds, custom targets stage `Coral.Managed.dll`,
   `O3DE.Core.dll`, and their runtimeconfig/deps under
   `Gems/O3DESharp/bin/Coral/` and `Gems/O3DESharp/bin/O3DE.Core/`.
2. **Runtime auto-deploy** (`O3DESharpSystemComponent::Activate` calling
   `DeployLatestManagedAssemblies` in Debug / Profile). On editor start
   the runtime walks the gem's staging directories and the dotnet build
   output (`Assets/Scripts/O3DE.Core/bin/{Debug|Release}/net9.0/`) and
   copies the newest `Coral.Managed.dll` / `O3DE.Core.dll` into the
   project's `Bin/Scripts/Coral/` and `Bin/Scripts/` respectively.
   Skipped in Release.
3. **User-csproj MSBuild deploy** (Phase 16b's `DeployToBinScripts`
   target). On every successful build of a user C# project, MSBuild
   copies the output DLL + PDB into `<ProjectPath>/Bin/Scripts/`. The
   editor's file watcher (Phase 16a) then dispatches a hot reload.

### Manual Build & Deploy

If you bypass MSBuild's deploy target — e.g. running `dotnet build`
against an unmigrated csproj — copy the output yourself:

```bash
# 1. Build O3DE.Core (if you modified it)
cd <engine>/Gems/O3DESharp/Assets/Scripts/O3DE.Core
dotnet build -c Debug

# 2. Build your game scripts
cd <project>/Gem/Source/CSharp/MyGame
dotnet build -c Debug

# 3. Copy the output to where the runtime loads from
cp bin/Debug/MyGame.dll <project>/Bin/Scripts/MyGame.dll
cp bin/Debug/MyGame.pdb <project>/Bin/Scripts/MyGame.pdb
```

…and run Tools → C# Scripting → **Migrate C# Project Files** so future
builds deploy automatically.

### Settings Registry

Custom paths can be configured in `<Project>/Registry/o3desharp.setreg`:

```json
{
    "O3DE": {
        "O3DESharp": {
            "UserAssemblies": [
                { "AssemblyName": "MyGame.dll" },
                { "AssemblyName": "MyGame.AI.dll" }
            ],
            "CoralDirectory":      "C:/custom/path/to/Coral/",
            "CoreApiAssemblyPath": "C:/custom/path/to/O3DE.Core.dll"
        }
    }
}
```

`UserAssemblies` (array, post-Phase 1) is the canonical form; each entry
is resolved against `<ProjectPath>/Bin/Scripts/`. The legacy
`UserAssemblyPath` (single string) is still honored as a fallback for
existing projects but new projects should use the array.

---

## 14. Project Export

To export a standalone game build with C# scripting, use the export script:

```bash
o3de.py export-project \
  --export-script Gems/O3DESharp/ExportScripts/export_project_with_csharp.py \
  --project-path /path/to/project \
  --output-path /path/to/export \
  --config profile
```

This automatically:
1. Builds user C# assemblies in **Release** mode
2. Deploys `Coral.Managed.dll`, `O3DE.Core.dll`, and your game DLLs
3. Packages everything into a runnable directory

---

## 15. Known Limitations

| Area | Limitation |
|------|-----------|
| **Input** | Direct keyboard/mouse API is placeholder — use O3DE's Input component via EBus/Reflection |
| **EBus Handler Marshaling** | Handler param marshaling covers primitives, `string`, `Vector2/3`, `Quaternion`, EntityId-shaped IDs. `Transform` / `Vector4` / `Color` / `Aabb` / `Matrix3x3` / `Matrix4x4` parameters arrive as `default(T)` with a warning — extend `EBusHandlerRegistry.UnmarshalArg<T>` to add coverage. |
| **Generics** | Generic types in BehaviorContext are not fully mapped |
| **Reflection Performance** | JSON round-trip on every call — use direct APIs for per-frame logic |
| **Hot Reload** | Debug/Profile builds only; non-serializable state is lost |
| **Platforms** | JIT on desktop (Windows/Linux/Mac); AOT required for consoles/mobile (experimental) |

---

## Full Example: Player Controller

```csharp
using O3DE;
using O3DE.Reflection;

namespace MyGame
{
    public class PlayerController : ScriptComponent
    {
        private float moveSpeed = 10f;
        private float jumpForce = 8f;
        private float gravity = -20f;
        private float verticalVelocity = 0f;
        private bool isGrounded = false;

        public override void OnCreate()
        {
            Debug.Log($"PlayerController ready on {Name}");
        }

        public override void OnUpdate(float deltaTime)
        {
            Vector3 movement = Vector3.Zero;

            // Ground check
            RaycastHit ground = Physics.GroundCheck(Transform.Position, 1.1f);
            isGrounded = ground.Hit;

            if (isGrounded && verticalVelocity < 0)
                verticalVelocity = 0;

            // Apply gravity
            verticalVelocity += gravity * deltaTime;
            movement.Z = verticalVelocity * deltaTime;

            // Apply movement along the entity's forward direction
            // (In a real game, you'd read input here)
            movement += Transform.Forward * moveSpeed * deltaTime;

            Transform.Translate(movement);

            // Respawn if fallen too far
            if (Transform.Position.Z < -50f)
            {
                Transform.Position = new Vector3(0, 0, 10);
                verticalVelocity = 0;
                Debug.LogWarning("Player respawned!");
            }
        }

        public override void OnDestroy()
        {
            Debug.Log("PlayerController shutting down");
        }
    }
}
```

## Full Example: Accessing Native Components via Reflection

```csharp
using O3DE;
using O3DE.Reflection;

namespace MyGame
{
    public class ComponentAccessExample : ScriptComponent
    {
        public override void OnCreate()
        {
            // List every reflected class in the engine
            var classes = NativeReflection.GetClassNames();
            Debug.Log($"Engine has {classes.Count} reflected classes");

            // Check what's on TransformBus
            var events = NativeReflection.GetEBusEventNames("TransformBus");
            Debug.Log("TransformBus events:");
            foreach (var evt in events)
                Debug.Log($"  - {evt}");

            // Read our world position via EBus (equivalent to Transform.Position)
            object? pos = NativeReflection.SendEBusEvent(
                "TransformBus", "GetWorldTranslation", EntityId);
            Debug.Log($"World position via EBus: {pos}");

            // Create a native Vector3, manipulate it, read it back
            using var vec = NativeReflection.CreateInstance("Vector3", 1f, 2f, 3f);
            object? length = vec.InvokeMethod("GetLength");
            Debug.Log($"Vector3(1,2,3).GetLength() = {length}");
        }
    }
}
```
