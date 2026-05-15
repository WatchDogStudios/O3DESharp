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
using System.Text.Json;
using O3DESharp.BindingGenerator.Caching;
using O3DESharp.BindingGenerator.Configuration;
using O3DESharp.BindingGenerator.GemDiscovery;
using O3DESharp.BindingGenerator.Parsing;

namespace O3DESharp.BindingGenerator.Generation
{
    /// <summary>
    /// Result statistics from binding generation
    /// </summary>
    public class GenerationResult
    {
        public int ClassesGenerated { get; set; }
        public int FilesWritten { get; set; }
        public int GemsProcessed { get; set; }
        public int GemsSkipped { get; set; }
        public List<string> ProcessedGemNames { get; set; } = new List<string>();
    }

    /// <summary>
    /// Orchestrates multi-gem binding generation with dependency ordering
    /// </summary>
    public class MultiGemBindingGenerator
    {
        private readonly BindingConfig _config;
        private readonly bool _verbose;
        private readonly bool _forceRebuild;
        private readonly string? _enginePath;
        private BuildCache? _buildCache;

        public MultiGemBindingGenerator(BindingConfig config, bool verbose = false, bool forceRebuild = false, string? enginePath = null)
        {
            _config = config;
            _verbose = verbose;
            _forceRebuild = forceRebuild;
            _enginePath = enginePath;
        }

