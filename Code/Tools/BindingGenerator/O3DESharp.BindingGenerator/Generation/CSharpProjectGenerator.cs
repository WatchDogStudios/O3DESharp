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
using System.Text;
using O3DESharp.BindingGenerator.GemDiscovery;

namespace O3DESharp.BindingGenerator.Generation
{
    /// <summary>
    /// Generates .csproj files for gem bindings with proper dependencies
    /// </summary>
    public class CSharpProjectGenerator
    {
        private readonly bool _verbose;

        public CSharpProjectGenerator(bool verbose = false)
        {
            _verbose = verbose;
        }

        /// <summary>
        /// Generate a .csproj file for a gem
        /// </summary>
        /// <param name="gem">Gem descriptor</param>
        /// <param name="outputDirectory">Output directory for the project file</param>
        /// <param name="allGems">All discovered gems for dependency resolution</param>
        /// <param name="coreAssemblyPath">Path to O3DE.Core assembly</param>
        public void Generate(GemDescriptor gem, string outputDirectory, Dictionary<string, GemDescriptor> allGems, string coreAssemblyPath)
        {
            Directory.CreateDirectory(outputDirectory);

            Log($"Generating .csproj for gem '{gem.GemName}' to {outputDirectory}");

            var sb = new StringBuilder();

            sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
            sb.AppendLine();
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine("    <TargetFramework>net8.0</TargetFramework>");
            sb.AppendLine("    <ImplicitUsings>disable</ImplicitUsings>");
            sb.AppendLine("    <Nullable>enable</Nullable>");
            sb.AppendLine("    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>");
            sb.AppendLine("    <LangVersion>latest</LangVersion>");
            sb.AppendLine($"    <AssemblyName>{gem.GemName}</AssemblyName>");
            sb.AppendLine($"    <RootNamespace>O3DE.{gem.GemName}</RootNamespace>");
            sb.AppendLine($"    <Version>{gem.Version}</Version>");
            sb.AppendLine("    <GenerateDocumentationFile>true</GenerateDocumentationFile>");
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine();

            // Add reference to O3DE.Core
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <Reference Include=\"O3DE.Core\">");
            sb.AppendLine($"      <HintPath>{coreAssemblyPath}</HintPath>");
            sb.AppendLine("      <Private>false</Private>");
            sb.AppendLine("    </Reference>");

            // Add project references for gem dependencies
            var gemDependencies = gem.Dependencies
                .Where(dep => allGems.ContainsKey(dep) && allGems[dep].IsEnabled)
                .ToList();

            if (gemDependencies.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("    <!-- Gem Dependencies -->");
                foreach (var depName in gemDependencies)
                {
                    var depGem = allGems[depName];
                    var relativePath = $"../{depGem.GemName}/{depGem.GemName}.csproj";
                    sb.AppendLine($"    <ProjectReference Include=\"{relativePath}\" />");
                }
            }

            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine();
            sb.AppendLine("</Project>");

            var outputPath = Path.Combine(outputDirectory, $"{gem.GemName}.csproj");
            File.WriteAllText(outputPath, sb.ToString());
            Log($"  Generated: {outputPath}");
        }

        private void Log(string message)
        {
            if (_verbose)
            {
                Console.WriteLine($"[ProjectGen] {message}");
            }
        }
    }
}
