# O3DE C# Binding Generator

A ClangSharp-based tool for generating C# bindings from C++ headers for O3DE gems.

## Features

- **Automatic C++ Parsing**: Uses ClangSharp (libclang wrapper) to parse C++ headers
- **Multi-Gem Support**: Generates bindings for multiple gems with automatic dependency resolution
- **Flexible Export Modes**: 
  - Attribute-based: Only export declarations marked with `O3DE_EXPORT_CSHARP`
  - Full export: Export all public declarations (no attributes required)
- **Type Mapping**: Automatic mapping of O3DE types (Vector3, Quaternion, EntityId, etc.)
- **Incremental Builds**: File hash caching for faster regeneration
- **Dependency Ordering**: Topological sort ensures gems are generated in correct order
- **Project Generation**: Automatic .csproj creation with proper dependency references

## Requirements

- .NET 8.0 SDK or later
- ClangSharp 17.0.1 (automatically installed via NuGet)

## Installation

Build the tool:

```bash
cd Code/Tools/BindingGenerator/O3DESharp.BindingGenerator
dotnet build -c Release
```

The compiled tool will be in `bin/Release/net8.0/O3DESharp.BindingGenerator.dll`

## Quick Start

### 1. List Gems in Your Project

```bash
dotnet run --project O3DESharp.BindingGenerator -- list-gems --project /path/to/your/project
```

### 2. Create Default Configuration

```bash
dotnet run --project O3DESharp.BindingGenerator -- init-config
```

This creates `binding_config.json` with default settings.

### 3. Generate Bindings

Generate for all enabled gems:

```bash
dotnet run --project O3DESharp.BindingGenerator -- generate --project /path/to/your/project
```

Generate for specific gems:

```bash
dotnet run --project O3DESharp.BindingGenerator -- generate --project /path/to/your/project --gems MyGem,AnotherGem
```

Enable verbose output:

```bash
dotnet run --project O3DESharp.BindingGenerator -- generate --project /path/to/your/project --verbose
```

## Export Modes

### Mode 1: Attribute-Based Export (Default)

Only declarations marked with `O3DE_EXPORT_CSHARP` are exported:

```cpp
// In your C++ header
class O3DE_EXPORT_CSHARP MyClass
{
public:
    O3DE_EXPORT_CSHARP void MyMethod();
    O3DE_EXPORT_CSHARP int MyProperty;
};
```

Enable with:
```bash
dotnet run -- generate --require-attribute
```

Or in `binding_config.json`:
```json
{
  "global": {
    "requireExportAttribute": true
  }
}
```

### Mode 2: Full Export (Recommended)

Export all public declarations without requiring attributes:

```cpp
// In your C++ header - no attributes needed!
class MyClass
{
public:
    void MyMethod();    // Will be exported
    int MyProperty;     // Will be exported
private:
    void Internal();    // Will NOT be exported (private)
};
```

This is the default mode. To explicitly enable:

```bash
dotnet run -- generate
# or
dotnet run -- generate --require-attribute false
```

Or in `binding_config.json`:
```json
{
  "global": {
    "requireExportAttribute": false
  }
}
```

## Configuration

### Example binding_config.json

```json
{
  "global": {
    "cSharpNamespace": "O3DE",
    "cSharpOutputPath": "Assets/Scripts/{GemName}",
    "cppOutputPath": "Code/Source/Scripting/Generated",
    "includePaths": [
      "${O3DE_ENGINE_PATH}/Code",
      "${O3DE_ENGINE_PATH}/Gems"
    ],
    "defines": [
      "O3DE_EXPORT_CSHARP=__attribute__((annotate(\"export_csharp\")))",
      "AZ_COMPILER_CLANG=1"
    ],
    "verbose": false,
    "incrementalBuild": true,
    "requireExportAttribute": false
  },
  "gems": {
    "MyGem": {
      "enabled": true,
      "headerPatterns": [
        "Code/Include/**/*.h"
      ],
      "excludePatterns": [
        "**/Platform/**",
        "**/Tests/**"
      ],
      "requireExportAttribute": false
    }
  }
}
```

### Configuration Options

#### Global Settings

