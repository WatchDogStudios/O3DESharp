/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.IO;
using System.Linq;
using System.Text;
using O3DESharp.BindingGenerator.Parsing;

namespace O3DESharp.BindingGenerator.Generation
{
    /// <summary>
    /// Generates C++ Coral registration code
    /// </summary>
    public class CppRegistrationGenerator
    {
        private readonly bool _verbose;

        public CppRegistrationGenerator(bool verbose = false)
        {
            _verbose = verbose;
        }

        /// <summary>
        /// Generate C++ registration code from parsed bindings
        /// </summary>
        /// <param name="bindings">Parsed binding declarations</param>
        /// <param name="outputDirectory">Output directory for generated files</param>
        public void Generate(ParsedBindings bindings, string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);

            Log($"Generating C++ registration code for gem '{bindings.GemName}' to {outputDirectory}");

            GenerateRegistrationFile(bindings, outputDirectory);
        }

        private void GenerateRegistrationFile(ParsedBindings bindings, string outputDirectory)
        {
            var sb = new StringBuilder();

            // File header
            AppendFileHeader(sb);

            sb.AppendLine("#include <Coral/Assembly.hpp>");
            sb.AppendLine("#include <AzCore/Console/ILogger.h>");
            sb.AppendLine();
            sb.AppendLine($"namespace {bindings.GemName}::Generated");
            sb.AppendLine("{");

            // Forward declarations for binding functions
            foreach (var parsedClass in bindings.Classes)
            {
                foreach (var method in parsedClass.Methods)
                {
                    var funcName = $"{parsedClass.Name}_{method.Name}";
                    sb.AppendLine($"    void {funcName}(/* parameters */);");
                }
            }

            foreach (var function in bindings.Functions)
            {
                sb.AppendLine($"    void {function.Name}(/* parameters */);");
            }

            sb.AppendLine();
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Register all generated bindings with the Coral assembly");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    void RegisterBindings(Coral::ManagedAssembly* assembly)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (assembly == nullptr)");
            sb.AppendLine("        {");
            sb.AppendLine("            AZLOG_ERROR(\"RegisterBindings - Assembly is null\");");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        AZLOG_INFO(\"Registering {bindings.GemName} bindings to assembly '%s'\", assembly->GetName().data());");
            sb.AppendLine();

            // Register each class's methods
            foreach (var parsedClass in bindings.Classes)
            {
                sb.AppendLine($"        // {parsedClass.Name} bindings");
                foreach (var method in parsedClass.Methods)
                {
                    var funcName = $"{parsedClass.Name}_{method.Name}";
                    sb.AppendLine($"        assembly->AddInternalCall(\"{bindings.GemName}.InternalCalls\", \"{funcName}\", reinterpret_cast<void*>(&{funcName}));");
                }
            }

            // Register standalone functions
            if (bindings.Functions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("        // Standalone function bindings");
                foreach (var function in bindings.Functions)
                {
                    sb.AppendLine($"        assembly->AddInternalCall(\"{bindings.GemName}.InternalCalls\", \"{function.Name}\", reinterpret_cast<void*>(&{function.Name}));");
                }
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            var outputPath = Path.Combine(outputDirectory, "BindingRegistration.g.cpp");
            File.WriteAllText(outputPath, sb.ToString());
            Log($"  Generated: {outputPath}");
        }

        private void AppendFileHeader(StringBuilder sb)
        {
            sb.AppendLine("/*");
            sb.AppendLine(" * Copyright (c) Contributors to the Open 3D Engine Project.");
            sb.AppendLine(" * For complete copyright and license terms please see the LICENSE at the root of this distribution.");
            sb.AppendLine(" *");
            sb.AppendLine(" * SPDX-License-Identifier: Apache-2.0 OR MIT");
            sb.AppendLine(" *");
            sb.AppendLine(" */");
            sb.AppendLine();
            sb.AppendLine("// AUTO-GENERATED FILE - DO NOT EDIT");
            sb.AppendLine($"// Generated by O3DESharp.BindingGenerator on {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
        }

        private void Log(string message)
        {
            if (_verbose)
            {
                Console.WriteLine($"[CppGen] {message}");
            }
        }
    }
}
