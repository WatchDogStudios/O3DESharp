//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace O3DESharp.BindingGenerator.Tests;

/// <summary>
/// Test fixtures and helper methods for binding generator tests.
/// Provides sample C++ headers and gem structures for testing.
/// </summary>
public static class TestFixtures
{
    /// <summary>
    /// Creates a temporary directory for test outputs.
    /// </summary>
    public static string CreateTempDirectory()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "O3DESharpTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempPath);
        return tempPath;
    }

    /// <summary>
    /// Creates a sample C++ header with various declaration types.
    /// </summary>
    public static string CreateSampleHeader(string outputPath, string className = "TestClass")
    {
        var headerContent = $@"
#pragma once
#include <AzCore/Math/Vector3.h>
#include <AzCore/Component/EntityId.h>

namespace O3DE {{

    /// <summary>
    /// Test class for binding generation
    /// </summary>
    class O3DE_EXPORT_CSHARP {className} {{
    public:
        {className}();
        ~{className}();

        // Property accessors
        void SetPosition(const AZ::Vector3& position);
        AZ::Vector3 GetPosition() const;

        void SetEntityId(AZ::EntityId id);
        AZ::EntityId GetEntityId() const;

        // Methods with various parameter types
        void Update(float deltaTime);
        bool IsValid() const;
        int GetCount() const;
        
        // Static methods
        static {className}* Create();
        static void Destroy({className}* instance);

    private:
        AZ::Vector3 m_position;
        AZ::EntityId m_entityId;
        int m_count;
    }};

    /// <summary>
    /// Simple struct for testing
    /// </summary>
    struct O3DE_EXPORT_CSHARP TestStruct {{
        float x;
        float y;
        float z;
    }};

    /// <summary>
    /// Test enum
    /// </summary>
    enum class O3DE_EXPORT_CSHARP TestEnum {{
        Value1,
        Value2,
        Value3
    }};
}}
";
        var headerPath = Path.Combine(outputPath, $"{className}.h");
        File.WriteAllText(headerPath, headerContent);
        return headerPath;
    }

    /// <summary>
    /// Creates a sample gem.json file.
    /// </summary>
    public static string CreateSampleGemJson(string gemPath, string gemName, string[] dependencies = null)
    {
        var gemJson = new
        {
            gem_name = gemName,
            version = "1.0.0",
            display_name = gemName,
            license = "Apache-2.0 Or MIT",
            origin = "Test",
            type = "Code",
            summary = $"Test gem {gemName}",
            icon_path = "preview.png",
            requirements = "",
            dependencies = dependencies ?? Array.Empty<string>()
        };

        var gemJsonPath = Path.Combine(gemPath, "gem.json");
        var jsonContent = System.Text.Json.JsonSerializer.Serialize(gemJson, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(gemJsonPath, jsonContent);
        return gemJsonPath;
    }

    /// <summary>
    /// Creates a sample binding_config.json file.
    /// </summary>
    public static string CreateSampleBindingConfig(string outputPath)
    {
        var config = new
        {
            global = new
            {
                requireExportAttribute = false,
                incrementalBuild = true,
                cSharpNamespace = "O3DE",
                includePaths = new[] { "Code/Include" }
            },
            gems = new Dictionary<string, object>
            {
                ["TestGem"] = new
                {
                    headerPatterns = new[] { "Code/Include/**/*.h" },
                    excludePatterns = new[] { "**/Tests/**", "**/Private/**" }
                }
            }
        };

        var configPath = Path.Combine(outputPath, "binding_config.json");
        var jsonContent = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(configPath, jsonContent);
        return configPath;
    }

    /// <summary>
    /// Creates a mock O3DE project structure for testing.
    /// </summary>
    public static string CreateMockProject(string projectName = "TestProject")
    {
        var projectPath = CreateTempDirectory();
        
        // Create project.json
        var projectJson = new
        {
            project_name = projectName,
            version = "0.0.0",
            compatible_engines = new[] { "o3de" }
        };

        var projectJsonPath = Path.Combine(projectPath, "project.json");
        var jsonContent = System.Text.Json.JsonSerializer.Serialize(projectJson, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(projectJsonPath, jsonContent);

        // Create Gems directory
        var gemsPath = Path.Combine(projectPath, "Gems");
        Directory.CreateDirectory(gemsPath);

        return projectPath;
    }

    /// <summary>
    /// Creates a mock gem with sample headers.
    /// </summary>
    public static string CreateMockGem(string projectPath, string gemName, string[] dependencies = null)
    {
        var gemPath = Path.Combine(projectPath, "Gems", gemName);
        Directory.CreateDirectory(gemPath);

        // Create gem.json
        CreateSampleGemJson(gemPath, gemName, dependencies);

        // Create include directory with sample header
        var includePath = Path.Combine(gemPath, "Code", "Include", gemName);
        Directory.CreateDirectory(includePath);
        CreateSampleHeader(includePath, $"{gemName}Class");

        return gemPath;
    }

    /// <summary>
    /// Gets the real O3DESharp gem path if available.
    /// </summary>
    public static string? GetRealO3DESharpGemPath()
    {
        // Try to find the gem relative to the test assembly location
        var assemblyPath = AppContext.BaseDirectory;
        var currentDir = new DirectoryInfo(assemblyPath);

        // Walk up the directory tree looking for the gem root
        while (currentDir != null)
        {
            var gemJsonPath = Path.Combine(currentDir.FullName, "gem.json");
            if (File.Exists(gemJsonPath))
            {
                // Verify it's the O3DESharp gem
                var gemJson = File.ReadAllText(gemJsonPath);
                if (gemJson.Contains("O3DESharp"))
                {
                    return currentDir.FullName;
                }
            }

            // Also check if we're in the Tools directory
            if (currentDir.Name == "BindingGenerator.Tests")
            {
                var gemRoot = currentDir.Parent?.Parent?.Parent?.Parent;
                if (gemRoot != null)
                {
                    var gemJson = Path.Combine(gemRoot.FullName, "gem.json");
                    if (File.Exists(gemJson))
                    {
                        return gemRoot.FullName;
                    }
                }
            }

            currentDir = currentDir.Parent;
        }

        return null;
    }

    /// <summary>
    /// Gets real O3DESharp header files if available.
    /// </summary>
    public static IEnumerable<string> GetRealO3DESharpHeaders()
    {
        var gemPath = GetRealO3DESharpGemPath();
        if (gemPath == null)
            return Enumerable.Empty<string>();

        var includePath = Path.Combine(gemPath, "Code", "Include", "O3DESharp");
        if (!Directory.Exists(includePath))
            return Enumerable.Empty<string>();

        return Directory.GetFiles(includePath, "*.h", SearchOption.AllDirectories);
    }

    /// <summary>
    /// Cleans up a temporary directory and all its contents.
    /// </summary>
    public static void CleanupTempDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
