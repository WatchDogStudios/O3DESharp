/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace O3DESharp.BindingGenerator.Configuration
{
    /// <summary>
    /// Loads and saves binding configuration files
    /// </summary>
    public class BindingConfigLoader
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        private static readonly Regex EnvVarPattern = new Regex(@"\$\{(\w+)\}", RegexOptions.Compiled);

        /// <summary>
        /// Load configuration from a JSON file
        /// </summary>
        /// <param name="configPath">Path to the configuration file</param>
        /// <returns>Loaded configuration, or default if file doesn't exist</returns>
        public static BindingConfig Load(string configPath)
        {
            if (!File.Exists(configPath))
            {
                Console.WriteLine($"Configuration file not found: {configPath}");
                Console.WriteLine("Using default configuration.");
                return CreateDefault();
            }

            try
            {
                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<BindingConfig>(json, JsonOptions);
                config ??= CreateDefault();
                ExpandEnvironmentVariables(config);
                return config;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading configuration: {ex.Message}");
                Console.WriteLine("Using default configuration.");
                return CreateDefault();
            }
        }

        /// <summary>
        /// Save configuration to a JSON file
        /// </summary>
        /// <param name="config">Configuration to save</param>
        /// <param name="configPath">Path to save the configuration</param>
        public static void Save(BindingConfig config, string configPath)
        {
            try
            {
                var json = JsonSerializer.Serialize(config, JsonOptions);
                File.WriteAllText(configPath, json);
                Console.WriteLine($"Configuration saved to: {configPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving configuration: {ex.Message}");
            }
        }

        /// <summary>
        /// Create a default configuration
        /// </summary>
        /// <returns>Default binding configuration</returns>
        public static BindingConfig CreateDefault()
        {
            var config = new BindingConfig();

            // Add some common O3DE include paths
            config.Global.IncludePaths.Add("${O3DE_ENGINE_PATH}/Code");
            config.Global.IncludePaths.Add("${O3DE_ENGINE_PATH}/Gems");

            return config;
        }

        /// <summary>
        /// Create a default configuration file if it doesn't exist
        /// </summary>
        /// <param name="configPath">Path where to create the configuration</param>
        public static void CreateDefaultFile(string configPath)
        {
            if (File.Exists(configPath))
            {
                Console.WriteLine($"Configuration file already exists: {configPath}");
                return;
            }

            var config = CreateDefault();
            Save(config, configPath);
        }

        /// <summary>
        /// Expand ${VAR_NAME} references in all path-like configuration fields.
        /// </summary>
        private static void ExpandEnvironmentVariables(BindingConfig config)
        {
            for (int i = 0; i < config.Global.IncludePaths.Count; i++)
            {
                config.Global.IncludePaths[i] = ExpandEnvVars(config.Global.IncludePaths[i]);
            }

            config.Global.CSharpOutputPath = ExpandEnvVars(config.Global.CSharpOutputPath);
            config.Global.CppOutputPath = ExpandEnvVars(config.Global.CppOutputPath);

            foreach (var gem in config.Gems.Values)
            {
                for (int i = 0; i < gem.IncludePaths.Count; i++)
                {
                    gem.IncludePaths[i] = ExpandEnvVars(gem.IncludePaths[i]);
                }
            }
        }

        /// <summary>
        /// Replace ${VAR_NAME} with the corresponding environment variable value.
        /// Unresolved variables are left as-is.
        /// </summary>
        private static string ExpandEnvVars(string input)
        {
            if (string.IsNullOrEmpty(input) || !input.Contains("${"))
                return input;

            return EnvVarPattern.Replace(input, match =>
            {
                var varName = match.Groups[1].Value;
                var value = Environment.GetEnvironmentVariable(varName);
                return value ?? match.Value; // leave unresolved as-is
            });
        }
    }
}
