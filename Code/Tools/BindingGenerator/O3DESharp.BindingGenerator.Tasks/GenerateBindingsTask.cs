/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace O3DESharp.BindingGenerator.Tasks
{
    /// <summary>
    /// MSBuild task for generating C# bindings from C++ headers at design-time.
    /// This allows IntelliSense and code completion to work with generated bindings
    /// before a full build is performed.
    /// </summary>
    public class GenerateBindingsTask : Task
    {
        /// <summary>
        /// Path to the project file (project.json or gem.json)
        /// </summary>
        [Required]
        public string ProjectPath { get; set; } = string.Empty;

        /// <summary>
        /// Path to the binding configuration file
        /// </summary>
        public string ConfigPath { get; set; } = "binding_config.json";

        /// <summary>
        /// Path to the binding generator executable
        /// </summary>
        public string GeneratorPath { get; set; } = string.Empty;

        /// <summary>
        /// Specific gems to generate bindings for (optional)
        /// </summary>
        public string[]? Gems { get; set; }

        /// <summary>
        /// Enable verbose logging
        /// </summary>
        public bool Verbose { get; set; }

        /// <summary>
        /// Force regeneration even if cached
        /// </summary>
        public bool Force { get; set; }

        /// <summary>
        /// Enable incremental builds
        /// </summary>
        public bool Incremental { get; set; } = true;

        /// <summary>
        /// List of generated C# files (output)
        /// </summary>
        [Output]
        public ITaskItem[]? GeneratedFiles { get; set; }

        /// <summary>
        /// Execute the binding generation task
        /// </summary>
        public override bool Execute()
        {
            try
            {
                Log.LogMessage(MessageImportance.Normal, "O3DE Binding Generator - Design-time generation starting...");

                // Find the generator executable
                var generatorPath = FindGenerator();
                if (string.IsNullOrEmpty(generatorPath))
                {
                    Log.LogError("Could not find O3DESharp.BindingGenerator executable. Please build the generator first or specify GeneratorPath.");
                    return false;
                }

                // Build command line arguments
                var args = BuildArguments();

                Log.LogMessage(MessageImportance.Low, $"Running: {generatorPath} {args}");

                // Run the generator
                var startInfo = new ProcessStartInfo
                {
                    FileName = generatorPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    Log.LogError("Failed to start binding generator process");
                    return false;
                }

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();

                process.WaitForExit();

                // Log output
                if (!string.IsNullOrWhiteSpace(output))
                {
                    foreach (var line in output.Split('\n'))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            Log.LogMessage(Verbose ? MessageImportance.High : MessageImportance.Normal, line.Trim());
                        }
                    }
                }

                // Log errors
                if (!string.IsNullOrWhiteSpace(error))
                {
                    foreach (var line in error.Split('\n'))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            Log.LogWarning(line.Trim());
                        }
                    }
                }

                if (process.ExitCode != 0)
                {
                    Log.LogError($"Binding generator failed with exit code {process.ExitCode}");
                    return false;
                }

                // Collect generated files for output
                GeneratedFiles = CollectGeneratedFiles();

                Log.LogMessage(MessageImportance.Normal, $"O3DE Binding Generator completed. Generated {GeneratedFiles?.Length ?? 0} files.");
                return true;
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, true);
                return false;
            }
        }

        private string FindGenerator()
        {
            // If explicitly specified, use that
            if (!string.IsNullOrEmpty(GeneratorPath) && File.Exists(GeneratorPath))
            {
                return GeneratorPath;
            }

            // Look for the generator in common locations
            var projectDir = Path.GetDirectoryName(ProjectPath);
            if (string.IsNullOrEmpty(projectDir))
            {
                projectDir = ".";
            }

            var possiblePaths = new[]
            {
                // Relative to project
                Path.Combine(projectDir, "Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/bin/Release/net8.0/O3DESharp.BindingGenerator.exe"),
                Path.Combine(projectDir, "Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/bin/Debug/net8.0/O3DESharp.BindingGenerator.exe"),
                Path.Combine(projectDir, "Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/bin/Release/net8.0/O3DESharp.BindingGenerator"),
                Path.Combine(projectDir, "Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/bin/Debug/net8.0/O3DESharp.BindingGenerator"),
                
                // As a dotnet tool
                "dotnet O3DESharp.BindingGenerator",
            };

            foreach (var path in possiblePaths)
            {
                if (path.StartsWith("dotnet "))
                {
                    // Check if dotnet tool is available
                    return path;
                }
                
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return string.Empty;
        }

        private string BuildArguments()
        {
            var args = new List<string>
            {
                "generate",
                $"--project \"{ProjectPath}\""
            };

            if (!string.IsNullOrEmpty(ConfigPath))
            {
                args.Add($"--config \"{ConfigPath}\"");
            }

            if (Gems != null && Gems.Length > 0)
            {
                args.Add($"--gems {string.Join(",", Gems)}");
            }

            if (Verbose)
            {
                args.Add("--verbose");
            }

            if (Force)
            {
                args.Add("--force");
            }

            if (Incremental)
            {
                args.Add("--incremental");
            }

            return string.Join(" ", args);
        }

        private ITaskItem[] CollectGeneratedFiles()
        {
            var files = new List<ITaskItem>();

            var projectDir = Path.GetDirectoryName(ProjectPath);
            if (string.IsNullOrEmpty(projectDir))
            {
                projectDir = ".";
            }

            // Look for generated files in Assets/Scripts/*/*.g.cs
            var assetsPath = Path.Combine(projectDir, "Assets/Scripts");
            if (Directory.Exists(assetsPath))
            {
                foreach (var file in Directory.GetFiles(assetsPath, "*.g.cs", SearchOption.AllDirectories))
                {
                    files.Add(new TaskItem(file));
                }
            }

            return files.ToArray();
        }
    }
}
