# Using Generated C# Bindings in Your O3DE Project

This guide explains how to run the O3DESharp Binding Generator, compile the
generated `.g.cs` wrappers into a reusable DLL, and reference that DLL from your
game scripts.

---

## Table of Contents

1. [Overview — What Gets Generated](#1-overview--what-gets-generated)
2. [Running the Binding Generator](#2-running-the-binding-generator)
3. [Understanding the Output](#3-understanding-the-output)
4. [Packaging Generated Bindings into a DLL](#4-packaging-generated-bindings-into-a-dll)
5. [Referencing the Bindings DLL from Game Scripts](#5-referencing-the-bindings-dll-from-game-scripts)
6. [Automatic Generation via MSBuild (Design-Time)](#6-automatic-generation-via-msbuild-design-time)
7. [Registering Bindings on the C++ Side](#7-registering-bindings-on-the-c-side)
8. [Worked Example: End-to-End Workflow](#8-worked-example-end-to-end-workflow)
9. [Configuration Reference](#9-configuration-reference)
10. [Troubleshooting](#10-troubleshooting)

---

## 1. Overview — What Gets Generated

The Binding Generator reads your C++ headers (using ClangSharp / libclang) and
produces:

| Output File | Language | Purpose |
|-------------|----------|---------|
| `InternalCalls.g.cs` | C# | `delegate* unmanaged<>` function-pointer fields — the raw native bridge |
| `{ClassName}.g.cs` | C# | Type-safe wrapper class with methods, properties, XML docs |
| `{EnumName}.g.cs` | C# | C# enum mirroring a C++ `enum class` |
| `FluentExtensions.g.cs` | C# | `With*()` builder methods for fluent chaining |
| `Metadata.g.cs` + `metadata.json` | C# / JSON | Runtime reflection metadata (used by hot reload) |
| `{GemName}.csproj` | MSBuild | A pre-configured project that compiles all the above into a DLL |
| `BindingRegistration.g.cpp` | C++ | Coral `AddInternalCall` + `UploadInternalCalls` registration |
| `{GemName}_HotReload.g.h` | C++ | Re-registration callbacks for hot reload |

**Key idea:** the generated `.g.cs` files should be compiled into a **DLL**
(one per Gem) that your game scripts reference — just like you reference
`O3DE.Core.dll` today.

---

## 2. Running the Binding Generator

### Prerequisites

```
.NET 8.0+ SDK            (dotnet --version)
ClangSharp 17.0.1        (pulled automatically by NuGet on first build)
```

### Build the Tool (first time only)

```powershell
cd Gems/O3DESharp/Code/Tools/BindingGenerator/O3DESharp.BindingGenerator
dotnet build -c Release
```

### Create a Default Config (if you don't have one)

```powershell
dotnet run -- init-config
# Creates binding_config.json in the current directory
```

### Generate Bindings

```powershell
# All enabled gems:
dotnet run -- generate --project F:\o3de

# Specific gems only:
dotnet run -- generate --project F:\o3de --gems MyGem,PhysicsGem

# Verbose output + force full regeneration:
dotnet run -- generate --project F:\o3de --verbose --force
```

You can also generate via the **Python orchestrator** inside the O3DE Editor:

```python
# Editor Console / Python script
from Editor.Scripts.csharp_binding_generator import ClangSharpInvoker
invoker = ClangSharpInvoker()
result = invoker.generate(project_path="F:/o3de", verbose=True)
print(f"Generated {result.total_files} files for {result.total_classes} classes")
```

### Generate *and* Build DLLs in One Step (Python Orchestrator)

The Python orchestrator can also generate per-gem `.csproj` files and compile
them into DLLs automatically:

```powershell
# Generate bindings + build DLLs in one command
python Editor/Scripts/generate_bindings.py --project F:\o3de --build-dlls

# Combine with other flags
python Editor/Scripts/generate_bindings.py --project F:\o3de --gems PhysX Atom --build-dlls --verbose
```

Or programmatically:

```python
from generate_bindings import BindingGenerationOrchestrator

orch = BindingGenerationOrchestrator()
orch.configure(output_directory="Generated/CSharp")
orch.load_reflection_data("reflection_data.json")
orch.generate()
orch.write_files()

# Generate per-gem .csproj files and compile them
csproj_paths = orch.generate_per_gem_projects()
results = orch.build_binding_dlls(csproj_paths)
for gem, ok in results.items():
    print(f"{gem}: {'OK' if ok else 'FAILED'}")
```

### What the Tool Prints

```
[MultiGem] Generating bindings for gem 'PhysicsGem'
[CSharpGen] Generated: Assets/Scripts/PhysicsGem/InternalCalls.g.cs
[CSharpGen] Generated: Assets/Scripts/PhysicsGem/RigidBodyComponent.g.cs
[CSharpGen] Generated: Assets/Scripts/PhysicsGem/ColliderComponent.g.cs
[CSharpGen] Generated: Assets/Scripts/PhysicsGem/ExampleState.g.cs
[CSharpGen] Generated 3 wrapper classes and 1 enums
[CppGen]   Generated: Code/Source/Scripting/Generated/BindingRegistration.g.cpp
[CppGen]   Generated: Code/Source/Scripting/Generated/PhysicsGem_HotReload.g.h
[ExtGen]   Generated: Assets/Scripts/PhysicsGem/FluentExtensions.g.cs (5 extension methods)
[MetaGen]  Generated: Assets/Scripts/PhysicsGem/Metadata.g.cs
[ProjGen]  Generated: Assets/Scripts/PhysicsGem/PhysicsGem.csproj
```

---

## 3. Understanding the Output

### Generated Wrapper Class

For a C++ header like:

```cpp
// RigidBodyComponent.h
class RigidBodyComponent
{
public:
    AZ::Vector3 GetLinearVelocity() const;
    void SetLinearVelocity(const AZ::Vector3& velocity);
    float GetMass() const;
    void ApplyForce(const AZ::Vector3& force);
    bool IsKinematic;
};
```

The generator produces:

**`RigidBodyComponent.g.cs`** — the class you use in game code:

```csharp
// AUTO-GENERATED FILE - DO NOT EDIT
using System;
using Coral.Managed.Interop;

namespace O3DE.PhysicsGem
{
    /// <summary>
    /// RigidBodyComponent
    /// </summary>
    public class RigidBodyComponent
    {
        public bool IsKinematic { get; set; }

        /// <summary>
        /// Get the linear velocity.
        /// </summary>
        public unsafe Vector3 GetLinearVelocity()
        {
            return InternalCalls.RigidBodyComponent_GetLinearVelocity(/* this pointer */);
        }

        /// <summary>
        /// Set the linear velocity.
        /// </summary>
        public unsafe void SetLinearVelocity(Vector3 velocity)
        {
            InternalCalls.RigidBodyComponent_SetLinearVelocity(/* this pointer */, velocity);
        }

        public unsafe float GetMass()
        {
            return InternalCalls.RigidBodyComponent_GetMass(/* this pointer */);
        }

        public unsafe void ApplyForce(Vector3 force)
        {
            InternalCalls.RigidBodyComponent_ApplyForce(/* this pointer */, force);
        }
    }
}
```

**`InternalCalls.g.cs`** — the raw function pointers (Coral binds these at load):

```csharp
// AUTO-GENERATED FILE - DO NOT EDIT
using System;
using System.Runtime.InteropServices;
using Coral.Managed.Interop;

namespace O3DE.PhysicsGem
{
    /// <summary>
    /// Internal calls to native PhysicsGem C++ functions.
    /// DO NOT call these directly - use the wrapper classes instead.
    /// </summary>
    internal static unsafe class InternalCalls
    {
#pragma warning disable 0649
        // RigidBodyComponent Functions
        internal static delegate* unmanaged<IntPtr, Vector3> RigidBodyComponent_GetLinearVelocity;
        internal static delegate* unmanaged<IntPtr, Vector3, void> RigidBodyComponent_SetLinearVelocity;
        internal static delegate* unmanaged<IntPtr, float> RigidBodyComponent_GetMass;
        internal static delegate* unmanaged<IntPtr, Vector3, void> RigidBodyComponent_ApplyForce;
#pragma warning restore 0649
    }
}
```

**`FluentExtensions.g.cs`** — fluent builder methods:

```csharp
namespace O3DE.PhysicsGem
{
    public static class FluentExtensions
    {
        /// <summary>
        /// Fluent: Set linear velocity and return the same object for chaining.
        /// </summary>
        public static RigidBodyComponent WithLinearVelocity(
            this RigidBodyComponent self, Vector3 velocity)
        {
            self.SetLinearVelocity(velocity);
            return self;
        }
    }
}
```

### Generated Enums

```csharp
namespace O3DE.PhysicsGem
{
    public enum ExampleState
    {
        Idle = 0,
        Running = 1,
        Paused = 2,
        Stopped = 3
    }
}
```

### Generated `.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <AssemblyName>PhysicsGem</AssemblyName>
    <RootNamespace>O3DE.PhysicsGem</RootNamespace>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="O3DE.Core">
      <HintPath>../../bin/O3DE.Core/O3DE.Core.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

---

## 4. Packaging Generated Bindings into a DLL

### Automated Build (Recommended)

If you used `--build-dlls` during generation (see Section 2), the DLLs are
already compiled. The orchestrator generates a `.csproj` per gem and runs
`dotnet build -c Release` on each one.

Each generated `.csproj` looks like:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>O3DE.Bindings.PhysicsGem</AssemblyName>
    <RootNamespace>O3DE.PhysicsGem</RootNamespace>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <EnableDefaultCompileItems>true</EnableDefaultCompileItems>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="O3DE.Core">
      <HintPath>...auto-detected.../O3DE.Core.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
```

The orchestrator automatically locates `O3DE.Core.dll` and adds a `<Reference>`
so the generated code can resolve the core API types.

### Manual Build

The generated `.csproj` is also ready to build manually:

```powershell
cd Assets/Scripts/PhysicsGem        # or wherever the generated .csproj lives
dotnet build -c Release
```

Output:

```
Assets/Scripts/PhysicsGem/
├── bin/
│   └── Release/
│       └── net8.0/
│           ├── PhysicsGem.dll          ← this is your bindings DLL
│           └── PhysicsGem.xml          ← XML docs for IntelliSense
├── InternalCalls.g.cs
├── RigidBodyComponent.g.cs
├── FluentExtensions.g.cs
├── Metadata.g.cs
├── metadata.json
└── PhysicsGem.csproj
```

### Deploy the DLL

Copy the built DLL into your project's scripting directory so the engine loads it:

```powershell
copy bin\Release\net8.0\PhysicsGem.dll  ..\..\Bin\Scripts\PhysicsGem.dll
```

Or set the output path directly in the `.csproj`:

```xml
<PropertyGroup>
  <OutputPath>..\..\Bin\Scripts\</OutputPath>
  <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
</PropertyGroup>
```

### Multi-Gem: Building All Binding DLLs at Once

If you have bindings for multiple gems, create a solution:

```powershell
dotnet new sln -n O3DEBindings
dotnet sln add Assets/Scripts/PhysicsGem/PhysicsGem.csproj
dotnet sln add Assets/Scripts/ScriptCanvasGem/ScriptCanvasGem.csproj
dotnet sln add Assets/Scripts/AudioGem/AudioGem.csproj
dotnet build O3DEBindings.sln -c Release
```

Gem-to-gem dependencies are handled automatically: if `PhysicsGem` depends on
`MathGem`, the generated `PhysicsGem.csproj` already has
`<ProjectReference Include="../MathGem/MathGem.csproj" />`.

---

## 5. Referencing the Bindings DLL from Game Scripts

Now your game script project just adds a `<Reference>` (or `<ProjectReference>`)
to the bindings DLL:

### Option A: Reference the pre-built DLL

```xml
<!-- MyGame.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <!-- The hand-written core scripting API -->
    <Reference Include="O3DE.Core">
      <HintPath>../../Bin/Scripts/O3DE.Core.dll</HintPath>
    </Reference>

    <!-- The generated binding DLLs -->
    <Reference Include="PhysicsGem">
      <HintPath>../../Bin/Scripts/PhysicsGem.dll</HintPath>
    </Reference>
    <Reference Include="AudioGem">
      <HintPath>../../Bin/Scripts/AudioGem.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
```

### Option B: Reference as a ProjectReference (source builds)

If both projects live in the same solution (recommended during development):

```xml
<ItemGroup>
  <Reference Include="O3DE.Core">
    <HintPath>../../Bin/Scripts/O3DE.Core.dll</HintPath>
  </Reference>
  <ProjectReference Include="../PhysicsGem/PhysicsGem.csproj" />
</ItemGroup>
```

This way, `dotnet build` on your game project automatically rebuilds the binding
DLL when headers change (if MSBuild task integration is enabled — see next section).

### Using the Bindings in Code

```csharp
using O3DE;
using O3DE.PhysicsGem;   // ← the generated namespace

namespace MyGame
{
    public class PhysicsDemo : ScriptComponent
    {
        public override void OnCreate()
        {
            Debug.Log("PhysicsDemo starting");
        }

        public override void OnUpdate(float deltaTime)
        {
            // Use the generated wrapper class (type-safe, zero-overhead)
            var body = new RigidBodyComponent();
            Vector3 velocity = body.GetLinearVelocity();

            if (velocity.Magnitude > 10f)
            {
                body.ApplyForce(-velocity.Normalized * 5f);
            }

            // Fluent builder pattern (from FluentExtensions.g.cs)
            body.WithLinearVelocity(new Vector3(0, 5, 0));

            // Enums work naturally
            ExampleState state = ExampleState.Running;
            if (state == ExampleState.Paused)
                Debug.Log("Game is paused");
        }
    }
}
```

### Side-by-Side: Direct Bindings vs Reflection API

| | Generated Bindings (DLL) | Reflection API (NativeReflection) |
|---|---|---|
| **Setup** | Run generator → build DLL → add reference | None — works out of the box |
| **Type safety** | Full compile-time checking | Runtime strings, `object?` returns |
| **IntelliSense** | Full (XML docs, parameter names) | None |
| **Performance** | `delegate* unmanaged<>` — zero marshalling | JSON serialization per call |
| **When to use** | Any component you use frequently | One-off queries, prototyping, dynamic access |

**Rule of thumb:** Use generated bindings for anything you call per-frame.
Use the Reflection API for infrequent or exploratory access.

---

## 6. Automatic Generation via MSBuild (Design-Time)

The generator ships with an **MSBuild task package**
(`O3DESharp.BindingGenerator.Tasks`) that can automatically re-generate bindings
every time you build — including during IntelliSense (design-time) builds.

### Setup

1. Build and pack the task NuGet:

```powershell
cd Code/Tools/BindingGenerator/O3DESharp.BindingGenerator.Tasks
dotnet build -c Release
# Produces: bin/Release/O3DESharp.BindingGenerator.Tasks.{version}.nupkg
```

2. Add a local NuGet source:

```powershell
dotnet nuget add source ./packages --name O3DELocal
copy bin\Release\*.nupkg  .\packages\
```

3. Reference the package from your binding `.csproj`:

```xml
<PackageReference Include="O3DESharp.BindingGenerator.Tasks" Version="1.0.0" />
```

Or use the **Import** approach (already present in the generated `.csproj`):

```xml
<Import Project="$(O3DEBindingGeneratorTasksPath)"
        Condition="Exists('$(O3DEBindingGeneratorTasksPath)')" />
```

### What Happens Automatically

| Build Phase | MSBuild Target | What It Does |
|-------------|---------------|--------------|
| Before Compile | `O3DEGenerateBindings` | Runs the generator; includes `*.g.cs` in `<Compile>` |
| Design-Time | `O3DEDesignTimeGenerate` | Same, but incremental-only — keeps IntelliSense fast |
| Clean | `O3DECleanBindings` | Deletes all `*.g.cs`, `*.g.cpp`, `*.g.h`, `metadata.json` |

### MSBuild Properties You Can Override

Set these in your `.csproj` `<PropertyGroup>` to customize behavior:

| Property | Default | Description |
|----------|---------|-------------|
| `O3DEProjectPath` | `../../project.json` | Path to project.json or gem.json |
| `O3DEBindingConfig` | `../../binding_config.json` | Path to config file |
| `O3DEGeneratorPath` | *(auto-detected)* | Path to the generator executable |
| `O3DEOutputPath` | `../../Assets/Scripts` | Where generated files go |
| `O3DEGenerateBindings` | `true` | Master enable/disable |
| `O3DEDesignTimeGenerate` | `true` | IntelliSense during editing |
| `O3DEIncremental` | `true` | Skip unchanged files (hash-based) |
| `O3DEForceRegen` | `false` | Force full regeneration |
| `O3DEVerbose` | `false` | Detailed generator output |
| `O3DECleanBindings` | `false` | Remove generated files on `dotnet clean` |

### Selecting Specific Gems

```xml
<ItemGroup>
  <O3DEGems Include="PhysicsGem" />
  <O3DEGems Include="AudioGem" />
</ItemGroup>
```

Leave empty to process all enabled gems in `binding_config.json`.

---

## 7. Registering Bindings on the C++ Side

The generated C++ file (`BindingRegistration.g.cpp`) must be called from your
gem's module to wire up the function pointers that the C# InternalCalls expect.

### Include the Generated Registration

In your gem's system component or module initialization:

```cpp
#include "Scripting/Generated/BindingRegistration.g.cpp"

void MyGemSystemComponent::Activate()
{
    // After Coral is initialized and the managed assembly is loaded:
    Coral::ManagedAssembly* assembly = GetLoadedAssembly("PhysicsGem");
    PhysicsGem::Generated::RegisterBindings(assembly);
}
```

### What RegisterBindings Does

```cpp
// Generated — BindingRegistration.g.cpp
namespace PhysicsGem::Generated
{
    void RegisterBindings(Coral::ManagedAssembly* assembly)
    {
        assembly->AddInternalCall("PhysicsGem.InternalCalls",
            "RigidBodyComponent_GetLinearVelocity",
            reinterpret_cast<void*>(&RigidBodyComponent_GetLinearVelocity));

        assembly->AddInternalCall("PhysicsGem.InternalCalls",
            "RigidBodyComponent_SetLinearVelocity",
            reinterpret_cast<void*>(&RigidBodyComponent_SetLinearVelocity));

        // ... all other methods ...

        assembly->UploadInternalCalls();
    }
}
```

Each `AddInternalCall` maps a fully-qualified C# field name
(`"PhysicsGem.InternalCalls"` + `"RigidBodyComponent_GetLinearVelocity"`) to the
address of a native C++ function. `UploadInternalCalls()` commits them all at once
to the Coral runtime.

### Hot Reload Support

The generated `{GemName}_HotReload.g.h` provides:

```cpp
namespace PhysicsGem::Generated
{
    void UnregisterBindings(Coral::ManagedAssembly* assembly);
    bool HotReload(Coral::ManagedAssembly* oldAssembly, Coral::ManagedAssembly* newAssembly);
}
```

Call `HotReload(old, new)` when the assembly is reloaded to re-register the
function pointers with the new assembly.

---

## 8. Worked Example: End-to-End Workflow

This section walks through generating bindings for a hypothetical "AudioGem" and
using them in a game project.

### Step 1 — Configure

Edit `binding_config.json` (or create one with `dotnet run -- init-config`):

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
    "AudioGem": {
      "enabled": true,
      "headerPatterns": [
        "Gems/AudioGem/Code/Include/**/*.h"
      ],
      "excludePatterns": [
        "**/Platform/**",
        "**/Tests/**"
      ]
    }
  }
}
```

### Step 2 — Generate

```powershell
cd F:\o3de\Gems\O3DESharp\Code\Tools\BindingGenerator\O3DESharp.BindingGenerator

dotnet run -- generate `
  --project F:\o3de `
  --config F:\o3de\binding_config.json `
  --gems AudioGem `
  --verbose
```

Output:
```
[Discover] Found gem: AudioGem (F:\o3de\Gems\AudioGem)
[Parser]   Parsing: AudioTriggerComponent.h (3 classes, 1 enum)
[CSharpGen] Generated: Assets/Scripts/AudioGem/InternalCalls.g.cs
[CSharpGen] Generated: Assets/Scripts/AudioGem/AudioTriggerComponent.g.cs
[CSharpGen] Generated: Assets/Scripts/AudioGem/AudioEnvironmentComponent.g.cs
[CSharpGen] Generated: Assets/Scripts/AudioGem/AudioState.g.cs
[ExtGen]   Generated: Assets/Scripts/AudioGem/FluentExtensions.g.cs
[ProjGen]  Generated: Assets/Scripts/AudioGem/AudioGem.csproj
[CppGen]   Generated: Code/Source/Scripting/Generated/BindingRegistration.g.cpp
```

### Step 3 — Build the Bindings DLL

```powershell
cd F:\o3de\Assets\Scripts\AudioGem
dotnet build -c Release

# DLL is now at: bin/Release/net8.0/AudioGem.dll
```

### Step 4 — Deploy

```powershell
copy bin\Release\net8.0\AudioGem.dll  F:\MyProject\Bin\Scripts\AudioGem.dll
```

### Step 5 — Reference from Your Game Project

**MyGame.csproj:**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <OutputPath>..\..\Bin\Scripts\</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="O3DE.Core">
      <HintPath>..\..\Bin\Scripts\O3DE.Core.dll</HintPath>
    </Reference>
    <Reference Include="AudioGem">
      <HintPath>..\..\Bin\Scripts\AudioGem.dll</HintPath>
    </Reference>
    <Reference Include="Coral.Managed">
      <HintPath>..\..\Bin\Scripts\Coral\Coral.Managed.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

### Step 6 — Write Game Code

```csharp
using O3DE;
using O3DE.AudioGem;

namespace MyGame
{
    public class AudioPlayer : ScriptComponent
    {
        public override void OnCreate()
        {
            // Use the generated type-safe wrapper
            var trigger = new AudioTriggerComponent();
            trigger.ExecuteTrigger("Play_Ambient");

            // Fluent chaining
            trigger
                .WithVolume(0.8f)
                .WithPitch(1.0f);

            // Generated enums
            if (trigger.GetState() == AudioState.Playing)
                Debug.Log("Audio is playing!");
        }

        public override void OnDestroy()
        {
            var trigger = new AudioTriggerComponent();
            trigger.ExecuteTrigger("Stop_Ambient");
        }
    }
}
```

### Step 7 — Build & Run

```powershell
cd F:\MyProject\Assets\Scripts\MyGame
dotnet build -c Debug
# → outputs Bin/Scripts/MyGame.dll (via OutputPath above)
```

Launch the O3DE Editor, add a C# Script Component with class `MyGame.AudioPlayer`,
and enter Game Mode.

---

## 9. Configuration Reference

### `binding_config.json` — Full Schema

```jsonc
{
  "global": {
    // Root C# namespace prefix. Generated code lives under O3DE.{GemName}.
    "cSharpNamespace": "O3DE",

    // Where generated .g.cs + .csproj go. {GemName} is replaced per-gem.
    "cSharpOutputPath": "Assets/Scripts/{GemName}",

    // Where generated .g.cpp + .g.h go (checked into source control).
    "cppOutputPath": "Code/Source/Scripting/Generated",

    // Extra -I paths passed to ClangSharp.
    "includePaths": [
      "${O3DE_ENGINE_PATH}/Code",
      "${O3DE_ENGINE_PATH}/Gems"
    ],

    // Preprocessor defines passed to ClangSharp.
    "defines": [
      "O3DE_EXPORT_CSHARP=__attribute__((annotate(\"export_csharp\")))",
      "AZ_COMPILER_CLANG=1"
    ],

    // Generate FluentExtensions.g.cs (With*() methods).
    "generateExtensionMethods": true,

    // Generate Metadata.g.cs + metadata.json.
    "generateMetadata": true,

    // Use SHA256 hashing to skip unchanged files.
    "incrementalBuild": true,

    // Only export declarations marked with O3DE_EXPORT_CSHARP.
    // false = export ALL public declarations.
    "requireExportAttribute": false,

    "verbose": false
  },
  "gems": {
    "MyGem": {
      "enabled": true,

      // Glob patterns relative to the gem root.
      "headerPatterns": ["Code/Include/**/*.h"],
      "excludePatterns": ["**/Platform/**", "**/Tests/**"],

      // Per-gem overrides (same keys as global):
      "includePaths": [],
      "defines": [],
      "cSharpNamespace": "O3DE",
      "requireExportAttribute": false
    }
  }
}
```

### C++ Type → C# Type Mapping

| C++ Type | C# Type |
|----------|---------|
| `bool` | `bool` |
| `int`, `int32_t` | `int` |
| `uint32_t`, `unsigned int` | `uint` |
| `int64_t` | `long` |
| `uint64_t` | `ulong` |
| `float` | `float` |
| `double` | `double` |
| `const char*`, `AZStd::string` | `string` |
| `AZ::Vector2` | `Vector2` |
| `AZ::Vector3` | `Vector3` |
| `AZ::Vector4` | `Vector4` |
| `AZ::Quaternion` | `Quaternion` |
| `AZ::Color` | `Color` |
| `AZ::EntityId` | `ulong` |
| `AZ::Uuid` | `Guid` |
| `Coral::NativeString` | `NativeString` |
| `Coral::Bool32` | `Bool32` |
| Unknown pointer types | `IntPtr` |

### Attribute-Based Export (Optional)

If you set `"requireExportAttribute": true`, only C++ declarations marked with the
`O3DE_EXPORT_CSHARP` attribute are exported:

```cpp
#include <Scripting/ExportAttributes.h>

class O3DE_EXPORT_CSHARP MyClass
{
public:
    O3DE_EXPORT_CSHARP void ExportedMethod();     // ✓ exported
    void AlsoExported();                            // ✓ exported (class-level attr)
private:
    void NotExported();                             // ✗ private — never exported
};

class AnotherClass
{
public:
    void Invisible();                               // ✗ no attribute — skipped
    O3DE_EXPORT_CSHARP void ButThisIs();           // ✓ method-level attr
};
```

When `requireExportAttribute` is `false` (the default), **all** public
declarations are exported — no attribute needed.

---

## 10. Troubleshooting

### Skipped Types (Template Instantiations & STL Containers)

The generator **automatically skips** C++ types that cannot be meaningfully
wrapped in C#:

- **Template instantiations** — any name containing `<` / `>` (e.g.,
  `AZ::RHI::Handle<unsigned int>`).
- **STL / AZStd containers** — `AZStd::unordered_map`, `AZStd::vector`,
  `AZStd::fixed_vector`, `AZStd::pair`, `AZStd::optional`, etc.
- **Internal iterator types** — `Iterator_VM<...>`.

These types are logged once at INFO level:

```
Skipped 312 unsupported template/container types
```

If you need a binding for a specific template specialization, create a `typedef`
in your C++ header and reflect that instead.

### Filename Sanitization

C++ class names like `AZ::Render::MeshComponent` are sanitized before being used
as filenames:

| C++ Name | Generated Filename |
|----------|-------------------|
| `AZ::Render::MeshComponent` | `AZ.Render.MeshComponent.g.cs` |
| `LmbrCentral::QuadShapeConfig` | `LmbrCentral.QuadShapeConfig.g.cs` |
| `ShaderSourceData::EntryPoint` | `ShaderSourceData.EntryPoint.g.cs` |

Namespace separators (`::`) become dots, illegal Windows characters
(`< > : " / \ | ? *`) are removed, and filenames are truncated to 120 characters.
The C# class name is similarly sanitized (last segment of the namespace; nested
parts joined with `_`).

### "No bindings generated"

1. Check that your gem is listed in `binding_config.json` under `gems` with
   `"enabled": true`.
2. Verify `headerPatterns` match your header locations — use `--verbose` to see
   what files the parser scans.
3. If using `--require-attribute`, ensure headers include
   `<Scripting/ExportAttributes.h>` and use `O3DE_EXPORT_CSHARP`.
4. Run `dotnet run -- list-gems --project /path` to confirm gem discovery.

### "libclang not found"

ClangSharp requires a native libclang. On Windows this is bundled via NuGet. On
Linux, install LLVM:

```bash
sudo apt install libclang-17-dev
export LD_LIBRARY_PATH=/usr/lib/llvm-17/lib:$LD_LIBRARY_PATH
```

### "Parse errors" in generated code

- Include paths may be missing in `binding_config.json`.
- Preprocessor defines may not match your build configuration.
- Use `--verbose` to see Clang diagnostics.

### "InternalCalls not bound" at runtime

The C++ side must call `RegisterBindings(assembly)` **after** the managed assembly
is loaded. Check that:

1. `BindingRegistration.g.cpp` is compiled into your gem's C++ target.
2. The registration function is called in your system component's `Activate()`.
3. The `AddInternalCall` type names match the namespace in the generated C#
   (`"O3DE.PhysicsGem.InternalCalls"`).

### IntelliSense not working for generated types

- Ensure the `.csproj` has a `<Reference>` or `<ProjectReference>` to the
  bindings DLL.
- If using MSBuild task integration, check that `O3DEDesignTimeGenerate` is
  `true` (the default).
- Rebuild the bindings project: `dotnet build`.

### Stale bindings after C++ changes

- Delete `.binding_cache.json` and regenerate, or pass `--force`.
- If using MSBuild task, set `<O3DEForceRegen>true</O3DEForceRegen>` for one build.

---

## Summary: Recommended Project Layout

```
MyProject/
├── binding_config.json
├── Assets/
│   └── Scripts/
│       ├── O3DE.Core/                    ← hand-written core API (ScriptComponent, etc.)
│       │   ├── O3DE.Core.csproj
│       │   └── *.cs
│       │
│       ├── PhysicsGem/                   ← GENERATED binding DLL project
│       │   ├── PhysicsGem.csproj         ← auto-generated .csproj
│       │   ├── InternalCalls.g.cs        ← function pointers
│       │   ├── RigidBodyComponent.g.cs   ← wrapper classes
│       │   ├── FluentExtensions.g.cs     ← With*() methods
│       │   └── bin/Release/net8.0/
│       │       └── PhysicsGem.dll        ← compiled bindings DLL
│       │
│       └── MyGame/                       ← YOUR game scripts
│           ├── MyGame.csproj             ← references O3DE.Core + PhysicsGem
│           ├── PlayerController.cs
│           └── AudioPlayer.cs
│
├── Bin/
│   └── Scripts/                          ← deployment directory
│       ├── Coral/
│       │   └── Coral.Managed.dll
│       ├── O3DE.Core.dll
│       ├── PhysicsGem.dll                ← deployed binding DLL
│       └── MyGame.dll                    ← deployed game scripts
│
└── Code/
    └── Source/
        └── Scripting/
            └── Generated/                ← C++ registration (compiled into gem)
                ├── BindingRegistration.g.cpp
                └── PhysicsGem_HotReload.g.h
```

**Workflow:**

1. **Generate:** `dotnet run -- generate --project .` (or automatic via MSBuild)
2. **Build bindings DLL:** `dotnet build Assets/Scripts/PhysicsGem/`
3. **Build game DLL:** `dotnet build Assets/Scripts/MyGame/`
4. **Deploy:** copy DLLs to `Bin/Scripts/`
5. **Run:** launch Editor, enter Game Mode
