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
            var rootCommand = new RootCommand("O3DE C# Binding Generator - Generates C# bindings from C++ headers using ClangSharp");

            // Global options
            var projectOption = new Option<string>(
                aliases: new[] { "--project", "-p" },
                getDefaultValue: () => ".",
                description: "Path to O3DE project.json or project directory");

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

            var requireAttributeOption = new Option<bool>(
                aliases: new[] { "--require-attribute" },
                getDefaultValue: () => false,
                description: "Only export declarations with O3DE_EXPORT_CSHARP attribute");

            // List gems command
            var listGemsCommand = new Command("list-gems", "List all discovered gems in the project");
            listGemsCommand.AddOption(projectOption);
            listGemsCommand.AddOption(verboseOption);
            listGemsCommand.SetHandler((context) =>
            {
                var project = context.ParseResult.GetValueForOption(projectOption) ?? ".";
                var verbose = context.ParseResult.GetValueForOption(verboseOption);
                context.ExitCode = ListGems(project, verbose);
            });

            // Generate command
            var generateCommand = new Command("generate", "Generate C# bindings for gems");
            generateCommand.AddOption(projectOption);
            generateCommand.AddOption(configOption);
            generateCommand.AddOption(gemsOption);
            generateCommand.AddOption(verboseOption);
            generateCommand.AddOption(incrementalOption);
            generateCommand.AddOption(requireAttributeOption);
            generateCommand.SetHandler((context) =>
            {
                var project = context.ParseResult.GetValueForOption(projectOption) ?? ".";
                var config = context.ParseResult.GetValueForOption(configOption) ?? "binding_config.json";
                var gems = context.ParseResult.GetValueForOption(gemsOption) ?? Array.Empty<string>();
                var verbose = context.ParseResult.GetValueForOption(verboseOption);
                var incremental = context.ParseResult.GetValueForOption(incrementalOption);
                var requireAttribute = context.ParseResult.GetValueForOption(requireAttributeOption);
                context.ExitCode = GenerateBindings(project, config, gems, verbose, incremental, requireAttribute);
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
            rootCommand.AddOption(configOption);
            rootCommand.AddOption(gemsOption);
            rootCommand.AddOption(verboseOption);
            rootCommand.AddOption(incrementalOption);
            rootCommand.AddOption(requireAttributeOption);
            
            rootCommand.SetHandler((context) =>
            {
                var project = context.ParseResult.GetValueForOption(projectOption) ?? ".";
                var config = context.ParseResult.GetValueForOption(configOption) ?? "binding_config.json";
                var gems = context.ParseResult.GetValueForOption(gemsOption) ?? Array.Empty<string>();
                var verbose = context.ParseResult.GetValueForOption(verboseOption);
                var incremental = context.ParseResult.GetValueForOption(incrementalOption);
                var requireAttribute = context.ParseResult.GetValueForOption(requireAttributeOption);
                context.ExitCode = GenerateBindings(project, config, gems, verbose, incremental, requireAttribute);
            });

            return rootCommand.Invoke(args);
        }

        static int ListGems(string projectPath, bool verbose)
        {
            try
            {
                Console.WriteLine("Discovering gems...\n");

                var discoveryService = new GemDiscoveryService(verbose);
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

        static int GenerateBindings(string projectPath, string configPath, string[] specificGems, bool verbose, bool incremental, bool requireAttribute)
        {
            try
            {
                Console.WriteLine("O3DE C# Binding Generator");
                Console.WriteLine("=========================\n");

                // Load configuration
                var config = BindingConfigLoader.Load(configPath);
                config.Global.Verbose = verbose;
                config.Global.IncrementalBuild = incremental;
                
                // Override require attribute setting if specified
                if (requireAttribute)
                {
                    config.Global.RequireExportAttribute = requireAttribute;
                }

                Console.WriteLine($"Configuration: {(File.Exists(configPath) ? configPath : "default")}");
                Console.WriteLine($"Require O3DE_EXPORT_CSHARP attribute: {config.Global.RequireExportAttribute}");
                Console.WriteLine($"Verbose: {verbose}");
                Console.WriteLine($"Incremental: {incremental}\n");

                // Discover gems
                var discoveryService = new GemDiscoveryService(verbose);
                var allGems = discoveryService.DiscoverGems(projectPath);

                // Filter to specific gems if requested
                if (specificGems != null && specificGems.Length > 0)
                {
                    var requestedGems = specificGems.SelectMany(g => g.Split(',')).Select(g => g.Trim()).ToHashSet();
                    foreach (var gem in allGems.Values)
                    {
                        if (!requestedGems.Contains(gem.GemName))
                        {
                            gem.IsEnabled = false;
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
                var generator = new MultiGemBindingGenerator(config, verbose);
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
