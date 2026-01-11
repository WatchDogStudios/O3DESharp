# O3DESharp - C# Scripting for O3DE

O3DESharp is a Gem that adds C# scripting support to the Open 3D Engine (O3DE) using the [Coral](https://github.com/StudioCherno/Coral) .NET host library.

## Overview

O3DESharp enables game developers to write gameplay logic in C# instead of (or alongside) C++ and Lua. It provides:

- **Full .NET 8.0 Support**: Write scripts using modern C# features
- **Hot Reload**: Recompile and reload C# assemblies without restarting the editor
- **Native Interop**: Seamless communication between C++ and C# code
- **Familiar API**: Entity/Component model similar to other popular engines
- **Automated Reflection**: Automatic access to any type reflected to O3DE's BehaviorContext

## Requirements

- **.NET 8.0 SDK**: Download from [https://dotnet.microsoft.com/download](https://dotnet.microsoft.com/download)
- **Supported Platforms**: Windows x64, Linux x64 (JIT & AOT), iOS, Mac, A *very popular* blue gaming console, Xbox, Switch 1 & 2 (AOT Only)

## Installation

1. Enable the O3DESharp Gem in your project:
   ```bash
   o3de register --gem-path Gems/O3DESharp
   o3de enable-gem --gem-name O3DESharp --project-path /path/to/your/project
   ```

2. Rebuild your project

3. Build the C# Core API:
   ```bash
   cd Gems/O3DESharp/Assets/Scripts/O3DE.Core
   dotnet build -c Release
   ```

4. Copy the built assemblies to your project's script directory

## Quick Start

### Creating Your First C# Script

1. Create a new C# class library project:
   ```bash
   dotnet new classlib -n MyGameScripts -f net8.0
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

Dynamic access to **any** type reflected to O3DE's BehaviorContext. This allows you to call methods, access properties, and send EBus events without compile-time bindings:

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

The O3DESharp system can be configured via the Settings Registry:

```json
{
    "O3DE": {
        "O3DESharp": {
            "CoralDirectory": "path/to/Coral",
            "CoreApiAssemblyPath": "path/to/O3DE.Core.dll",
            "UserAssemblyPath": "path/to/GameScripts.dll"
        }
    }
}
```

## Hot Reload

O3DESharp supports hot-reloading of C# assemblies during development:

1. Make changes to your C# code
2. Rebuild the assembly
3. In the Editor, trigger a reload (exact mechanism TBD)

**Note**: Hot reload is only available in Debug and Profile builds.

## Architecture

See the gems [Technical Design Document (In Progress).](https://hackmd.io/@MWD09WiVQ1O6VGcMVslq8w/rJHN3HjNZg/edit)

## C# Binding Generation Workflow (Automated Through C# Project Manager!)

Steps

1. **C++ Reflection** (Runtime): The `BehaviorContextReflector` extracts metadata from O3DE's BehaviorContext
2. **JSON Export** (Runtime/Build): The `ReflectionDataExporter` exports this metadata to JSON
3. **Python Generation** (Build): Python scripts generate C# source files from the JSON
4. **Compilation**: The generated C# files are compiled into assemblies

### Step 1: Export Reflection Data from C++

In your O3DE application or Editor, export the reflection data:

```cpp
#include <Scripting/Reflection/BehaviorContextReflector.h>
#include <Scripting/Reflection/ReflectionDataExporter.h>

// Get the behavior context
AZ::BehaviorContext* behaviorContext = nullptr;
AZ::ComponentApplicationBus::BroadcastResult(
    behaviorContext, &AZ::ComponentApplicationRequests::GetBehaviorContext);

// Reflect all types
O3DESharp::BehaviorContextReflector reflector;
reflector.ReflectFromContext(behaviorContext);

// Export to JSON
O3DESharp::ReflectionDataExporter exporter;
O3DESharp::ReflectionExportConfig config;
config.outputPath = "reflection_data.json";
config.prettyPrint = true;

auto result = exporter.Export(reflector, config);
if (result.success)
{
    AZ_Printf("O3DESharp", "Exported %zu classes, %zu EBuses",
        result.classesExported, result.ebusesExported);
}
```

### Step 2: Generate C# Bindings with Python

Run the Python binding generator:

```bash
# Generate all bindings for a project
python Editor/Scripts/generate_bindings.py \
    --reflection-data reflection_data.json \
    --project /path/to/project \
    --output Generated/CSharp

# Generate bindings for specific gems only
python Editor/Scripts/generate_bindings.py \
    --reflection-data reflection_data.json \
    --project /path/to/project \
    --gems PhysX Atom ScriptCanvas

# Generate core bindings only (no gem organization)
python Editor/Scripts/generate_bindings.py \
    --reflection-data reflection_data.json \
    --core-only

# Generate with separate .csproj per gem
python Editor/Scripts/generate_bindings.py \
    --reflection-data reflection_data.json \
    --project /path/to/project \
    --per-gem-projects
```

### Step 3: Build Generated Assemblies

```bash
cd Generated/CSharp
dotnet build -c Release
```

### Output Structure

The generator creates an organized structure:

```
Generated/CSharp/
├── O3DE.Generated.sln           # Visual Studio solution
├── Core/                        # Core O3DE bindings
│   ├── O3DE.Core.csproj
│   ├── AssemblyInfo.cs
│   ├── Math.cs                  # Vector3, Quaternion, Transform
│   ├── Entity.cs                # Entity, EntityId, Component
│   ├── Core.cs                  # Debug, Time, etc.
│   └── Core.EBus.cs             # TransformBus, EntityBus
├── Atom/                        # Atom gem bindings
│   ├── Atom.csproj
│   ├── Rendering.cs
│   ├── Materials.cs
│   └── Rendering.EBus.cs
├── PhysX/                       # PhysX gem bindings
│   ├── PhysX.csproj
│   ├── RigidBody.cs
│   ├── Collision.cs
│   └── Physics.EBus.cs
├── ScriptCanvas/                # ScriptCanvas bindings
│   └── ...
└── InternalCalls.cs             # Native method declarations
```

## Gem-Aware Binding Generation

O3DESharp can automatically generate C# bindings organized by source gem, allowing you to see which classes come from which gems and generate per-gem assemblies.

### Enabling Gem-Aware Generation

```cpp
#include <Scripting/Reflection/BehaviorContextReflector.h>
#include <Scripting/Reflection/ReflectionDataExporter.h>

// 1. Reflect from BehaviorContext
BehaviorContextReflector reflector;
reflector.ReflectFromContext(behaviorContext);

// 2. Export to JSON for the Python generator
ReflectionDataExporter exporter;
ReflectionExportConfig config;
config.outputPath = "reflection_data.json";
config.prettyPrint = true;

auto result = exporter.Export(reflector, config);
if (result.success)
{
    AZ_Printf("O3DESharp", "Exported %zu classes, %zu EBuses",
        result.classesExported, result.ebusesExported);
}

// 3. Run Python generator (via command line or script)
// python Editor/Scripts/generate_bindings.py \
//     --reflection-data reflection_data.json \
//     --project /path/to/project \
//     --output Generated/CSharp
```

### Generating Bindings for Specific Gems

You can generate bindings for specific gems and their dependencies using Python:

```bash
# Generate bindings for a single gem (and its dependencies)
python Editor/Scripts/generate_bindings.py \
    --reflection-data reflection_data.json \
    --project /path/to/project \
    --gems MyGem

# Generate bindings for multiple specific gems
python Editor/Scripts/generate_bindings.py \
    --reflection-data reflection_data.json \
    --project /path/to/project \
    --gems PhysX Atom ScriptCanvas
```

### Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| `organizeByGem` | `true` | Group generated classes by source gem |
| `separateGemDirectories` | `true` | Create separate directories for each gem |
| `generatePerGemAssemblies` | `false` | Generate separate .csproj for each gem |
| `includeGems` | empty | List of gems to include (empty = all active) |
| `excludeGems` | empty | List of gems to exclude |
| `includeGemDependencies` | `true` | Include dependent gems in generation |
| `generateInterGemReferences` | `true` | Add using statements for dependent gems |

### Output Structure

With `separateGemDirectories = true`, the generated structure looks like:

```
Generated/CSharp/
├── Atom/
│   ├── Core.cs
│   ├── Rendering.cs
│   ├── Core.EBus.cs
│   ├── AssemblyInfo.cs
│   └── Atom.csproj (if generatePerGemAssemblies = true)
├── PhysX/
│   ├── Core.cs
│   ├── Collision.cs
│   ├── Core.EBus.cs
│   └── PhysX.csproj
├── ScriptCanvas/
│   └── ...
└── InternalCalls.cs
```

### Gem Dependency Resolution

The Python `GemDependencyResolver` class provides utilities for working with gem dependencies:

```python
from gem_dependency_resolver import GemDependencyResolver

resolver = GemDependencyResolver()
resolver.discover_gems_from_project("/path/to/project")

# Get all dependencies for a gem
deps = resolver.get_gem_dependencies("MyGem", include_transitive=True)

# Get gems that depend on a gem
dependents = resolver.get_gem_dependents("AzCore", include_transitive=True)

# Get gems in topological order (dependencies first)
ordered = resolver.get_gems_in_dependency_order()

# Check if one gem depends on another
depends = resolver.depends_on("PhysX", "AzCore")

# Map a class to its source gem
gem_name = resolver.resolve_gem_for_class("RigidBody", "Physics")
```

### Custom Class-to-Gem Mappings

You can configure how classes are mapped to gems in Python:

```python
from gem_dependency_resolver import GemDependencyResolver, GemMappingConfig

config = GemMappingConfig()
config.use_category_attribute = True   # Use BehaviorContext category
config.use_name_prefixes = True        # Use class name prefixes
config.default_gem_name = "O3DE.Core"  # Fallback gem name

# Add custom prefix mappings
config.prefix_mappings["MyPrefix"] = "MyGem"

# Add custom category mappings
config.category_mappings["MyCategory"] = "MyGem"

resolver = GemDependencyResolver(mapping_config=config)

# Register explicit class mappings
resolver.register_class_mapping("MySpecialClass", "MyGem")
```

### Python API Reference

The Python binding generator provides several modules:

#### gem_dependency_resolver.py

```python
from gem_dependency_resolver import GemDependencyResolver

resolver = GemDependencyResolver()

# Discover gems from a project
result = resolver.discover_gems_from_project("/path/to/project")

# Get all active gems
gems = resolver.get_active_gems()

# Get gem dependencies
deps = resolver.get_gem_dependencies("PhysX", include_transitive=True)

# Get gems in dependency order
ordered = resolver.get_gems_in_dependency_order()

# Resolve which gem a class belongs to
gem_name = resolver.resolve_gem_for_class("RigidBody", "Physics")
```

#### csharp_binding_generator.py

```python
from csharp_binding_generator import (
    CSharpBindingGenerator, 
    BindingGeneratorConfig,
    load_reflection_data_from_json
)

# Load reflection data
reflection_data = load_reflection_data_from_json("reflection_data.json")

# Configure generator
config = BindingGeneratorConfig()
config.output_directory = "Generated/CSharp"
config.root_namespace = "O3DE.Generated"
config.generate_core_bindings = True
config.generate_gem_bindings = True
config.separate_gem_directories = True

# Generate bindings
generator = CSharpBindingGenerator(config)
result = generator.generate_from_reflection_data(reflection_data, gem_resolver)

# Write to disk
files_written = generator.write_files()
```

#### generate_bindings.py (Main Entry Point)

```python
from generate_bindings import (
    generate_all_bindings,
    generate_gem_bindings,
    generate_core_bindings,
    list_available_gems
)

# Generate everything
result = generate_all_bindings(
    output_directory="Generated/CSharp",
    reflection_data_path="reflection_data.json",
    project_path="/path/to/project"
)

# Generate for a specific gem
result = generate_gem_bindings(
    gem_name="PhysX",
    output_directory="Generated/CSharp",
    reflection_data_path="reflection_data.json",
    project_path="/path/to/project"
)

# List available gems
gems = list_available_gems("/path/to/project")
```

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

- **Input System**: Direct input access is not yet fully implemented. Use O3DE's Input component with BehaviorContext for now.
- **EBus Handlers**: Creating C# EBus handlers (receiving events) is not yet supported. Only sending/broadcasting is available.
- **Generics**: Generic types in BehaviorContext are not fully supported.
- **Editor Integration**: Script class selection is currently a text field. A dropdown picker is planned.
- **Performance**: The reflection API is slower than direct bindings. Use direct APIs for performance-critical code.

## Troubleshooting

### ".NET runtime not found"

Ensure the .NET 8.0 SDK is installed and available in your PATH:
```bash
dotnet --version
```

### "Coral.Managed.dll not found" or "Failed to find Coral.Managed.runtimeconfig.json"

This error occurs when the Coral .NET hosting files haven't been deployed to your project's runtime directory.

**Solution 1: Use the C# Script Manager (Recommended)**

1. In the O3DE Editor, go to **Tools > C# Script Manager**
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

## Contributing

Contributions are welcome! Please see the main O3DE contributing guidelines.

## License

Licensed under Apache-2.0. See the LICENSE files for details.