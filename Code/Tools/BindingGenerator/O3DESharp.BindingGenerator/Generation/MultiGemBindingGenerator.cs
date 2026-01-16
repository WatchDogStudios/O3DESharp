/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using O3DESharp.BindingGenerator.Configuration;
using O3DESharp.BindingGenerator.GemDiscovery;
using O3DESharp.BindingGenerator.Parsing;

namespace O3DESharp.BindingGenerator.Generation
{
    /// <summary>
    /// Orchestrates multi-gem binding generation with dependency ordering
    /// </summary>
    public class MultiGemBindingGenerator
    {
        private readonly BindingConfig _config;
        private readonly bool _verbose;

        public MultiGemBindingGenerator(BindingConfig config, bool verbose = false)
        {
            _config = config;
            _verbose = verbose;
        }

        /// <summary>
        /// Generate bindings for all enabled gems in dependency order
        /// </summary>
        /// <param name="gems">All discovered gems</param>
        /// <param name="sortedGems">Gems in dependency order</param>
        /// <param name="projectPath">Path to the project root</param>
        public void GenerateAll(Dictionary<string, GemDescriptor> gems, List<GemDescriptor> sortedGems, string projectPath)
        {
            Log($"Generating bindings for {sortedGems.Count} gems in dependency order...");

            var csharpGenerator = new CSharpCodeGenerator(_config.Global.CSharpNamespace, _verbose);
            var cppGenerator = new CppRegistrationGenerator(_verbose);
            var projectGenerator = new CSharpProjectGenerator(_verbose);

            foreach (var gem in sortedGems)
            {
                try
                {
                    GenerateGemBindings(gem, gems, projectPath, csharpGenerator, cppGenerator, projectGenerator);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error generating bindings for gem '{gem.GemName}': {ex.Message}");
                    if (_verbose)
                    {
                        Console.WriteLine(ex.StackTrace);
                    }
                }
            }

            Log("Binding generation complete!");
        }

        private void GenerateGemBindings(
            GemDescriptor gem,
            Dictionary<string, GemDescriptor> allGems,
            string projectPath,
            CSharpCodeGenerator csharpGenerator,
            CppRegistrationGenerator cppGenerator,
            CSharpProjectGenerator projectGenerator)
        {
            Log($"\nProcessing gem: {gem.GemName}");

            // Get gem-specific settings
            var gemSettings = _config.Gems.ContainsKey(gem.GemName)
                ? _config.Gems[gem.GemName]
                : new GemSettings();

            if (!gemSettings.Enabled)
            {
                Log($"  Skipped (disabled in config)");
                return;
            }

            // Find header files to parse
            var headerFiles = FindHeaderFiles(gem, gemSettings);
            if (headerFiles.Count == 0)
            {
                Log($"  No header files found");
                return;
            }

            // Build include paths
            var includePaths = BuildIncludePaths(gem, gemSettings, allGems);

            // Build defines
            var defines = BuildDefines(gemSettings);

            // Determine if we require export attribute
            var requireExportAttribute = gemSettings.RequireExportAttribute ?? _config.Global.RequireExportAttribute;

            // Parse headers
            var parser = new O3DEHeaderParser(requireExportAttribute, _verbose);
            var bindings = parser.ParseHeaders(headerFiles, includePaths, defines, gem.GemName);

            if (bindings.Classes.Count == 0 && bindings.Functions.Count == 0 && bindings.Enums.Count == 0)
            {
                Log($"  No bindings found to generate");
                return;
            }

            // Generate C# code
            var csharpOutputPath = ResolvePath(_config.Global.CSharpOutputPath, gem);
            csharpGenerator.Generate(bindings, csharpOutputPath);

            // Generate C++ code
            var cppOutputPath = ResolvePath(_config.Global.CppOutputPath, gem);
            cppGenerator.Generate(bindings, cppOutputPath);

            // Generate .csproj
            var coreAssemblyPath = ResolveCorePath(projectPath);
            projectGenerator.Generate(gem, csharpOutputPath, allGems, coreAssemblyPath);

            Log($"  Generated bindings for {gem.GemName}");
        }

