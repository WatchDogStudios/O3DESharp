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

namespace O3DESharp.BindingGenerator.GemDiscovery
{
    /// <summary>
    /// Service for discovering O3DE gems and building dependency graphs
    /// </summary>
    public class GemDiscoveryService
    {
        private readonly bool _verbose;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly string? _enginePathOverride;

        /// <summary>
        /// The resolved engine root path, available after DiscoverGems has been called.
        /// </summary>
        public string? ResolvedEnginePath { get; private set; }

        /// <summary>
        /// Maximum directory depth when recursively searching for gems.
        /// </summary>
        private const int MaxGemSearchDepth = 4;

        public GemDiscoveryService(bool verbose = false, string? enginePathOverride = null)
        {
            _verbose = verbose;
            _enginePathOverride = enginePathOverride;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }

        /// <summary>
        /// Discover all gems from an O3DE project
        /// </summary>
        /// <param name="projectPath">Path to project.json or project directory</param>
        /// <returns>Dictionary of gem name to gem descriptor</returns>
        public Dictionary<string, GemDescriptor> DiscoverGems(string projectPath)
        {
            // Normalize project path
            if (Directory.Exists(projectPath))
            {
                projectPath = Path.Combine(projectPath, "project.json");
            }

            if (!File.Exists(projectPath))
            {
                throw new FileNotFoundException($"Project file not found: {projectPath}");
            }

            Log($"Loading project from: {projectPath}");

            // Load project.json
            var projectJson = File.ReadAllText(projectPath);
            var project = JsonSerializer.Deserialize<ProjectDescriptor>(projectJson, _jsonOptions);
            if (project == null)
            {
                throw new InvalidOperationException("Failed to parse project.json");
            }

            Log($"Project: {project.ProjectName} v{project.Version}");
            Log($"Enabled gems: {project.GemNames.Count}");

            // Find all gems
            var projectDir = Path.GetDirectoryName(projectPath) ?? throw new InvalidOperationException("Invalid project path");
            var allGems = new Dictionary<string, GemDescriptor>();

            // Search for gems in common locations
            var gemSearchPaths = new List<string>
            {
                projectDir, // Gems in project directory
                Path.Combine(projectDir, "Gems"), // Gems/subfolder
            };

            // Resolve engine path: CLI override > project.json > manifest > env var > script-relative
            var enginePath = ResolveEnginePath(project.Engine, projectDir);
            ResolvedEnginePath = enginePath;
            if (!string.IsNullOrEmpty(enginePath) && Directory.Exists(enginePath))
            {
                gemSearchPaths.Add(Path.Combine(enginePath, "Gems"));
                Log($"Engine root resolved: {enginePath}");
            }
            else
            {
                Console.WriteLine(
                    "Warning: Could not resolve engine path. "
                    + "Set O3DE_ENGINE_PATH, pass --engine, or register the engine in ~/.o3de/o3de_manifest.json");
            }

            // Also check external gem directories from o3de_manifest.json
            foreach (var extPath in GetExternalGemPaths())
            {
                if (Directory.Exists(extPath))
                {
                    gemSearchPaths.Add(extPath);
                }
            }

            // Find all gem.json files
            foreach (var searchPath in gemSearchPaths.Distinct())
            {
                if (!Directory.Exists(searchPath))
                {
                    continue;
                }

                Log($"Searching for gems in: {searchPath}");
                FindGemsInDirectory(searchPath, allGems, 0);
            }

            // Mark enabled gems
            foreach (var gemName in project.GemNames)
            {
                if (allGems.TryGetValue(gemName, out var gem))
                {
                    gem.IsEnabled = true;
                }
                else
                {
                    Console.WriteLine($"Warning: Enabled gem '{gemName}' not found");
                }
            }

            Log($"Total gems discovered: {allGems.Count}");
            Log($"Enabled gems: {allGems.Values.Count(g => g.IsEnabled)}");

            return allGems;
        }

