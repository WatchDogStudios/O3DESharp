# O3DESharp Binding Generator - Advanced Features

## Table of Contents

1. [Incremental Build System](#incremental-build-system)
2. [Runtime Metadata Generation](#runtime-metadata-generation)
3. [Hot Reload Support](#hot-reload-support)
4. [Fluent API Extension Methods](#fluent-api-extension-methods)
5. [Default Parameter Values](#default-parameter-values)
6. [MSBuild Task Integration](#msbuild-task-integration)

---

## Incremental Build System

The binding generator now supports incremental builds using SHA256 file hashing and a JSON cache file.

### How It Works

- **File Hashing**: Each header file is hashed using SHA256
- **Configuration Hashing**: The binding configuration is also hashed
- **Cache Storage**: Hashes are stored in `.binding_cache.json`
- **Change Detection**: Only regenerates when files or config change

### Usage

```bash
# Normal build (uses cache)
O3DESharp.BindingGenerator generate --project ./project.json

# Force full rebuild
O3DESharp.BindingGenerator generate --project ./project.json --force

# Disable incremental builds
O3DESharp.BindingGenerator generate --project ./project.json --incremental false
```

### Configuration

In `binding_config.json`:

```json
{
  "global": {
    "incrementalBuild": true,
    "cacheFilePath": ".binding_cache.json"
  }
}
```

### Files Created

- [Caching/FileHasher.cs](Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Caching/FileHasher.cs) - SHA256 hash computation
- [Caching/BuildCache.cs](Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Caching/BuildCache.cs) - Cache management

---

## Runtime Metadata Generation

The generator now produces runtime reflection metadata for each gem, enabling hot reload and dynamic binding resolution.

### Generated Files

For each gem, the following files are generated:

- `Metadata.g.cs` - Strongly-typed metadata classes
- `metadata.json` - JSON metadata for tooling

### Example Usage

```csharp
using O3DE.MyGem;

// Get class metadata
var classInfo = BindingMetadata.GetClass("PhysicsComponent");
if (classInfo != null)
{
    Console.WriteLine($"Methods: {classInfo.Methods.Length}");
    Console.WriteLine($"Properties: {classInfo.Properties.Length}");
}

// Enumerate all bindings
foreach (var cls in BindingMetadata.Classes)
{
    Console.WriteLine($"Class: {cls.Name} ({cls.Methods.Length} methods)");
}
```

### Configuration

In `binding_config.json`:

```json
{
  "global": {
    "generateMetadata": true
  }
}
```

### Files Created

- [Generation/MetadataGenerator.cs](Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Generation/MetadataGenerator.cs)

---

## Hot Reload Support

The binding system now supports hot reloading C# script assemblies without restarting the editor.

### Features

- **State Preservation**: Serializable field values are saved before unload
- **Assembly Versioning**: Reload generation tracking for debugging
- **Event System**: Subscribe to reload events for custom handling
- **Automatic Registration**: Generated C++ code includes hot reload hooks

### C# HotReloadManager API

```csharp
using O3DE.Core.HotReload;

// Register a script for hot reload tracking
HotReloadManager.Instance.RegisterScript("MyScript_123", myScript, "MyGem");

// Subscribe to reload events
HotReloadManager.Instance.ScriptReloading += (sender, args) =>
{
    if (args.IsBeforeReload)
        SaveCustomState();
};

HotReloadManager.Instance.ScriptReloaded += (sender, args) =>
{
    RestoreCustomState();
};

// Get current reload generation
int gen = HotReloadManager.Instance.ReloadGeneration;
```

### C++ Hot Reload Hooks

The generated C++ code includes callbacks for native integration:

```cpp
#include "MyGem_HotReload.g.h"

// Set up callbacks
MyGem::Generated::HotReloadCallbacks::SetBeforeUnloadCallback(
    [](const AZStd::string& gemName) {
        // Save native state
    });

MyGem::Generated::HotReloadCallbacks::SetAfterLoadCallback(
    [](const AZStd::string& gemName) {
        // Restore native state
    });
```

### Files Created

- [Assets/Scripts/O3DE.Core/HotReload/HotReloadManager.cs](Assets/Scripts/O3DE.Core/HotReload/HotReloadManager.cs)
- Generated: `{GemName}_HotReload.g.h`

---

## Fluent API Extension Methods

The generator creates extension methods for a fluent/builder API pattern.

### Generated Extensions

- `SetX()` methods get `WithX()` fluent wrappers
- Properties get `WithPropertyName()` setters
- Void methods with common prefixes get `*Fluent()` wrappers

### Example Usage

```csharp
// Traditional API
var transform = new Transform();
transform.SetPosition(new Vector3(0, 0, 0));
transform.SetRotation(Quaternion.Identity);
transform.SetScale(new Vector3(1, 1, 1));

// Fluent API
var transform = new Transform()
    .WithPosition(new Vector3(0, 0, 0))
    .WithRotation(Quaternion.Identity)
    .WithScale(new Vector3(1, 1, 1));
```

### Configuration

In `binding_config.json`:

```json
{
  "global": {
    "generateExtensionMethods": true
  }
}
```

### Files Created

- [Generation/ExtensionMethodGenerator.cs](Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Generation/ExtensionMethodGenerator.cs)
- Generated: `FluentExtensions.g.cs` per gem

---

## Default Parameter Values

C++ default parameter values are now converted to C# optional parameters.

### Supported Conversions

| C++ | C# |
|-----|-----|
| `nullptr` | `null` |
| `true`/`false` | `true`/`false` |
| `1.0f` | `1.0f` |
| `MyEnum::Value` | `MyEnum.Value` |
| `AZ::Vector3(0,0,0)` | `new Vector3(0,0,0)` |
| `{}` | `default` |

### Example

C++ Header:
```cpp
void SetPosition(const AZ::Vector3& pos = AZ::Vector3::Zero);
void SetEnabled(bool enabled = true);
void SetName(const char* name = nullptr);
```

Generated C#:
```csharp
public void SetPosition(Vector3 pos = default);
public void SetEnabled(bool enabled = true);
public void SetName(string? name = null);
```

### Files Modified

- [Generation/CSharpCodeGenerator.cs](Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Generation/CSharpCodeGenerator.cs)

---

## MSBuild Task Integration

An MSBuild task enables design-time binding generation for IntelliSense support.

### Installation

Reference the NuGet package in your `.csproj`:

```xml
<PackageReference Include="O3DESharp.BindingGenerator.Tasks" Version="1.0.0" />
```

Or set the path manually:

```xml
<PropertyGroup>
  <O3DEBindingGeneratorTasksPath>path/to/O3DESharp.BindingGenerator.Tasks.targets</O3DEBindingGeneratorTasksPath>
</PropertyGroup>
<Import Project="$(O3DEBindingGeneratorTasksPath)" />
```

### Configuration Properties

| Property | Default | Description |
|----------|---------|-------------|
| `O3DEProjectPath` | Auto | Path to project.json or gem.json |
| `O3DEBindingConfig` | Auto | Path to binding_config.json |
| `O3DEGeneratorPath` | Auto | Path to generator executable |
| `O3DEGenerateBindings` | `true` | Enable build-time generation |
| `O3DEDesignTimeGenerate` | `true` | Enable design-time generation |
| `O3DEIncremental` | `true` | Use incremental builds |
| `O3DEForceRegen` | `false` | Force full regeneration |
| `O3DEVerbose` | `false` | Verbose output |
| `O3DECleanBindings` | `false` | Clean generated files on `dotnet clean` |

### Specific Gems

```xml
<ItemGroup>
  <O3DEGems Include="MyGem" />
  <O3DEGems Include="AnotherGem" />
</ItemGroup>
```

### Files Created

- [O3DESharp.BindingGenerator.Tasks.csproj](Code/Tools/BindingGenerator/O3DESharp.BindingGenerator.Tasks/O3DESharp.BindingGenerator.Tasks.csproj)
- [GenerateBindingsTask.cs](Code/Tools/BindingGenerator/O3DESharp.BindingGenerator.Tasks/GenerateBindingsTask.cs)
- [build/O3DESharp.BindingGenerator.Tasks.targets](Code/Tools/BindingGenerator/O3DESharp.BindingGenerator.Tasks/build/O3DESharp.BindingGenerator.Tasks.targets)
- [build/O3DESharp.BindingGenerator.Tasks.props](Code/Tools/BindingGenerator/O3DESharp.BindingGenerator.Tasks/build/O3DESharp.BindingGenerator.Tasks.props)

---

## Building

Build all components:

```bash
# Build the binding generator
dotnet build Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/O3DESharp.BindingGenerator.csproj -c Release

# Build the MSBuild tasks package
dotnet pack Code/Tools/BindingGenerator/O3DESharp.BindingGenerator.Tasks/O3DESharp.BindingGenerator.Tasks.csproj -c Release

# Build O3DE.Core with hot reload support
dotnet build Assets/Scripts/O3DE.Core/O3DE.Core.csproj -c Release
```

Or use the solution:

```bash
dotnet build O3DESharp.sln -c Release
```

---

## Version History

### v1.0.0
- Initial implementation of all advanced features
- Incremental build with SHA256 hashing
- Runtime metadata generation (C# and JSON)
- Hot reload support with state preservation
- Fluent API extension methods
- Default parameter value conversion
- MSBuild task for design-time generation