        private List<string> FindHeaderFiles(GemDescriptor gem, GemSettings settings)
        {
            var headerFiles = new List<string>();

            foreach (var pattern in settings.HeaderPatterns)
            {
                var searchPath = Path.Combine(gem.GemPath, pattern);
                var directory = Path.GetDirectoryName(searchPath) ?? gem.GemPath;
                var filePattern = Path.GetFileName(searchPath);

                if (Directory.Exists(directory))
                {
                    var files = Directory.GetFiles(directory, filePattern, SearchOption.AllDirectories);
                    headerFiles.AddRange(files);
                }
            }

            // Apply exclude patterns
            var excludedFiles = new HashSet<string>();
            foreach (var pattern in settings.ExcludePatterns)
            {
                var searchPath = Path.Combine(gem.GemPath, pattern);
                var directory = Path.GetDirectoryName(searchPath) ?? gem.GemPath;
                var filePattern = Path.GetFileName(searchPath);

                if (Directory.Exists(directory))
                {
                    var files = Directory.GetFiles(directory, filePattern, SearchOption.AllDirectories);
                    foreach (var file in files)
                    {
                        excludedFiles.Add(file);
                    }
                }
            }

            return headerFiles.Where(f => !excludedFiles.Contains(f)).Distinct().ToList();
        }

        private List<string> BuildIncludePaths(GemDescriptor gem, GemSettings settings, Dictionary<string, GemDescriptor> allGems)
        {
            var includePaths = new List<string>();

            // Add gem's own include paths
            includePaths.Add(Path.Combine(gem.GemPath, "Code/Include"));
            includePaths.Add(Path.Combine(gem.GemPath, "Code/Source"));

            // Add global include paths
            includePaths.AddRange(_config.Global.IncludePaths);

            // Add gem-specific include paths
            includePaths.AddRange(settings.IncludePaths.Select(p => Path.Combine(gem.GemPath, p)));

            // Add dependency include paths
            foreach (var depName in gem.Dependencies)
            {
                if (allGems.TryGetValue(depName, out var depGem))
                {
                    includePaths.Add(Path.Combine(depGem.GemPath, "Code/Include"));
                }
            }

            return includePaths.Distinct().Where(Directory.Exists).ToList();
        }

        private List<string> BuildDefines(GemSettings settings)
        {
            var defines = new List<string>();
            defines.AddRange(_config.Global.Defines);
            defines.AddRange(settings.Defines);
            return defines;
        }

        private string ResolvePath(string pathTemplate, GemDescriptor gem)
        {
            var resolved = pathTemplate.Replace("{GemName}", gem.GemName);
            return Path.Combine(gem.GemPath, resolved);
        }

        private string ResolveCorePath(string projectPath)
        {
            // Try multiple possible locations for O3DE.Core assembly
            var possiblePaths = new[]
            {
                Path.Combine(projectPath, "Assets/Scripts/O3DE.Core/bin/Release/net8.0/O3DE.Core.dll"),
                Path.Combine(projectPath, "Assets/Scripts/O3DE.Core/bin/Debug/net8.0/O3DE.Core.dll"),
                Path.Combine(projectPath, "Assets/Scripts/O3DE.Core/bin/Release/net9.0/O3DE.Core.dll"),
                Path.Combine(projectPath, "Assets/Scripts/O3DE.Core/bin/Debug/net9.0/O3DE.Core.dll"),
                "../../../Assets/Scripts/O3DE.Core/bin/Debug/net8.0/O3DE.Core.dll" // Relative fallback
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            // Return a placeholder path if not found - user can fix in generated .csproj
            Log($"  Warning: O3DE.Core.dll not found, using placeholder path");
            return Path.Combine(projectPath, "Assets/Scripts/O3DE.Core/bin/Debug/net8.0/O3DE.Core.dll");
        }

        private void Log(string message)
        {
            if (_verbose)
            {
                Console.WriteLine($"[MultiGemGen] {message}");
            }
            else if (!message.StartsWith("  "))
            {
                // Always print top-level progress
                Console.WriteLine(message);
            }
        }
    }
}