        /// <summary>
        /// Build a topologically sorted list of gems based on dependencies
        /// </summary>
        /// <param name="gems">All discovered gems</param>
        /// <param name="enabledOnly">Only include enabled gems</param>
        /// <returns>Topologically sorted list of gems</returns>
        public List<GemDescriptor> BuildDependencyOrder(Dictionary<string, GemDescriptor> gems, bool enabledOnly = true)
        {
            var gemsToProcess = enabledOnly 
                ? gems.Values.Where(g => g.IsEnabled).ToList()
                : gems.Values.ToList();

            Log($"Building dependency order for {gemsToProcess.Count} gems...");

            // Build adjacency list (gem -> dependencies)
            var graph = new Dictionary<string, List<string>>();
            var inDegree = new Dictionary<string, int>();

            foreach (var gem in gemsToProcess)
            {
                graph[gem.GemName] = new List<string>();
                inDegree[gem.GemName] = 0;
            }

            foreach (var gem in gemsToProcess)
            {
                foreach (var dep in gem.Dependencies)
                {
                    // Only include dependencies that are in our gem set
                    if (graph.ContainsKey(dep))
                    {
                        graph[dep].Add(gem.GemName);
                        inDegree[gem.GemName]++;
                    }
                }
            }

            // Topological sort using Kahn's algorithm
            var queue = new Queue<string>();
            foreach (var gem in gemsToProcess)
            {
                if (inDegree[gem.GemName] == 0)
                {
                    queue.Enqueue(gem.GemName);
                }
            }

            var sorted = new List<GemDescriptor>();
            while (queue.Count > 0)
            {
                var gemName = queue.Dequeue();
                sorted.Add(gems[gemName]);

                foreach (var dependent in graph[gemName])
                {
                    inDegree[dependent]--;
                    if (inDegree[dependent] == 0)
                    {
                        queue.Enqueue(dependent);
                    }
                }
            }

            // Check for cycles
            if (sorted.Count != gemsToProcess.Count)
            {
                var missing = gemsToProcess.Where(g => !sorted.Contains(g)).Select(g => g.GemName);
                throw new InvalidOperationException($"Circular dependency detected among gems: {string.Join(", ", missing)}");
            }

            Log($"Dependency order: {string.Join(" -> ", sorted.Select(g => g.GemName))}");

            return sorted;
        }

