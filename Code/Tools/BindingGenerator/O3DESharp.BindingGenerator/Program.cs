/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using O3DESharp.BindingGenerator.Configuration;
using O3DESharp.BindingGenerator.GemDiscovery;
using O3DESharp.BindingGenerator.Generation;

namespace O3DESharp.BindingGenerator
{
    class Program
    {
        static int Main(string[] args)
        {
            // ============================================================
            // Force line-buffered stdout/stderr so callers that pipe our
            // output (the editor's C# Project Manager, CI, etc.) see
            // progress lines as we emit them - not "silent for 3 minutes
            // then a wall of buffered text at the end".
            //
            // .NET's default behavior when stdout is redirected to a pipe
            // is block-buffering (typically 4KB). Console.WriteLine adds
            // the line to the buffer but doesn't flush; the buffer only
            // drains when it fills, the process exits, or someone calls
            // Flush() explicitly. That's exactly what was making the
            // generator look stuck: 11 gems' worth of "Processing gem:
            // X" lines were piling up in the buffer, and the editor's
            // log only refreshed when generation finished and the
            // child's exit flushed the pipe.
            //
            // Wrapping Console.Out in a StreamWriter with AutoFlush=true
            // makes every WriteLine call flush the underlying pipe, so
            // each line appears in the editor log as it's emitted.
            // Cheap (the writer is wrapping an already-open stream;
            // AutoFlush just toggles a bool on each call) but
            // transformative for the live-progress UX.
            // ============================================================
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });

            var rootCommand = new RootCommand("O3DE C# Binding Generator - Generates C# bindings from C++ headers using ClangSharp");

            // Global options
            var projectOption = new Option<string>(
                aliases: new[] { "--project", "-p" },
                getDefaultValue: () => ".",
                description: "Path to O3DE project.json or project directory");

            var engineOption = new Option<string?>(
                aliases: new[] { "--engine", "-e" },
                description: "Path to O3DE engine root. Auto-detected from project.json, ~/.o3de/o3de_manifest.json, or O3DE_ENGINE_PATH env var if not specified.");

            var configOption = new Option<string>(
                aliases: new[] { "--config", "-c" },
                getDefaultValue: () => "binding_config.json",
                description: "Path to binding configuration JSON file");

            var gemsOption = new Option<string[]>(
                aliases: new[] { "--gems", "-g" },
                description: "Specific gems to generate bindings for (comma-separated). If not specified, generates for all enabled gems.")
            {
                AllowMultipleArgumentsPerToken = true
            };

            var verboseOption = new Option<bool>(
                aliases: new[] { "--verbose", "-v" },
                description: "Enable verbose logging");

            var incrementalOption = new Option<bool>(
                aliases: new[] { "--incremental", "-i" },
                getDefaultValue: () => true,
                description: "Enable incremental builds (skip unchanged files)");

            var forceOption = new Option<bool>(
                aliases: new[] { "--force", "-f" },
                description: "Force regeneration of all bindings (ignore cache)");

            var requireAttributeOption = new Option<bool>(
                aliases: new[] { "--require-attribute" },
                getDefaultValue: () => false,
                description: "Only export declarations with O3DE_EXPORT_CSHARP attribute");

            var csharpOutputOption = new Option<string?>(
                aliases: new[] { "--output", "-o" },
                description: "Output directory for generated C# bindings. If specified, all gem bindings are written under this directory instead of each gem's own directory.");

            // List gems command
            var listGemsCommand = new Command("list-gems", "List all discovered gems in the project");
            listGemsCommand.AddOption(projectOption);
            listGemsCommand.AddOption(engineOption);
            listGemsCommand.AddOption(verboseOption);
            listGemsCommand.SetHandler((context) =>
            {
                var project = context.ParseResult.GetValueForOption(projectOption) ?? ".";
                var engine = context.ParseResult.GetValueForOption(engineOption);
                var verbose = context.ParseResult.GetValueForOption(verboseOption);
                context.ExitCode = ListGems(project, engine, verbose);
            });