- **cSharpNamespace**: Root namespace for generated C# code (default: "O3DE")
- **cSharpOutputPath**: Output directory for C# files (supports `{GemName}` placeholder)
- **cppOutputPath**: Output directory for C++ registration code
- **includePaths**: Additional include directories for ClangSharp
- **defines**: Preprocessor defines
- **verbose**: Enable detailed logging
- **incrementalBuild**: Use file hash caching for faster rebuilds
- **requireExportAttribute**: Require `O3DE_EXPORT_CSHARP` on declarations (default: false)

#### Per-Gem Settings

- **enabled**: Whether to generate bindings for this gem
- **headerPatterns**: Glob patterns for header files to parse
- **excludePatterns**: Glob patterns for headers to exclude
- **includePaths**: Additional include paths for this gem
- **defines**: Additional preprocessor defines for this gem
- **cSharpNamespace**: Override namespace for this gem
- **requireExportAttribute**: Override global setting for this gem

## Type Mappings

The tool automatically maps C++ types to C# equivalents:

| C++ Type | C# Type |
|----------|---------|
| `bool` | `bool` |
| `int`, `int32_t` | `int` |
| `uint32_t` | `uint` |
| `int64_t` | `long` |
| `float` | `float` |
| `double` | `double` |
| `const char*`, `AZStd::string` | `string` |
| `AZ::Vector2` | `Vector2` |
| `AZ::Vector3` | `Vector3` |
| `AZ::Vector4` | `Vector4` |
| `AZ::Quaternion` | `Quaternion` |
| `AZ::EntityId` | `ulong` |
| `AZ::Uuid` | `Guid` |

## Generated Output

For each gem, the tool generates:

### C# Files
- **InternalCalls.g.cs**: Function pointer declarations for Coral interop
- **{ClassName}.g.cs**: Wrapper classes for each exported C++ class
- **{EnumName}.g.cs**: C# enums for each exported C++ enum
- **{GemName}.csproj**: Project file with proper dependencies

### C++ Files
- **BindingRegistration.g.cpp**: Coral registration code for all bindings

## Command-Line Interface

### Commands

- **generate**: Generate bindings (default command)
- **list-gems**: List all discovered gems
- **init-config**: Create default configuration file

### Options

- `--project, -p`: Path to project.json or project directory (default: ".")
- `--config, -c`: Path to binding_config.json (default: "binding_config.json")
- `--gems, -g`: Comma-separated list of specific gems to process
- `--verbose, -v`: Enable verbose logging
- `--incremental, -i`: Enable incremental builds (default: true)
- `--require-attribute`: Only export declarations with O3DE_EXPORT_CSHARP attribute

### Examples

```bash
# List all gems
dotnet run -- list-gems -p /path/to/project

# Generate with verbose output
dotnet run -- generate -p /path/to/project -v

# Generate specific gems only
dotnet run -- generate -p /path/to/project -g MyGem,AnotherGem

# Generate with attribute requirement
dotnet run -- generate --require-attribute

# Use custom config
dotnet run -- generate -c my_config.json
```

## Integration with O3DE

1. **Add to CMake**: Include the generated registration code in your gem's CMakeLists.txt
2. **Call RegisterBindings**: In your gem's initialization code:

```cpp
#include "Generated/BindingRegistration.g.cpp"

void MyGemModule::InitializeScripting()
{
    auto* assembly = GetCoralAssembly();
    MyGem::Generated::RegisterBindings(assembly);
}
```

3. **Build C# Projects**: The generated .csproj files can be built with:

```bash
dotnet build Assets/Scripts/MyGem/MyGem.csproj
```

## Troubleshooting

### libclang not found

If ClangSharp can't find libclang, ensure you have LLVM installed or set the path:

```bash
export LD_LIBRARY_PATH=/path/to/llvm/lib:$LD_LIBRARY_PATH  # Linux
```

### Parse errors

- Check that include paths are correct in binding_config.json
- Verify preprocessor defines match your build configuration
- Use `--verbose` flag to see detailed parse information

### No bindings generated

- Ensure headers have public declarations (or `O3DE_EXPORT_CSHARP` if required)
- Check that header patterns match your gem structure
- Use `list-gems` to verify gem discovery

## License

Copyright (c) Contributors to the Open 3D Engine Project.

Licensed under Apache-2.0 OR MIT. See LICENSE file for details.
