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

        public GemDiscoveryService(bool verbose = false)
        {
            _verbose = verbose;
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

            // Add engine gems if engine path is specified
            if (!string.IsNullOrEmpty(project.Engine))
            {
                var enginePath = ResolveEnginePath(project.Engine, projectDir);
                if (Directory.Exists(enginePath))
                {
                    gemSearchPaths.Add(Path.Combine(enginePath, "Gems"));
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
                FindGemsInDirectory(searchPath, allGems);
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

        private void FindGemsInDirectory(string directory, Dictionary<string, GemDescriptor> gems)
        {
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

                // Recursively search subdirectories (max depth 3 to avoid deep traversal)
                var subdirs = Directory.GetDirectories(directory);
                foreach (var subdir in subdirs)
                {
                    var dirName = Path.GetFileName(subdir);
                    // Skip common non-gem directories
                    if (dirName == "bin" || dirName == "obj" || dirName == "build" || dirName == ".git")
                    {
                        continue;
                    }
                    FindGemsInDirectory(subdir, gems);
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

        private string ResolveEnginePath(string enginePath, string projectDir)
        {
            // Handle relative paths
            if (!Path.IsPathRooted(enginePath))
            {
                return Path.GetFullPath(Path.Combine(projectDir, enginePath));
            }
            return enginePath;
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