            // Generate command
            var generateCommand = new Command("generate", "Generate C# bindings for gems");
            generateCommand.AddOption(projectOption);
            generateCommand.AddOption(engineOption);
            generateCommand.AddOption(configOption);
            generateCommand.AddOption(gemsOption);
            generateCommand.AddOption(verboseOption);
            generateCommand.AddOption(incrementalOption);
            generateCommand.AddOption(forceOption);
            generateCommand.AddOption(requireAttributeOption);
            generateCommand.AddOption(csharpOutputOption);
            generateCommand.SetHandler((context) =>
            {
                var project = context.ParseResult.GetValueForOption(projectOption) ?? ".";
                var engine = context.ParseResult.GetValueForOption(engineOption);
                var config = context.ParseResult.GetValueForOption(configOption) ?? "binding_config.json";
                var gems = context.ParseResult.GetValueForOption(gemsOption) ?? Array.Empty<string>();
                var verbose = context.ParseResult.GetValueForOption(verboseOption);
                var incremental = context.ParseResult.GetValueForOption(incrementalOption);
                var force = context.ParseResult.GetValueForOption(forceOption);
                var requireAttribute = context.ParseResult.GetValueForOption(requireAttributeOption);
                var csharpOutput = context.ParseResult.GetValueForOption(csharpOutputOption);
                context.ExitCode = GenerateBindings(project, engine, config, gems, verbose, incremental, force, requireAttribute, csharpOutput);
            });

            // Init config command
            var outputOption = new Option<string>(
                aliases: new[] { "--output", "-o" },
                getDefaultValue: () => "binding_config.json",
                description: "Output path for the configuration file");
            
            var initConfigCommand = new Command("init-config", "Create a default binding_config.json file");
            initConfigCommand.AddOption(outputOption);
            initConfigCommand.SetHandler((context) =>
            {
                var output = context.ParseResult.GetValueForOption(outputOption) ?? "binding_config.json";
                BindingConfigLoader.CreateDefaultFile(output);
                context.ExitCode = 0;
            });

            rootCommand.AddCommand(listGemsCommand);
            rootCommand.AddCommand(generateCommand);
            rootCommand.AddCommand(initConfigCommand);

            // Default action is generate
            rootCommand.AddOption(projectOption);
            rootCommand.AddOption(engineOption);
            rootCommand.AddOption(configOption);
            rootCommand.AddOption(gemsOption);
            rootCommand.AddOption(verboseOption);
            rootCommand.AddOption(incrementalOption);
            rootCommand.AddOption(forceOption);
            rootCommand.AddOption(requireAttributeOption);
            rootCommand.AddOption(csharpOutputOption);

            rootCommand.SetHandler((context) =>
            {
                var project = context.ParseResult.GetValueForOption(projectOption) ?? ".";
                var engine = context.ParseResult.GetValueForOption(engineOption);
                var config = context.ParseResult.GetValueForOption(configOption) ?? "binding_config.json";
                var gems = context.ParseResult.GetValueForOption(gemsOption) ?? Array.Empty<string>();
                var verbose = context.ParseResult.GetValueForOption(verboseOption);
                var incremental = context.ParseResult.GetValueForOption(incrementalOption);
                var force = context.ParseResult.GetValueForOption(forceOption);
                var requireAttribute = context.ParseResult.GetValueForOption(requireAttributeOption);
                var csharpOutput = context.ParseResult.GetValueForOption(csharpOutputOption);
                context.ExitCode = GenerateBindings(project, engine, config, gems, verbose, incremental, force, requireAttribute, csharpOutput);
            });

