# O3DESharp - C# Scripting for O3DE

O3DESharp is a Gem that adds C# scripting support to the Open 3D Engine (O3DE) using the [Coral](https://github.com/WatchDogStudios/Coral) .NET host library (a fork of [StudioCherno/Coral](https://github.com/StudioCherno/Coral) with WD-Studios-specific fixes).

## Overview

O3DESharp enables game developers to write gameplay logic in C# instead of (or alongside) C++ and Lua. It provides:

- **Full .NET 9.0 Support**: Write scripts using modern C# features
- **Hot Reload**: Recompile and reload C# assemblies without restarting the editor
- **Native Interop**: Seamless communication between C++ and C# code
- **Familiar API**: Entity/Component model similar to other popular engines
- **Automated Reflection**: Automatic access to any type reflected to O3DE's BehaviorContext

## What's New (Phase 16 → Phase 18-E)

Recent iterations added the editor + workflow polish that was always
the missing half of "hot reload", and most recently the second half of
EBus support — C# can now both send and *receive* EBus events:

- **First-class EBus handlers** (Phase 18-E). Decorate any partial
  `ScriptComponent` subclass with `[EBus("BusName")]` and individual
  methods with `[EBusHandler("EventName")]`; a Roslyn source generator
  (`O3DESharp.SourceGenerators`) emits the `ConnectTo<BusName>` /
  `DisconnectFrom<BusName>` / dispatch glue at compile time. The C++
  side installs a `BehaviorEBusHandler` with a generic hook that
  marshals args back into managed code, so user methods see typed
  parameters. See [SCRIPTING_GUIDE.md §9 — Receiving EBus Events](SCRIPTING_GUIDE.md#9-calling-ebus-events-from-c)
  for the authoring pattern.
- **Auto-reload on file change** (Phase 16a). The editor watches
  `<ProjectPath>/Bin/Scripts/` and reloads user assemblies automatically
  when a DLL is rebuilt. Toggle via Tools → C# Scripting → "Reload
  Scripts on File Change".
- **MSBuild post-build deploy** (Phase 16b). New `.csproj`s from the
  template carry a `DeployToBinScripts` target that copies the build
  output to the engine's load path — so IDE builds (Rider / Visual
  Studio / `dotnet build`) all auto-reload without going through the
  editor's Build menu. Existing csprojs: run Tools → C# Scripting →
  "Migrate C# Project Files" once.
- **`[ExposedProperty]` typed widgets** for `bool` / `int` / `float` /
  `string`. Phase 14 flipped the `EditContext` to the
  `CSharpExposedProperties` handler.
- **Script picker UX**: dropdown + Browse / Create / Edit buttons on the
  Script Class field, with recently-used classes pinned to the top.
- Build for users other than the maintainer was fixed: the binding
  generator's `Metadata.g.cs` no longer embeds the maintainer's drive
  letter; user-project csproj template now declares net9.0 to match
  `O3DE.Core`; the Coral Python EBus handler creation pattern was
  corrected to use `azlmbr.editor.CSharpEditorToolsBusHandler()` instead
  of `azlmbr.object.create`.

For installed-engine projects: `cmake --install` doesn't currently copy
`Code/Tools/BindingGenerator/` into the install tree, so the
ClangSharpInvoker falls back to the engine source path via
`azlmbr.paths.engroot`. Either keep the source clone reachable or copy
the tool dir into the install location.

## Requirements