        /// <summary>
        /// Generate bindings for all enabled gems in dependency order
        /// </summary>
        /// <param name="gems">All discovered gems</param>
        /// <param name="sortedGems">Gems in dependency order</param>
        /// <param name="projectPath">Path to the project root</param>
        public GenerationResult GenerateAll(Dictionary<string, GemDescriptor> gems, List<GemDescriptor> sortedGems, string projectPath)
        {
            Log($"Generating bindings for {sortedGems.Count} gems in dependency order...");

            var result = new GenerationResult();

            // Initialize build cache if incremental builds are enabled
            if (_config.Global.IncrementalBuild && !_forceRebuild)
            {
                var cacheFilePath = Path.Combine(projectPath, _config.Global.CacheFilePath);
                _buildCache = new BuildCache(cacheFilePath, _verbose);
                var (entryCount, filesTracked) = _buildCache.GetStats();
                Log($"Incremental build enabled. Cache has {entryCount} entries tracking {filesTracked} files.");
            }
            else if (_forceRebuild)
            {
                Log("Force rebuild requested - skipping cache checks.");
            }

            var csharpGenerator = new CSharpCodeGenerator(_config.Global.CSharpNamespace, _verbose);
            var cppGenerator = new CppRegistrationGenerator(_verbose);
            var projectGenerator = new CSharpProjectGenerator(_verbose);
            var metadataGenerator = new MetadataGenerator(_verbose);
            var extensionGenerator = new ExtensionMethodGenerator(_config.Global.CSharpNamespace, _verbose);

            foreach (var gem in sortedGems)
            {
                try
                {
                    var (generated, classCount, fileCount) = GenerateGemBindings(gem, gems, projectPath, csharpGenerator, cppGenerator, projectGenerator, metadataGenerator, extensionGenerator);
                    if (generated)
                    {
                        result.GemsProcessed++;
                        result.ClassesGenerated += classCount;
                        result.FilesWritten += fileCount;
                        result.ProcessedGemNames.Add(gem.GemName);
                    }
                    else
                    {
                        result.GemsSkipped++;
                    }
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

            // Save the cache
            _buildCache?.Save();

            Log($"Binding generation complete! Generated: {result.GemsProcessed}, Skipped (cached): {result.GemsSkipped}");

            // Output machine-readable summary for orchestrator scripts
            Console.WriteLine($"Generated {result.ClassesGenerated} classes in {result.FilesWritten} files");
            if (result.ProcessedGemNames.Count > 0)
            {
                Console.WriteLine($"Processed gems: {string.Join(", ", result.ProcessedGemNames)}");
            }

            return result;
        }

        /// <summary>
        /// Generate bindings for a single gem
        /// </summary>
        /// <returns>Tuple of (generated, classCount, fileCount)</returns>
        private (bool generated, int classCount, int fileCount) GenerateGemBindings(
            GemDescriptor gem,
            Dictionary<string, GemDescriptor> allGems,
            string projectPath,
            CSharpCodeGenerator csharpGenerator,
            CppRegistrationGenerator cppGenerator,
            CSharpProjectGenerator projectGenerator,
            MetadataGenerator metadataGenerator,
            ExtensionMethodGenerator extensionGenerator)
        {
            Log($"\nProcessing gem: {gem.GemName}");

            // Get gem-specific settings
            var gemSettings = _config.Gems.ContainsKey(gem.GemName)
                ? _config.Gems[gem.GemName]
                : new GemSettings();

            if (!gemSettings.Enabled)
            {
                Log($"  Skipped (disabled in config)");
                return (false, 0, 0);
            }

            // Find header files to parse
            var headerFiles = FindHeaderFiles(gem, gemSettings);
            if (headerFiles.Count == 0)
            {
                Log($"  No header files found");
                return (false, 0, 0);
            }

            // Check cache for incremental build
            if (_buildCache != null)
            {
                var configHash = ComputeConfigHash(gemSettings);
                if (!_buildCache.NeedsRegeneration(gem.GemName, headerFiles, configHash))
                {
                    Console.WriteLine($"  [{gem.GemName}] Up to date (cached)");
                    return (false, 0, 0);
                }
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
                return (false, 0, 0);
            }

            // Generate C# code
            var csharpOutputPath = ResolvePath(_config.Global.CSharpOutputPath, gem);
            csharpGenerator.Generate(bindings, csharpOutputPath);

            // Generate C++ code
            var cppOutputPath = ResolvePath(_config.Global.CppOutputPath, gem);
            cppGenerator.Generate(bindings, cppOutputPath);

            // Generate metadata for runtime reflection and hot reload
            if (_config.Global.GenerateMetadata)
            {
                metadataGenerator.Generate(bindings, csharpOutputPath, _config.Global.CSharpNamespace);
            }

            // Generate fluent API extension methods
            if (_config.Global.GenerateExtensionMethods)
            {
                extensionGenerator.Generate(bindings, csharpOutputPath);
            }

            // Generate .csproj
            var coreAssemblyPath = ResolveCorePath(projectPath);
            projectGenerator.Generate(gem, csharpOutputPath, allGems, coreAssemblyPath);

            // Update cache
            if (_buildCache != null)
            {
                var configHash = ComputeConfigHash(gemSettings);
                var outputFiles = CollectOutputFiles(csharpOutputPath, cppOutputPath);
                _buildCache.UpdateEntry(gem.GemName, headerFiles, configHash, outputFiles);
            }

            Console.WriteLine($"  [{gem.GemName}] Generated bindings");
            var classCount = bindings.Classes.Count;
            var fileCount = CollectOutputFiles(csharpOutputPath, cppOutputPath).Count;
            return (true, classCount, fileCount);
        }

        /// <summary>
        /// Compute a hash of the gem configuration for cache invalidation
        /// </summary>
        private string ComputeConfigHash(GemSettings settings)
        {
            var configStr = JsonSerializer.Serialize(new
            {
                settings.Enabled,
                settings.IncludePaths,
                settings.HeaderPatterns,
                settings.ExcludePatterns,
                GemCSharpNamespace = settings.CSharpNamespace,
                settings.Defines,
                GemRequireExportAttribute = settings.RequireExportAttribute,
                GlobalCSharpNamespace = _config.Global.CSharpNamespace,
                GlobalRequireExportAttribute = _config.Global.RequireExportAttribute,
                GlobalDefines = _config.Global.Defines
            });
            return FileHasher.ComputeStringHash(configStr);
        }

        /// <summary>
        /// Collect all output files for cache tracking
        /// </summary>
        private List<string> CollectOutputFiles(string csharpOutputPath, string cppOutputPath)
        {
            var outputFiles = new List<string>();

            if (Directory.Exists(csharpOutputPath))
            {
                outputFiles.AddRange(Directory.GetFiles(csharpOutputPath, "*.g.cs"));
                outputFiles.AddRange(Directory.GetFiles(csharpOutputPath, "*.csproj"));
            }

            if (Directory.Exists(cppOutputPath))
            {
                outputFiles.AddRange(Directory.GetFiles(cppOutputPath, "*.g.cpp"));
                outputFiles.AddRange(Directory.GetFiles(cppOutputPath, "*.g.h"));
            }

            return outputFiles;
        }

        private List<string> FindHeaderFiles(GemDescriptor gem, GemSettings settings)
        {
            var headerFiles = new List<string>();

            foreach (var pattern in settings.HeaderPatterns)
            {
                // Handle glob patterns like "Code/Include/**/*.h"
                // Split into the base directory (before **) and the file pattern
                var fullPattern = Path.Combine(gem.GemPath, pattern);

                string directory;
                string filePattern;
                SearchOption searchOption;

                var doubleStarIdx = fullPattern.IndexOf("**", StringComparison.Ordinal);
                if (doubleStarIdx >= 0)
                {
                    // Everything before ** is the base directory
                    directory = fullPattern.Substring(0, doubleStarIdx).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    // Everything after ** is the file pattern (strip leading separators)
                    var remainder = fullPattern.Substring(doubleStarIdx + 2).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    filePattern = string.IsNullOrEmpty(remainder) ? "*" : remainder;
                    searchOption = SearchOption.AllDirectories;
                }
                else
                {
                    directory = Path.GetDirectoryName(fullPattern) ?? gem.GemPath;
                    filePattern = Path.GetFileName(fullPattern);
                    searchOption = SearchOption.TopDirectoryOnly;
                }

                if (Directory.Exists(directory))
                {
                    var files = Directory.GetFiles(directory, filePattern, searchOption);
                    headerFiles.AddRange(files);
                }
            }

            // Apply exclude patterns
            var excludedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pattern in settings.ExcludePatterns)
            {
                // For patterns like "**/Platform/**", extract the directory segment to match against
                var pathSegment = pattern.Replace("**", "").Replace("*", "")
                    .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/');

                if (!string.IsNullOrEmpty(pathSegment))
                {
                    var sep1 = Path.DirectorySeparatorChar + pathSegment + Path.DirectorySeparatorChar;
                    var sep2 = "/" + pathSegment + "/";
                    foreach (var file in headerFiles)
                    {
                        if (file.Contains(sep1) || file.Contains(sep2))
                        {
                            excludedFiles.Add(file);
                        }
                    }
                }
                else
                {
                    // Treat as a concrete glob
                    var fullPattern = Path.Combine(gem.GemPath, pattern);
                    var doubleStarIdx = fullPattern.IndexOf("**", StringComparison.Ordinal);
                    string directory;
                    string filePatternExcl;
                    SearchOption searchOption;

                    if (doubleStarIdx >= 0)
                    {
                        directory = fullPattern.Substring(0, doubleStarIdx).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        var remainder = fullPattern.Substring(doubleStarIdx + 2).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        filePatternExcl = string.IsNullOrEmpty(remainder) ? "*" : remainder;
                        searchOption = SearchOption.AllDirectories;
                    }
                    else
                    {
                        directory = Path.GetDirectoryName(fullPattern) ?? gem.GemPath;
                        filePatternExcl = Path.GetFileName(fullPattern);
                        searchOption = SearchOption.TopDirectoryOnly;
                    }

                    if (Directory.Exists(directory))
                    {
                        foreach (var file in Directory.GetFiles(directory, filePatternExcl, searchOption))
                        {
                            excludedFiles.Add(file);
                        }
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

            // Add global include paths from config
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

            // Add O3DE engine framework include paths
            if (!string.IsNullOrEmpty(_enginePath) && Directory.Exists(_enginePath))
            {
                var frameworkDir = Path.Combine(_enginePath, "Code", "Framework");
                if (Directory.Exists(frameworkDir))
                {
                    // Core frameworks: AzCore, AzFramework, AzNetworking, etc.
                    // O3DE uses flat include structure: e.g. Code/Framework/AzCore/AzCore/base.h
                    // included as <AzCore/base.h> with include path Code/Framework/AzCore
                    foreach (var framework in Directory.GetDirectories(frameworkDir))
                    {
                        includePaths.Add(framework);

                        // O3DE platform-specific headers live in Platform/Windows/ (or Linux/Mac)
                        var platformDir = Path.Combine(framework, "Platform", "Windows");
                        if (Directory.Exists(platformDir))
                            includePaths.Add(platformDir);
                    }
                }

                // AtomCore and other Atom framework includes
                var atomRpiInclude = Path.Combine(_enginePath, "Gems", "Atom", "RPI", "Code", "Include");
                if (Directory.Exists(atomRpiInclude))
                    includePaths.Add(atomRpiInclude);

                var atomRhiInclude = Path.Combine(_enginePath, "Gems", "Atom", "RHI", "Code", "Include");
                if (Directory.Exists(atomRhiInclude))
                    includePaths.Add(atomRhiInclude);

                var atomFeatureCommonInclude = Path.Combine(_enginePath, "Gems", "Atom", "Feature", "Common", "Code", "Include");
                if (Directory.Exists(atomFeatureCommonInclude))
                    includePaths.Add(atomFeatureCommonInclude);

                // Auto-discover 3rdParty package include paths
                var thirdPartyPackagesDir = Resolve3rdPartyPackagesPath();
                if (thirdPartyPackagesDir != null)
                {
                    Log($"  Using 3rdParty packages from: {thirdPartyPackagesDir}");
                    foreach (var packageDir in Directory.GetDirectories(thirdPartyPackagesDir))
                    {
                        // Packages have structure like: PackageName-version/LibName/include/
                        foreach (var subDir in Directory.GetDirectories(packageDir))
                        {
                            var includeDir = Path.Combine(subDir, "include");
                            if (Directory.Exists(includeDir))
                                includePaths.Add(includeDir);
                        }
                        // Also check top-level include: PackageName-version/include/
                        var topInclude = Path.Combine(packageDir, "include");
                        if (Directory.Exists(topInclude))
                            includePaths.Add(topInclude);
                    }
                }
            }

            return includePaths.Distinct().Where(Directory.Exists).ToList();
        }

        private List<string> BuildDefines(GemSettings settings)
        {
            // Engine-required macros first; user globals override / extend; per-gem overrides last.
            // Later entries win when libclang sees duplicate -D flags, so user wins over defaults.
            var defines = new List<string>();
            defines.AddRange(Configuration.BindingConfig.EngineRequiredDefines);
            defines.AddRange(_config.Global.Defines);
            defines.AddRange(settings.Defines);
            return defines;
        }

        private string ResolvePath(string pathTemplate, GemDescriptor gem)
        {
            var resolved = pathTemplate.Replace("{GemName}", gem.GemName);

            // If the resolved path is absolute, use it directly (e.g. from --output)
            if (Path.IsPathRooted(resolved))
            {
                return resolved;
            }

            return Path.Combine(gem.GemPath, resolved);
        }

        /// <summary>
        /// Find the O3DE 3rdParty packages directory.
        /// Resolution order:
        /// 1. LY_3RDPARTY_PATH environment variable
        /// 2. CMakeCache.txt in engine workspace directory
        /// 3. Well-known location: ~/.o3de/3rdParty/packages/
        /// </summary>
        private string? Resolve3rdPartyPackagesPath()
        {
            // 1. Environment variable
            var envPath = Environment.GetEnvironmentVariable("LY_3RDPARTY_PATH");
            if (!string.IsNullOrEmpty(envPath))
            {
                var packagesDir = Path.Combine(envPath, "packages");
                if (Directory.Exists(packagesDir))
                    return packagesDir;
                if (Directory.Exists(envPath))
                    return envPath;
            }

            // 2. CMakeCache.txt in engine workspace
            if (!string.IsNullOrEmpty(_enginePath))
            {
                var cmakeCachePath = Path.Combine(_enginePath, "workspace", "CMakeCache.txt");
                if (File.Exists(cmakeCachePath))
                {
                    try
                    {
                        foreach (var line in File.ReadLines(cmakeCachePath))
                        {
                            if (line.StartsWith("LY_3RDPARTY_PATH:PATH="))
                            {
                                var path = line.Substring("LY_3RDPARTY_PATH:PATH=".Length).Trim();
                                var packagesDir = Path.Combine(path, "packages");
                                if (Directory.Exists(packagesDir))
                                    return packagesDir;
                                break;
                            }
                        }
                    }
                    catch { /* ignore read errors */ }
                }
            }

            // 3. Well-known location
            var wellKnown = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".o3de", "3rdParty", "packages");
            if (Directory.Exists(wellKnown))
                return wellKnown;

            return null;
        }

        private string ResolveCorePath(string projectPath)
        {
            // Try multiple possible locations for O3DE.Core assembly
            // Prefer newer framework versions first (net10.0 > net9.0 > net8.0)
            var possiblePaths = new List<string>
            {
                Path.Combine(projectPath, "Assets/Scripts/O3DE.Core/bin/Release/net10.0/O3DE.Core.dll"),
                Path.Combine(projectPath, "Assets/Scripts/O3DE.Core/bin/Debug/net10.0/O3DE.Core.dll"),
                Path.Combine(projectPath, "Assets/Scripts/O3DE.Core/bin/Release/net9.0/O3DE.Core.dll"),
                Path.Combine(projectPath, "Assets/Scripts/O3DE.Core/bin/Debug/net9.0/O3DE.Core.dll"),
                Path.Combine(projectPath, "Assets/Scripts/O3DE.Core/bin/Release/net8.0/O3DE.Core.dll"),
                Path.Combine(projectPath, "Assets/Scripts/O3DE.Core/bin/Debug/net8.0/O3DE.Core.dll"),
            };

            // Also check the O3DESharp gem's Assets directory (where the core scripts live)
            if (!string.IsNullOrEmpty(_enginePath))
            {
                var gemCorePath = Path.Combine(_enginePath, "Gems", "O3DESharp", "Assets", "Scripts", "O3DE.Core");
                possiblePaths.Add(Path.Combine(gemCorePath, "bin/Release/net10.0/O3DE.Core.dll"));
                possiblePaths.Add(Path.Combine(gemCorePath, "bin/Debug/net10.0/O3DE.Core.dll"));
                possiblePaths.Add(Path.Combine(gemCorePath, "bin/Release/net9.0/O3DE.Core.dll"));
                possiblePaths.Add(Path.Combine(gemCorePath, "bin/Debug/net9.0/O3DE.Core.dll"));
                possiblePaths.Add(Path.Combine(gemCorePath, "bin/Release/net8.0/O3DE.Core.dll"));
                possiblePaths.Add(Path.Combine(gemCorePath, "bin/Debug/net8.0/O3DE.Core.dll"));
            }

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            // Return a placeholder path if not found - user can fix in generated .csproj
            Log($"  Warning: O3DE.Core.dll not found, using placeholder path");
            return Path.Combine(projectPath, "Assets/Scripts/O3DE.Core/bin/Release/net9.0/O3DE.Core.dll");
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