            return rootCommand.Invoke(args);
        }

        static int ListGems(string projectPath, string? enginePath, bool verbose)
        {
            try
            {
                Console.WriteLine("Discovering gems...\n");

                var discoveryService = new GemDiscoveryService(verbose, enginePath);
                var gems = discoveryService.DiscoverGems(projectPath);

                Console.WriteLine($"\nFound {gems.Count} gems:");
                Console.WriteLine(new string('-', 80));
                Console.WriteLine($"{"Gem Name",-30} {"Version",-10} {"Type",-10} {"Enabled",-10}");
                Console.WriteLine(new string('-', 80));

                foreach (var gem in gems.Values.OrderBy(g => g.GemName))
                {
                    var enabled = gem.IsEnabled ? "Yes" : "No";
                    Console.WriteLine($"{gem.GemName,-30} {gem.Version,-10} {gem.Type,-10} {enabled,-10}");
                }

                Console.WriteLine(new string('-', 80));
                Console.WriteLine($"Total: {gems.Count}, Enabled: {gems.Values.Count(g => g.IsEnabled)}");

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                if (verbose)
                {
                    Console.WriteLine(ex.StackTrace);
                }
                return 1;
            }
        }

        static int GenerateBindings(string projectPath, string? enginePath, string configPath, string[] specificGems, bool verbose, bool incremental, bool force, bool requireAttribute, string? csharpOutputDir)
        {
            try
            {
                Console.WriteLine("O3DE C# Binding Generator");
                Console.WriteLine("=========================\n");

                // Resolve a relative --config against the project path so the
                // lookup is stable regardless of CWD. This eliminates a class
                // of "looks in the wrong place" bug we used to paper over with
                // a duplicate binding_config.json under the tool directory.
                if (!Path.IsPathRooted(configPath))
                {
                    var projectAbs = Path.GetFullPath(projectPath);
                    var projectRoot = Directory.Exists(projectAbs)
                        ? projectAbs
                        : Path.GetDirectoryName(projectAbs) ?? projectAbs;
                    configPath = Path.Combine(projectRoot, configPath);
                }

                // Load configuration
                var config = BindingConfigLoader.Load(configPath);
                config.Global.Verbose = verbose;
                config.Global.IncrementalBuild = incremental;

                // Override require attribute setting if specified
                if (requireAttribute)
                {
                    config.Global.RequireExportAttribute = requireAttribute;
                }

                // Override C# output path if --output is specified
                if (!string.IsNullOrEmpty(csharpOutputDir))
                {
                    // When --output is specified, write each gem's bindings to a subdirectory
                    var absOutput = Path.GetFullPath(csharpOutputDir);
                    config.Global.CSharpOutputPath = Path.Combine(absOutput, "{GemName}");
                }

                Console.WriteLine($"Configuration: {(File.Exists(configPath) ? configPath : "default")}");
                Console.WriteLine($"Require O3DE_EXPORT_CSHARP attribute: {config.Global.RequireExportAttribute}");
                Console.WriteLine($"Verbose: {verbose}");
                Console.WriteLine($"Incremental: {incremental}");
                Console.WriteLine($"Force rebuild: {force}\n");

                // Discover gems
                var discoveryService = new GemDiscoveryService(verbose, enginePath);
                var allGems = discoveryService.DiscoverGems(projectPath);

                // Filter to specific gems if requested
                if (specificGems != null && specificGems.Length > 0)
                {
                    var requestedGems = specificGems.SelectMany(g => g.Split(',')).Select(g => g.Trim()).ToHashSet();
                    foreach (var gem in allGems.Values)
                    {
                        if (requestedGems.Contains(gem.GemName))
                        {
                            gem.IsEnabled = true;
                        }
                        else
                        {
                            gem.IsEnabled = false;
                        }
                    }

                    // Warn about requested gems that weren't found
                    var foundGemNames = allGems.Values.Where(g => g.IsEnabled).Select(g => g.GemName).ToHashSet();
                    foreach (var requested in requestedGems)
                    {
                        if (!foundGemNames.Contains(requested))
                        {
                            Console.WriteLine($"Warning: Requested gem '{requested}' was not found in discovered gems.");
                        }
                    }

                    Console.WriteLine($"Filtering to specific gems: {string.Join(", ", requestedGems)}\n");
                }

                // Build dependency order
                var sortedGems = discoveryService.BuildDependencyOrder(allGems, enabledOnly: true);

                if (sortedGems.Count == 0)
                {
                    Console.WriteLine("No enabled gems found to generate bindings for.");
                    return 0;
                }

                Console.WriteLine($"Generating bindings for {sortedGems.Count} gems:\n");
                foreach (var gem in sortedGems)
                {
                    Console.WriteLine($"  - {gem.GemName}");
                }
                Console.WriteLine();

                // Generate bindings
                var generator = new MultiGemBindingGenerator(config, verbose, force, discoveryService.ResolvedEnginePath);
                var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? projectPath;
                generator.GenerateAll(allGems, sortedGems, projectDir);

                Console.WriteLine("\n✓ Binding generation complete!");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n✗ Error: {ex.Message}");
                if (verbose)
                {
                    Console.WriteLine(ex.StackTrace);
                }
                return 1;
            }
        }
    }
}