- **.NET 9.0 SDK**: Download from [https://dotnet.microsoft.com/download](https://dotnet.microsoft.com/download)
- **.NET 9.0 Runtime** (required for any machine running the editor,
  whether or not it builds C#). On Windows, install both packages from
  the link above — the SDK includes a runtime, but if you only install
  the runtime on a player's machine, Coral.Managed's
  `runtimeconfig.json` (`tfm: net9.0`, `rollForward: LatestMinor`)
  will require a 9.x runtime specifically.
- **Supported Platforms**: Windows x64, Linux x64 (JIT only today).

> **Roadmap, not yet implemented:** macOS, iOS, Android, and console platforms
> (PlayStation, Xbox, Nintendo Switch) are targets we'd like to support. They
> require NativeAOT-friendly bindings and platform-specific Coral hosting that
> are not in this gem yet. `gem.json` currently declares only Linux and Windows
> as supported.

### Building on Linux

O3DESharp supports Linux (editor + runtime) with the same C# authoring, build,
hot-reload, and run workflow as Windows.

Prerequisites:
- .NET 9.0 SDK. If it isn't on your `PATH`, either add it, set `DOTNET_ROOT`,
  or set `O3DESHARP_DOTNET_EXECUTABLE` to the `dotnet` binary — the editor
  tooling checks all three (plus common install locations) before giving up.
- CMake will auto-install a .NET 9 SDK into `<build>/.dotnet` if none is found
  (see `Code/o3desharp_netverify.cmake`); export `DOTNET_ROOT=<build>/.dotnet`
  so the editor picks up that copy.

The managed `O3DE.Core` API builds via the `dotnet` CLI on Ninja/Make
generators (no Visual Studio required).
### How this gem is consumed (do not vendor it)

O3DESharp lives at its own checkout and is consumed by reference. Register it once:

```bash
o3de register --gem-path /path/to/O3DESharp
```

Then enable it in your project as normal. The engine references the gem; it never contains a copy.

**The gem must never be copied into an engine tree.** This is a deliberate, rejected practice, not
an oversight. A vendored copy previously drifted 109 commits behind the source of record while
accumulating ~2,700 lines of work that existed in no repository at all — recoverable from no clone,
one `git clean` from destruction. Referencing a single checkout makes that class of drift
structurally impossible. If you need reproducible builds pinned to a gem version, pin a published
release from the gem repository rather than copying files into the engine.

## Installation

1. Enable the O3DESharp Gem in your project:
   ```bash
   o3de register --gem-path Gems/O3DESharp
   o3de enable-gem --gem-name O3DESharp --project-path /path/to/your/project
   ```

2. Rebuild your project. CMake's `O3DESharp.StageCoral` and
   `O3DESharp.StageO3DECore` custom targets stage `Coral.Managed.dll`,
   `O3DE.Core.dll`, and their runtimeconfig/deps next to the engine
   binaries automatically — no manual copy step.

3. (Optional) Build the C# Core API standalone, e.g. for IDE intellisense
   when working on the API itself:
   ```bash
   cd Gems/O3DESharp/Assets/Scripts/O3DE.Core
   dotnet build -c Release
   ```

## Quick Start

### Creating Your First C# Script

1. Create a new C# class library project:
   ```bash
   dotnet new classlib -n MyGameScripts -f net9.0
   ```

2. Add a reference to O3DE.Core.dll

3. Create a script by inheriting from `ScriptComponent`:

```csharp
using O3DE;

namespace MyGame
{
    public class PlayerController : ScriptComponent
    {
        private float speed = 10.0f;

        public override void OnCreate()
        {
            Debug.Log($"PlayerController created on {Name}!");
        }

        public override void OnUpdate(float deltaTime)
        {
            // Move the entity forward
            Transform.Translate(Transform.Forward * speed * deltaTime);
        }

        public override void OnDestroy()
        {
            Debug.Log("PlayerController destroyed!");
        }
    }
}
```

4. Build your assembly:
   ```bash
   dotnet build -c Release
   ```

5. In the O3DE Editor:
   - Add a "C# Script" component to an entity
   - Set the "Script Class" field to `MyGame.PlayerController`
   - Run the game!

### Exposing Fields to the Inspector — `[ExposedProperty]`

Decorate any public field or public auto-property on a `ScriptComponent`
subclass with `[ExposedProperty]` to make it editable in the inspector and
serialized with the entity / prefab:

```csharp
public class PlayerController : ScriptComponent
{
    [ExposedProperty]
    public float Speed = 10.0f;

    [ExposedProperty("Maximum Health")]
    public int MaxHealth = 100;

    [ExposedProperty] public bool CanJump = true;
}
```

The component config keeps a `name → value` map (`Exposed Properties` in the
inspector). Values are applied to the managed instance before `OnCreate`
runs, so your initialization code sees the editor-configured values.

Supported types in the current slice: `bool`, integer types (`byte` /
`sbyte` / `short` / `ushort` / `int` / `uint` / `long` / `ulong`),
`float`, `double`, and `string`. Typed inspector widgets (sliders, color
pickers, `Vector3` / `Quaternion` / enum support) are a planned follow-up;
today the inspector shows a generic key/value editor.

## Two API Approaches

O3DESharp provides two complementary approaches for accessing O3DE functionality:

### 1. Direct API (Recommended for Common Operations)

Hand-written bindings for frequently used functionality. These provide the best performance and type safety:

- `Entity`, `Transform` - Entity and transform access
- `Vector3`, `Quaternion` - Math types
- `Debug` - Logging utilities
- `Time` - Delta time and time scale
- `Physics` - Raycasting and physics queries

### 2. Automated Reflection API (For Everything Else)

Dynamic access to **any** type reflected to O3DE's BehaviorContext. This
allows you to call methods, access properties, and send EBus events
without compile-time bindings.

EBus *handling* (receiving events) uses a third path: the
`O3DESharp.SourceGenerators` Roslyn generator picks up
`[EBus]` / `[EBusHandler]` attributes on your `ScriptComponent` partial
class and emits the Connect/Disconnect/dispatch glue at compile time.
See [SCRIPTING_GUIDE.md §9 — Receiving EBus Events](SCRIPTING_GUIDE.md#9-calling-ebus-events-from-c)
for the authoring pattern.



```csharp
using O3DE.Reflection;

// Query available classes
IReadOnlyList<string> classes = NativeReflection.GetClassNames();

// Check if a class exists
bool exists = NativeReflection.ClassExists("Vector3");

// Get method names
IReadOnlyList<string> methods = NativeReflection.GetMethodNames("Vector3");

// Create native objects
using (var nativeObj = NativeReflection.CreateInstance("SomeNativeClass"))
{
    // Invoke methods
    nativeObj.InvokeMethod("SomeMethod", arg1, arg2);
    
    // Access properties
    float value = nativeObj.GetProperty<float>("SomeProperty");
    nativeObj.SetProperty("SomeProperty", 42.0f);
}

// Broadcast EBus events
NativeReflection.BroadcastEBusEvent("SomeBus", "SomeEvent", args);

// Send to specific entity
NativeReflection.SendEBusEvent("TransformBus", "SetPosition", entityId, newPosition);
```

## API Reference

### ScriptComponent

Base class for all C# scripts. Override these methods to implement your logic:

| Method | Description |
|--------|-------------|
| `OnCreate()` | Called when the component is activated |
| `OnUpdate(float deltaTime)` | Called every frame |
| `OnDestroy()` | Called when the component is deactivated |
| `OnTransformChanged()` | Called when the entity's transform changes |

### Entity

Represents an O3DE entity:

```csharp
// Get entity properties
string name = Entity.Name;
bool isActive = Entity.IsActive;
bool isValid = Entity.IsValid;

// Control entity state
Entity.Activate();
Entity.Deactivate();

// Check for components
bool hasPhysics = Entity.HasComponent("PhysXRigidBodyComponent");
```

### Transform

Access and modify entity transforms:

```csharp
// Position
Vector3 pos = Transform.Position;           // World position
Vector3 localPos = Transform.LocalPosition; // Local position
Transform.Position = new Vector3(0, 0, 10);

// Rotation
Quaternion rot = Transform.Rotation;        // Quaternion
Vector3 euler = Transform.EulerAngles;      // Euler angles (degrees)
Transform.Rotation = Quaternion.Identity;

// Scale
float scale = Transform.UniformScale;
Transform.UniformScale = 2.0f;

// Direction vectors
Vector3 forward = Transform.Forward;  // Y-axis in O3DE
Vector3 right = Transform.Right;      // X-axis
Vector3 up = Transform.Up;            // Z-axis

// Hierarchy
Entity? parent = Transform.Parent;
Transform.SetParent(otherEntity);

// Utility methods
Transform.Translate(new Vector3(1, 0, 0));
Transform.Rotate(new Vector3(0, 0, 45));
Transform.LookAt(targetPosition);
```

### Vector3

3D vector type matching O3DE's coordinate system:

```csharp
// Creation
Vector3 v = new Vector3(1, 2, 3);
Vector3 zero = Vector3.Zero;
Vector3 one = Vector3.One;
Vector3 forward = Vector3.Forward; // (0, 1, 0) in O3DE

// Operations
Vector3 sum = a + b;
Vector3 scaled = v * 2.0f;
float dot = Vector3.Dot(a, b);
Vector3 cross = Vector3.Cross(a, b);
float distance = Vector3.Distance(a, b);
Vector3 lerped = Vector3.Lerp(a, b, 0.5f);

// Properties
float magnitude = v.Magnitude;
Vector3 normalized = v.Normalized;
```

### Quaternion

Rotation quaternion:

```csharp
// Creation
Quaternion identity = Quaternion.Identity;
Quaternion fromEuler = Quaternion.FromEuler(0, 90, 0);
Quaternion fromAxis = Quaternion.AngleAxis(45, Vector3.Up);
Quaternion lookAt = Quaternion.LookRotation(direction);

// Operations
Quaternion combined = a * b;
Vector3 rotatedPoint = rotation * point;
Quaternion interpolated = Quaternion.Slerp(a, b, 0.5f);

// Conversion
Vector3 euler = rotation.EulerAngles;
```

### Debug

Logging utilities:

```csharp
Debug.Log("Info message");
Debug.LogWarning("Warning message");
Debug.LogError("Error message");
Debug.LogException(exception);

// Formatted logging
Debug.Log("Position: {0}", position);

// Debug-only logging (stripped in release builds)
Debug.LogDebug("Debug-only message");

// Assertions
Debug.Assert(condition, "Assertion failed message");
```

### Time

Time-related functionality:

```csharp
float dt = Time.DeltaTime;        // Seconds since last frame
float total = Time.TotalTime;     // Seconds since start
float scale = Time.TimeScale;     // Simulation speed (1.0 = normal)
ulong frame = Time.FrameCount;    // Current frame number
float fps = Time.FPS;             // Frames per second

// Time scale control
Time.Pause();                     // Sets TimeScale to 0
Time.Resume();                    // Sets TimeScale to 1
bool paused = Time.IsPaused;

// Utilities
bool elapsed = Time.HasElapsed(startTime, duration);
float progress = Time.Progress(startTime, duration);
```

### Physics

Physics queries:

```csharp
// Raycast
RaycastHit hit = Physics.Raycast(origin, direction, maxDistance);
if (hit.Hit)
{
    Vector3 point = hit.Point;
    Vector3 normal = hit.Normal;
    float distance = hit.Distance;
    Entity? hitEntity = hit.GetEntity();
}

// Linecast (point-to-point)
if (Physics.Linecast(from, to, out RaycastHit hit))
{
    // Something blocking the path
}

// Line of sight
bool canSee = Physics.HasLineOfSight(fromEntity, toEntity);

// Ground check
float groundHeight;
if (Physics.GetGroundHeight(position, out groundHeight))
{
    // Ground found at groundHeight
}
```

### NativeReflection

Dynamic access to any BehaviorContext-reflected type:

```csharp
using O3DE.Reflection;

// Query types
IReadOnlyList<string> classes = NativeReflection.GetClassNames();
IReadOnlyList<string> methods = NativeReflection.GetMethodNames("ClassName");
IReadOnlyList<string> properties = NativeReflection.GetPropertyNames("ClassName");
IReadOnlyList<string> ebuses = NativeReflection.GetEBusNames();
IReadOnlyList<string> events = NativeReflection.GetEBusEventNames("BusName");

// Check existence
bool classExists = NativeReflection.ClassExists("SomeClass");
bool methodExists = NativeReflection.MethodExists("SomeClass", "SomeMethod");

// Create instances
NativeObject obj = NativeReflection.CreateInstance("ClassName", constructorArgs);

// Invoke methods
object? result = NativeReflection.InvokeStaticMethod("Class", "Method", args);
object? result = NativeReflection.InvokeInstanceMethod(obj, "Method", args);
object? result = NativeReflection.InvokeGlobalMethod("Function", args);

// Property access
T value = NativeReflection.GetProperty<T>(obj, "PropertyName");
NativeReflection.SetProperty(obj, "PropertyName", value);

// EBus events
NativeReflection.BroadcastEBusEvent("BusName", "EventName", args);
NativeReflection.SendEBusEvent("BusName", "EventName", entityId, args);

// Cleanup
NativeReflection.DestroyInstance(obj);
```

### NativeObject

Wrapper for native objects created through reflection:

```csharp
using (NativeObject obj = NativeReflection.CreateInstance("SomeClass"))
{
    // Invoke methods
    obj.InvokeMethod("DoSomething", arg1, arg2);
    
    // With return value
    float result = obj.InvokeMethod<float>("GetValue");
    
    // Properties
    obj.SetProperty("PropertyName", 42);
    int value = obj.GetProperty<int>("PropertyName");
}
// Object is automatically destroyed when disposed
```

## Configuration

The O3DESharp system can be configured via the Settings Registry. Drop a
`.setreg` file under `<ProjectPath>/Registry/`:

```json
{
    "O3DE": {
        "O3DESharp": {
            "CoralDirectory":       "path/to/Coral",
            "CoreApiAssemblyPath":  "path/to/O3DE.Core.dll",

            "UserAssemblies": [
                { "AssemblyName": "MyGameScripts.dll" },
                { "AssemblyName": "MyPhysicsHelpers.dll" }
            ],

            "AutoReload":              true,
            "AutoReloadDebounceMs":    500
        }
    }
}
```

Field reference:

- `UserAssemblies` — preferred form. Array of `{ "AssemblyName": "..." }`.
  Each entry is resolved relative to `<ProjectPath>/Bin/Scripts/`, so the
  `AssemblyName` field is just the filename. The runtime loads every entry
  into a single shared assembly load context (so they can reference each
  other freely).
- `UserAssemblyPath` (single string) — legacy fallback for projects that
  only ship one user assembly. Still honored, but new projects should
  use the array form.
- `AutoReload` (Phase 16) — when true, the editor watches
  `<ProjectPath>/Bin/Scripts/` for DLL changes and triggers
  `ReloadUserAssemblies` automatically. Defaults to true in Debug / Profile
  builds, false in Release. Toggle via the menu (see below) instead of
  editing the setreg for one-off testing.
- `AutoReloadDebounceMs` — milliseconds of inactivity to wait before
  firing the reload. Default 500 (sized so a typical `dotnet build`'s
  multi-chunk write coalesces into one reload).

## Hot Reload

O3DESharp supports hot-reloading of C# assemblies during development:

1. Edit your C# script in your IDE (Rider / VS / VS Code).
2. Build the project (MSBuild's `DeployToBinScripts` target — baked into
   the user csproj template — copies the output to
   `<ProjectPath>/Bin/Scripts/`).
3. Either:
   - **Auto:** the editor's file watcher (Phase 16a) notices the DLL
     change, waits out the debounce window, and dispatches
     `O3DESharpRequestBus::ReloadUserAssemblies`. Live components on
     active entities drop their refs through
     `O3DESharpHotReloadNotificationBus`, the assembly load context is
     torn down and recreated, and components rebind to the new types.
     Inspector `[ExposedProperty]` fields re-reflect from the new
     assembly too. No editor action required.
   - **Manual:** Tools → C# Scripting → **Reload Scripts** triggers the
     same code path immediately. Useful when auto-reload is off or you
     want to be explicit.

To disable auto-reload for a session: Tools → C# Scripting → uncheck
**Reload Scripts on File Change**.

**Limitations**:
- Hot reload is only available in Debug and Profile builds. Release sets
  `CoralHostConfig::enableHotReload = false` and `ReloadUserAssemblies`
  becomes a no-op.
- Existing `.csproj` files written before Phase 16b don't have the
  `DeployToBinScripts` target. Run Tools → C# Scripting → **Migrate C#
  Project Files** once per project to add it; the existing csproj is
  backed up to `*.csproj.pre-deploy-target.bak`.

## Architecture

See the gems [Technical Design Document (In Progress).](https://hackmd.io/@MWD09WiVQ1O6VGcMVslq8w/rJHN3HjNZg/edit)

## C# Binding Generation Workflow

The binding generator (`Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/`)
has **two backends**, selected with `--source`:

> **Which backend should I use?**
> - **`--source reflection` (the default)** reads `reflection_data.json`,
>   a dump of O3DE's `BehaviorContext` produced by the editor at runtime —
>   the same public-API surface Lua / ScriptCanvas / Python already use.
>   No header parsing, no MSVC compatibility issues, no cross-gem include
>   walking. **This is the recommended backend for everything a C# script
>   would actually want to call.** Prerequisite: launch the O3DE Editor
>   for your project at least once first, so `AutoExportReflectionData`
>   writes out `reflection_data.json`; then pass its path explicitly with
>   `--reflection-data <path-to-reflection_data.json>` (the CLI's built-in
>   default search path is not guaranteed to match where your project's
>   editor build wrote the file — always pass `--reflection-data`
>   explicitly rather than relying on the default).
> - **`--source clang`** uses ClangSharp / libclang to parse C++ headers
>   directly, emitting `.g.cs` C# wrappers, an `InternalCalls.g.cs` interop
>   stub, a `BindingRegistration.g.cpp` Coral registration file, and a
>   `metadata.json` hot-reload manifest. It's heavier (fights MSVC
>   compatibility and cross-gem include paths) and can generate wrappers
>   that have no matching `BehaviorContext` dispatch path, which crash at
>   runtime if called. Use it only when you need a type a component
>   exposes in its header but hasn't reflected to `BehaviorContext`.

> **Note:** Earlier versions of this gem shipped a separate Python binding
> generator under `Editor/Scripts/` that consumed a BehaviorContext JSON dump.
> That generator is deprecated. The Python files under `Editor/Scripts/` that
> remain are thin orchestrators that shell out to the C# tool.

### Quick start

```powershell
# 1. Build the tool (first time only)
cd Gems/O3DESharp/Code/Tools/BindingGenerator/O3DESharp.BindingGenerator
dotnet build -c Release

# 2. Generate bindings for every enabled gem in your project (reflection
#    backend — launch the Editor once first to produce reflection_data.json)
dotnet run -- generate --project <path-to-your-O3DE-project> --source reflection --reflection-data <path-to-your-O3DE-project>/Generated/reflection_data.json

# 3. Generate bindings for specific gems only, using the ClangSharp header
#    parser instead
dotnet run -- generate --project <path-to-your-O3DE-project> --source clang --gems PhysX,Atom

# 4. Force a full regeneration (skip the incremental cache; --source clang only)
dotnet run -- generate --project <path-to-your-O3DE-project> --source clang --verbose --force
```

The generator is also wired into CMake as the `O3DESharp.GenerateBindings`
target and runs automatically when `O3DESHARP_AUTO_GENERATE_BINDINGS=ON`
(the default). Configuration lives in `binding_config.json` at the repo root.

For the end-to-end workflow — including how to compile the generated `.g.cs`
files into a per-gem DLL that your game scripts reference, MSBuild
design-time generation, and registering bindings on the C++ side — see
[GENERATED_BINDINGS_GUIDE.md](GENERATED_BINDINGS_GUIDE.md).

## Reflection System Details

The automated reflection system (Option B) works by:

1. **At Initialization**: The `BehaviorContextReflector` iterates over O3DE's `BehaviorContext` and extracts metadata about all reflected classes, methods, properties, and EBuses.

2. **On Query**: When C# code calls `NativeReflection.GetClassNames()` or similar, the cached metadata is returned.

3. **On Invocation**: When C# code calls a method dynamically:
   - Arguments are marshalled from C# types to native types
   - The `GenericDispatcher` looks up the `BehaviorMethod` and invokes it
   - Return values are marshalled back to C# types

4. **Type Mapping**: The system automatically handles:
   - Primitives: `bool`, `int`, `float`, `double`, `string`
   - Math types: `Vector3`, `Quaternion`, `Transform`
   - Entity types: `EntityId`
   - Complex objects: Passed as opaque handles

This approach means that:
- Any new type reflected to BehaviorContext is automatically accessible from C#
- No code generation or manual binding is required
- Lua-accessible APIs are also C#-accessible

## Known Limitations

- **Template Instantiations**: C++ template types (e.g., `AZStd::vector<AZ::Vector3>`, `AZ::RHI::Handle<uint>`) are automatically skipped during binding generation since they cannot be meaningfully represented as standalone C# classes. Use a `typedef` alias and reflect that instead.
- **Input System**: Direct input access is not yet fully implemented. Use O3DE's Input component with BehaviorContext for now.
- **EBus Handler param marshaling**: Phase 18-E ships first-class
  managed handlers via `[EBus]` / `[EBusHandler]` (see
  [SCRIPTING_GUIDE.md §9](SCRIPTING_GUIDE.md#9-calling-ebus-events-from-c)).
  The arg-unmarshal table covers primitives, `string`, `Guid` (for
  `AZ::Uuid`), `Vector2`, `Vector3`, `Quaternion`, and EntityId-shaped
  IDs. `Transform`, `Vector4`, `Color`, `Aabb`, `Matrix3x3` / `Matrix4x4`, and arbitrary
  user-defined structs currently arrive as `default(T)` with a warning
  in the console; extend `EBusHandlerRegistry.UnmarshalArg<T>` and the
  matching C++ marshal table to add coverage. Buses without a
  `Handler<>()` reflection (rare; emit-only buses) refuse `Register`
  with a clear log message.
- **Generics**: Generic types in BehaviorContext are not fully supported.
- **Performance**: The reflection API is slower than direct bindings. Use direct APIs for performance-critical code.
- **Asset references in `[ExposedProperty]`**: typed widgets exist for `bool`, integer types, `float`, `double`, and `string`. `Vector3` / `Quaternion` / `Color` / `EntityId` / `AssetReference<T>` are planned. The inspector falls back to a generic key/value editor for those.

## Troubleshooting

### ".NET runtime not found"

Ensure the .NET 9.0 SDK is installed and available in your PATH:
```bash
dotnet --version
```

### "Coral.Managed.dll not found" or "Failed to find Coral.Managed.runtimeconfig.json"

This error occurs when the Coral .NET hosting files haven't been deployed to your project's runtime directory.

**Solution 1: Use the C# Project Manager (Recommended)**

1. In the O3DE Editor, open the Python console (**Tools > Python Console**,
   or `View > Python Console` depending on your Editor build) and run:
   ```python
   import csharp_editor_bootstrap
   csharp_editor_bootstrap.open_csharp_project_manager()
   ```
   This opens the **C# Project Manager** dialog. (There is currently no
   registered `Tools >` menu entry for it — `register_menus()` in
   `Editor/Scripts/csharp_editor_bootstrap.py` only logs these Python
   console commands to the Console panel on Editor startup; it does not
   call the ActionManager API to add a real menu item yet.)
2. In the **Settings** section, check the **Deployment** status
3. Click **Deploy Coral** to automatically copy the required files
4. If auto-detection fails, click **Browse...** to manually select the Coral.Managed build output directory

**Solution 2: Manual Deployment**

Copy these files from the engine's build output to your project:

```
<ProjectPath>/Bin/Scripts/Coral/
├── Coral.Managed.dll
├── Coral.Managed.runtimeconfig.json
└── Coral.Managed.deps.json
```

The source files can be found at:
- CMake staging: `<Engine>/Gems/O3DESharp/bin/Coral/`
- Build output: `<BuildDir>/Build/<Config>/Coral.Managed.*`

**Solution 3: Configure via Settings Registry**

Add to your project's `Registry/o3desharp.setreg`:
```json
{
    "O3DE": {
        "O3DESharp": {
            "CoralDirectory": "C:/path/to/Coral.Managed/files"
        }
    }
}
```

### "Script class not found"

- Ensure the class name is fully qualified (e.g., `MyNamespace.MyClass`)
- Verify the assembly is built and in the correct location
- Check that the class inherits from `ScriptComponent`

### "Method not found in reflection"

- Ensure the method is reflected to BehaviorContext in C++
- Check that the method has the correct scope attributes
- Use `NativeReflection.MethodExists()` to verify availability

### Hot reload not working

- Hot reload is only available in Debug and Profile builds
- Ensure file watchers are not blocked by antivirus software
- Check the console for reload-related error messages

## Examples

See the `Assets/Scripts/Examples/` folder for complete examples:

- **PlayerController.cs**: Basic script demonstrating direct API usage
- **ReflectionExample.cs**: Demonstrates the automated reflection system

## Deploying and Exporting Projects

When exporting your O3DE project for distribution, O3DESharp automatically includes all necessary C# runtime DLLs and builds your user assemblies.

### Quick Export

For projects with C# scripts, use the O3DESharp export script:

```bash
o3de.py export-project \
  --export-script Gems/O3DESharp/ExportScripts/export_project_with_csharp.py \
  --project-path /path/to/your/project \
  --output-path /path/to/export \
  --config profile
```

This automatically:
1. Builds all user C# assemblies (Release configuration)
2. Deploys them to `Bin/Scripts/` in the export package
3. Includes Coral.Managed.dll and O3DE.Core.dll
4. Creates a complete, runnable game package

### Configuring User Assemblies

By default, O3DESharp auto-discovers all `.csproj` files in `Assets/Scripts/` (excluding O3DE.Core and Examples). For explicit control, create a Settings Registry configuration:

**File**: `<ProjectPath>/Registry/o3desharp.setreg`

```json
{
  "O3DE": {
    "O3DESharp": {
      "UserAssemblies": [
        {
          "ProjectPath": "Assets/Scripts/MyGame/MyGame.csproj",
          "AssemblyName": "MyGame.dll"
        },
        {
          "ProjectPath": "Assets/Scripts/Abilities/Abilities.csproj",
          "AssemblyName": "Abilities.dll"
        }
      ]
    }
  }
}
```

**Fields:**
- `ProjectPath`: Relative path from project root to `.csproj` file
- `AssemblyName`: Output DLL name (optional, defaults to project name + .dll)

### Export Package Structure

The exported launcher package includes all C# runtime files in the correct locations:

```
<ProjectName>GamePackage/
├── <ProjectName>.GameLauncher.exe
└── Bin/
    └── Scripts/
        ├── Coral/
        │   ├── Coral.Managed.dll
        │   ├── Coral.Managed.runtimeconfig.json
        │   └── Coral.Managed.deps.json
        ├── O3DE.Core.dll
        ├── O3DE.Core.deps.json
        ├── MyGame.dll              # Your user assemblies
        └── MyGame.deps.json
```

### Shipping without requiring .NET (experimental)

By default, O3DESharp is **framework-dependent**: Coral resolves a machine-wide
.NET 9 runtime install on the player's machine at startup (`hostfxr` →
`hostpolicy`, `rollForward: LatestMinor`). That's the supported path today and
is unchanged by everything below.

M2 adds an **opt-in, experimental** alternative: bundling a private,
self-contained CoreCLR next to the launcher so a shipped game can run C#
scripts on a machine with **no .NET installed at all**.

```bash
cmake -S . -B build -DO3DESHARP_BUNDLE_DOTNET_RUNTIME=ON
cmake --build build --target O3DESharp.StageRuntimeBundle
# Re-run configure once more so the produced bundle gets queued for deploy
# (its file list isn't known until the publish above has actually run):
cmake -S . -B build
```

What this does:
- Publishes `Code/Tools/RuntimeBundle/probe/probe.csproj` with `dotnet publish
  -c Release -r <rid> --self-contained true` for the desktop RID matching the
  host platform (`win-x64`, `linux-x64`, `osx-x64`, or `osx-arm64`), which
  harvests the private CoreCLR + hostfxr runtime pack for that RID.
- Stages the harvested runtime into the build tree and deploys it to
  `Bin/Scripts/dotnet/` alongside the existing `Bin/Scripts/Coral/` and
  `Bin/Scripts/O3DE.Core.dll` deployment.

Known limitations:
- **Per-RID.** Only desktop RIDs are in scope (no console/mobile).
- **Untrimmed.** The bundle is a full, untrimmed runtime (~70-200 MB); the
  reflection/JSON-based dispatch Coral uses fights the .NET trimmer, so
  trimming is out of scope for now.
- **Needs the Coral fork's `DotnetRootOverride` support.** Pointing Coral's
  hostfxr resolution at the bundled runtime instead of the machine-wide one
  requires a small change in the `WatchDogStudios/Coral` fork
  (`HostInstance::Initialize` honoring a `dotnet_root` override). That change
  is tracked separately and is **not yet landed**, so as of this writing the
  bundle is produced and deployed, but Coral does not yet consume it - the
  default framework-dependent resolution still runs even with the option on.
- **Experimental / not configure-verified in every environment.** The CMake
  step was verified by actually running the `dotnet publish` command it
  wraps (confirms `hostfxr`/`coreclr`/`System.Private.CoreLib.dll` are
  produced), but has not been exercised through a full `cmake --configure`
  against the O3DE engine SDK in every environment. Treat it as
  experimental until you've verified it in your own build.

The default (`O3DESHARP_BUNDLE_DOTNET_RUNTIME=OFF`) keeps today's
framework-dependent behavior: players still need .NET 9 installed.

### Development Workflow vs Export

**During Development:**
- Use the C# Project Manager tool (see [Troubleshooting](#coralmanageddll-not-found-or-failed-to-find-coralmanagedruntimeconfigjson) above for how to open it) to deploy DLLs to your project's `Bin/Scripts/` directory
- Iterate quickly by rebuilding C# projects and using hot reload

**For Export:**
- Use the custom export script (automatically builds and deploys everything)
- Produces a complete, distributable package with all dependencies

### Troubleshooting Export Issues

**Error: ".NET SDK not found"**

Install the .NET SDK from [https://dotnet.microsoft.com/download](https://dotnet.microsoft.com/download) and restart your terminal.

```bash
# Verify installation
dotnet --version
```

**Error: "Failed to build [ProjectName].csproj"**

Check the export log for C# compilation errors. Build manually to see detailed errors:

```bash
cd Assets/Scripts/MyGame
dotnet build -c Release
```

Common causes:
- Missing package references
- Syntax errors in C# code
- Incorrect O3DE.Core.dll reference path

**Export succeeds but launcher crashes on startup**

Verify all DLLs are present in the export package:

```bash
# Check for required DLLs
ls <ExportPath>/<Project>GamePackage/Bin/Scripts/
# Should show: Coral/, O3DE.Core.dll, and your user DLLs
```

**User assemblies not included**

- Check Settings Registry syntax (must be valid JSON)
- Verify `.csproj` paths are relative to project root
- Review export logs for "O3DESharp: Auto-discovered user project" messages

## Contributing

Contributions are welcome! Please see the main O3DE contributing guidelines.

## License

Licensed under Apache-2.0. See the LICENSE files for details.