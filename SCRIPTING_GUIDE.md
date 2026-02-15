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
12. [Binding Generator](#12-binding-generator)
13. [Building & Deploying](#13-building--deploying)
14. [Project Export](#14-project-export)
15. [Known Limitations](#15-known-limitations)

---

## 1. Quick Start

### Prerequisites

- .NET 8.0 or 9.0 SDK installed (`dotnet --version`)
- O3DE Editor built with the **O3DESharp** Gem enabled

### Create a C# Project

The easiest way is through the O3DE Editor's **Tools → C# Project Manager** (if
the editor Python tools are registered), or manually:

```
<YourProject>/
└── Assets/
    └── Scripts/
        └── MyGame/
            ├── MyGame.csproj
            └── GameScript.cs
```

**MyGame.csproj:**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <OutputPath>bin/$(Configuration)</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="O3DE.Core">
      <HintPath>../../../Bin/Scripts/O3DE.Core.dll</HintPath>
    </Reference>
    <Reference Include="Coral.Managed">
      <HintPath>../../../Bin/Scripts/Coral/Coral.Managed.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

### Build

```bash
cd <YourProject>/Assets/Scripts/MyGame
dotnet build -c Debug
```

Copy the resulting `MyGame.dll` (or configure the output path) to
`<YourProject>/Bin/Scripts/GameScripts.dll` — this is the assembly the engine
loads by default. You can change the name via the Settings Registry key
`/O3DE/O3DESharp/UserAssemblyPath`.

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

> **Note:** You can only *send/broadcast* EBus events. Registering as a C# EBus
> *handler* (receiving events) is not yet supported.

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
        └── GameScripts.dll                    ← Your compiled C# project
```

### Automatic Deployment

O3DESharp **automatically finds and deploys** the latest `Coral.Managed.dll` and
`O3DE.Core.dll` on startup. It searches:

- Gem staging directories (`Gems/O3DESharp/bin/Coral/`, `Gems/O3DESharp/bin/O3DE.Core/`)
- `dotnet build` output (`Assets/Scripts/O3DE.Core/bin/{Debug|Release}/{net8.0|net9.0}/`)
- CMake build workspace (`Build/`, `_deps/`)
- Install tree (`install/Gems/O3DESharp/...`)

It compares timestamps and always picks the **newest** version, copying it only if
the deployed copy is missing or stale.

### Manual Build & Deploy

```bash
# 1. Build O3DE.Core (if modified)
cd <engine>/Gems/O3DESharp/Assets/Scripts/O3DE.Core
dotnet build -c Debug

# 2. Build your game scripts
cd <project>/Assets/Scripts/MyGame
dotnet build -c Debug

# 3. Copy to deploy location (if not using auto-deploy)
cp bin/Debug/MyGame.dll <project>/Bin/Scripts/GameScripts.dll
```

### Settings Registry

Custom paths can be configured in `<Project>/Registry/o3desharp.setreg`:

```json
{
    "O3DE": {
        "O3DESharp": {
            "UserAssemblyPath": "C:/custom/path/to/GameScripts.dll",
            "CoralDirectory": "C:/custom/path/to/Coral/",
            "CoreApiAssemblyPath": "C:/custom/path/to/O3DE.Core.dll"
        }
    }
}
```

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
| **EBus Handlers** | You can send/broadcast events, but cannot register as a C# handler to *receive* events |
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