        private void FindGemsInDirectory(string directory, Dictionary<string, GemDescriptor> gems, int depth)
        {
            if (depth > MaxGemSearchDepth)
            {
                return;
            }

            try
            {
                // Check current directory for gem.json
                var gemJsonPath = Path.Combine(directory, "gem.json");
                if (File.Exists(gemJsonPath))
                {
                    var gem = LoadGemDescriptor(gemJsonPath);
                    if (gem != null && !gems.ContainsKey(gem.GemName))
                    {
                        gems[gem.GemName] = gem;
                        Log($"Found gem: {gem.GemName} at {gem.GemPath}");
                    }
                }

                // Recursively search subdirectories
                var subdirs = Directory.GetDirectories(directory);
                foreach (var subdir in subdirs)
                {
                    var dirName = Path.GetFileName(subdir);
                    // Skip common non-gem directories
                    if (dirName == "bin" || dirName == "obj" || dirName == "build" || dirName == ".git")
                    {
                        continue;
                    }
                    FindGemsInDirectory(subdir, gems, depth + 1);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip directories we don't have access to
            }
        }

        private GemDescriptor? LoadGemDescriptor(string gemJsonPath)
        {
            try
            {
                var json = File.ReadAllText(gemJsonPath);
                var gem = JsonSerializer.Deserialize<GemDescriptor>(json, _jsonOptions);
                if (gem != null)
                {
                    gem.GemPath = Path.GetDirectoryName(gemJsonPath) ?? string.Empty;
                }
                return gem;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to load gem from {gemJsonPath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Resolve the O3DE engine root directory.
        ///
        /// Resolution order:
        /// 1. Explicit --engine CLI override
        /// 2. project.json "engine" field treated as an absolute path (if rooted and exists)
        /// 3. project.json "engine" field treated as an engine name, looked up in ~/.o3de/o3de_manifest.json
        /// 4. Walk up from projectDir looking for engine.json (source-engine layout)
        /// 5. O3DE_ENGINE_PATH environment variable
        /// 6. Walk up from this assembly's location (when the tool lives inside the engine tree)
        /// </summary>
        private string? ResolveEnginePath(string? engineRef, string projectDir)
        {
            // 1. Explicit CLI override
            if (!string.IsNullOrEmpty(_enginePathOverride))
            {
                var overridePath = Path.GetFullPath(_enginePathOverride);
                if (Directory.Exists(overridePath) && File.Exists(Path.Combine(overridePath, "engine.json")))
                {
                    Log($"Engine resolved via --engine override: {overridePath}");
                    return overridePath;
                }
                Log($"Warning: --engine path does not contain engine.json: {overridePath}");
            }

            // 2. If engine field looks like an absolute path that exists, use it directly
            if (!string.IsNullOrEmpty(engineRef) && Path.IsPathRooted(engineRef))
            {
                if (Directory.Exists(engineRef) && File.Exists(Path.Combine(engineRef, "engine.json")))
                {
                    Log($"Engine resolved via absolute path in project.json: {engineRef}");
                    return engineRef;
                }
            }

            // 3. Look up engine name in ~/.o3de/o3de_manifest.json
            if (!string.IsNullOrEmpty(engineRef))
            {
                var manifestPath = GetManifestPath();
                if (File.Exists(manifestPath))
                {
                    var resolved = LookupEngineInManifest(manifestPath, engineRef);
                    if (resolved != null)
                    {
                        Log($"Engine resolved via o3de_manifest.json: {resolved}");
                        return resolved;
                    }
                }
            }

            // 4. Walk up from project directory looking for engine.json (source-engine layout)
            var current = new DirectoryInfo(projectDir);
            for (int i = 0; i < 6 && current != null; i++)
            {
                if (File.Exists(Path.Combine(current.FullName, "engine.json")))
                {
                    Log($"Engine resolved by walking up from project: {current.FullName}");
                    return current.FullName;
                }
                current = current.Parent;
            }

            // 5. O3DE_ENGINE_PATH environment variable
            var envPath = Environment.GetEnvironmentVariable("O3DE_ENGINE_PATH");
            if (!string.IsNullOrEmpty(envPath) && Directory.Exists(envPath))
            {
                Log($"Engine resolved via O3DE_ENGINE_PATH env var: {envPath}");
                return envPath;
            }

            // 6. Walk up from this tool's assembly location
            var assemblyDir = AppContext.BaseDirectory;
            current = new DirectoryInfo(assemblyDir);
            for (int i = 0; i < 10 && current != null; i++)
            {
                if (File.Exists(Path.Combine(current.FullName, "engine.json")))
                {
                    Log($"Engine resolved via tool location: {current.FullName}");
                    return current.FullName;
                }
                current = current.Parent;
            }

            return null;
        }

        /// <summary>
        /// Look up an engine by name in the O3DE manifest file.
        /// </summary>
        private string? LookupEngineInManifest(string manifestPath, string engineNameOrPath)
        {
            try
            {
                var json = File.ReadAllText(manifestPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Check "engines" array — each entry is a path to a directory containing engine.json
                if (root.TryGetProperty("engines", out var enginesArray) && enginesArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in enginesArray.EnumerateArray())
                    {
                        var engineDir = entry.GetString();
                        if (string.IsNullOrEmpty(engineDir) || !Directory.Exists(engineDir))
                            continue;

                        var engineJsonPath = Path.Combine(engineDir, "engine.json");
                        if (!File.Exists(engineJsonPath))
                            continue;

                        // Check if engine name matches
                        try
                        {
                            var engineJson = File.ReadAllText(engineJsonPath);
                            using var engineDoc = JsonDocument.Parse(engineJson);
                            if (engineDoc.RootElement.TryGetProperty("engine_name", out var nameProp))
                            {
                                var name = nameProp.GetString();
                                if (string.Equals(name, engineNameOrPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    return engineDir;
                                }
                            }
                        }
                        catch
                        {
                            // Skip malformed engine.json
                        }
                    }
                }

                // Check "engines_path" — directories that contain engine folders
                if (root.TryGetProperty("engines_path", out var enginesPathArray) && enginesPathArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in enginesPathArray.EnumerateArray())
                    {
                        var enginesRoot = entry.GetString();
                        if (string.IsNullOrEmpty(enginesRoot) || !Directory.Exists(enginesRoot))
                            continue;

                        foreach (var child in Directory.GetDirectories(enginesRoot))
                        {
                            var engineJsonPath = Path.Combine(child, "engine.json");
                            if (!File.Exists(engineJsonPath))
                                continue;

                            try
                            {
                                var engineJson = File.ReadAllText(engineJsonPath);
                                using var engineDoc = JsonDocument.Parse(engineJson);
                                if (engineDoc.RootElement.TryGetProperty("engine_name", out var nameProp))
                                {
                                    var name = nameProp.GetString();
                                    if (string.Equals(name, engineNameOrPath, StringComparison.OrdinalIgnoreCase))
                                    {
                                        return child;
                                    }
                                }
                            }
                            catch
                            {
                                // Skip malformed engine.json
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Warning: Failed to read o3de_manifest.json: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Get the path to the O3DE manifest file (~/.o3de/o3de_manifest.json).
        /// </summary>
        private static string GetManifestPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".o3de",
                "o3de_manifest.json");
        }

        /// <summary>
        /// Get external gem paths from o3de_manifest.json.
        /// </summary>
        private List<string> GetExternalGemPaths()
        {
            var paths = new List<string>();
            var manifestPath = GetManifestPath();
            if (!File.Exists(manifestPath))
                return paths;

            try
            {
                var json = File.ReadAllText(manifestPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("external_subdirectories", out var extArray) &&
                    extArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in extArray.EnumerateArray())
                    {
                        var p = entry.GetString();
                        if (!string.IsNullOrEmpty(p) && Directory.Exists(p))
                            paths.Add(p);
                    }
                }

                if (root.TryGetProperty("gems_path", out var gemsPathArray) &&
                    gemsPathArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in gemsPathArray.EnumerateArray())
                    {
                        var p = entry.GetString();
                        if (!string.IsNullOrEmpty(p) && Directory.Exists(p))
                            paths.Add(p);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Warning: Failed to read external gem paths from manifest: {ex.Message}");
            }

            return paths;
        }

        private void Log(string message)
        {
            if (_verbose)
            {
                Console.WriteLine($"[GemDiscovery] {message}");
            }
        }
    }
}
