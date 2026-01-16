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
using O3DESharp.BindingGenerator.Parsing;

namespace O3DESharp.BindingGenerator.Generation
{
    /// <summary>
    /// Generates C# P/Invoke declarations and wrapper classes
    /// </summary>
    public class CSharpCodeGenerator
    {
        private readonly string _namespace;
        private readonly bool _verbose;

        public CSharpCodeGenerator(string namespaceRoot, bool verbose = false)
        {
            _namespace = namespaceRoot;
            _verbose = verbose;
        }

        /// <summary>
        /// Generate C# code from parsed bindings
        /// </summary>
        /// <param name="bindings">Parsed binding declarations</param>
        /// <param name="outputDirectory">Output directory for generated files</param>
        public void Generate(ParsedBindings bindings, string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);

            Log($"Generating C# code for gem '{bindings.GemName}' to {outputDirectory}");

            // Generate InternalCalls file
            GenerateInternalCalls(bindings, outputDirectory);

            // Generate wrapper classes
            foreach (var parsedClass in bindings.Classes)
            {
                GenerateWrapperClass(parsedClass, bindings.GemName, outputDirectory);
            }

            // Generate enums
            foreach (var parsedEnum in bindings.Enums)
            {
                GenerateEnum(parsedEnum, bindings.GemName, outputDirectory);
            }

            Log($"Generated {bindings.Classes.Count} wrapper classes and {bindings.Enums.Count} enums");
        }

        private void GenerateInternalCalls(ParsedBindings bindings, string outputDirectory)
        {
            var sb = new StringBuilder();

            // File header
            AppendFileHeader(sb);

            sb.AppendLine("using System;");
            sb.AppendLine("using System.Runtime.InteropServices;");
            sb.AppendLine("using Coral.Managed.Interop;");
            sb.AppendLine();
            sb.AppendLine($"namespace {_namespace}.{bindings.GemName}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine($"    /// Internal calls to native {bindings.GemName} C++ functions.");
            sb.AppendLine("    /// These are static function pointer fields that Coral binds to native code at runtime.");
            sb.AppendLine("    ///");
            sb.AppendLine("    /// DO NOT call these directly - use the wrapper classes instead.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    internal static unsafe class InternalCalls");
            sb.AppendLine("    {");
            sb.AppendLine("        // Suppress CS0649: Field is never assigned (Coral assigns these at runtime)");
            sb.AppendLine("#pragma warning disable 0649");
            sb.AppendLine();

            // Generate function pointers for each class
            foreach (var parsedClass in bindings.Classes)
            {
                sb.AppendLine($"        // ============================================================");
                sb.AppendLine($"        // {parsedClass.Name} Functions");
                sb.AppendLine($"        // ============================================================");
                sb.AppendLine();

                foreach (var method in parsedClass.Methods)
                {
                    GenerateInternalCallDeclaration(sb, parsedClass, method);
                }

                sb.AppendLine();
            }

            // Generate function pointers for standalone functions
            if (bindings.Functions.Count > 0)
            {
                sb.AppendLine($"        // ============================================================");
                sb.AppendLine($"        // Standalone Functions");
                sb.AppendLine($"        // ============================================================");
                sb.AppendLine();

                foreach (var function in bindings.Functions)
                {
                    GenerateInternalCallDeclaration(sb, function);
                }
            }

            sb.AppendLine("#pragma warning restore 0649");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            var outputPath = Path.Combine(outputDirectory, "InternalCalls.g.cs");
            File.WriteAllText(outputPath, sb.ToString());
            Log($"  Generated: {outputPath}");
        }

        private void GenerateInternalCallDeclaration(StringBuilder sb, ParsedClass parsedClass, ParsedMethod method)
        {
            var paramList = new List<string>();

            // Add 'this' pointer for non-static methods
            if (!method.IsStatic)
            {
                paramList.Add("IntPtr");
            }

            // Add parameters
            foreach (var param in method.Parameters)
            {
                paramList.Add(param.Type.CSharpTypeName);
            }

            var returnType = method.ReturnType.CSharpTypeName;
            var paramsStr = string.Join(", ", paramList);

            sb.AppendLine($"        internal static delegate* unmanaged<{paramsStr}, {returnType}> {parsedClass.Name}_{method.Name};");
        }

        private void GenerateInternalCallDeclaration(StringBuilder sb, ParsedFunction function)
        {
            var paramList = function.Parameters.Select(p => p.Type.CSharpTypeName).ToList();
            var paramsStr = string.Join(", ", paramList);
            var returnType = function.ReturnType.CSharpTypeName;

            sb.AppendLine($"        internal static delegate* unmanaged<{paramsStr}, {returnType}> {function.Name};");
        }

        private void GenerateWrapperClass(ParsedClass parsedClass, string gemName, string outputDirectory)
        {
            var sb = new StringBuilder();

            // File header
            AppendFileHeader(sb);

            sb.AppendLine("using System;");
            sb.AppendLine("using Coral.Managed.Interop;");
            sb.AppendLine();
            sb.AppendLine($"namespace {_namespace}.{gemName}");
            sb.AppendLine("{");

            // Class documentation
            if (!string.IsNullOrEmpty(parsedClass.Documentation))
            {
                sb.AppendLine("    /// <summary>");
                sb.AppendLine($"    /// {parsedClass.Documentation}");
                sb.AppendLine("    /// </summary>");
            }

            sb.AppendLine($"    public class {parsedClass.Name}");
            sb.AppendLine("    {");

            // Generate properties
            foreach (var property in parsedClass.Properties)
            {
                GenerateProperty(sb, parsedClass, property);
            }

            if (parsedClass.Properties.Count > 0 && parsedClass.Methods.Count > 0)
            {
                sb.AppendLine();
            }

            // Generate methods
            foreach (var method in parsedClass.Methods)
            {
                GenerateMethod(sb, parsedClass, method);
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            var outputPath = Path.Combine(outputDirectory, $"{parsedClass.Name}.g.cs");
            File.WriteAllText(outputPath, sb.ToString());
            Log($"  Generated: {outputPath}");
        }

        private void GenerateProperty(StringBuilder sb, ParsedClass parsedClass, ParsedProperty property)
        {
            if (!string.IsNullOrEmpty(property.Documentation))
            {
                sb.AppendLine("        /// <summary>");
                sb.AppendLine($"        /// {property.Documentation}");
                sb.AppendLine("        /// </summary>");
            }

            // For now, generate simple placeholder properties
            // In a full implementation, these would call getter/setter internal calls
            sb.AppendLine($"        public {property.Type.CSharpTypeName} {property.Name} {{ get; set; }}");
            sb.AppendLine();
        }

        private void GenerateMethod(StringBuilder sb, ParsedClass parsedClass, ParsedMethod method)
        {
            if (!string.IsNullOrEmpty(method.Documentation))
            {
                sb.AppendLine("        /// <summary>");
                sb.AppendLine($"        /// {method.Documentation}");
                sb.AppendLine("        /// </summary>");
            }

            // Method signature
            var staticModifier = method.IsStatic ? "static " : "";
            var paramList = string.Join(", ", method.Parameters.Select(p => $"{p.Type.CSharpTypeName} {p.Name}"));
            sb.AppendLine($"        public {staticModifier}unsafe {method.ReturnType.CSharpTypeName} {method.Name}({paramList})");
            sb.AppendLine("        {");

            // Method body - call the internal call
            var args = new List<string>();
            if (!method.IsStatic)
            {
                args.Add("/* this pointer */");
            }
            args.AddRange(method.Parameters.Select(p => p.Name));

            var argsStr = string.Join(", ", args);

            if (method.ReturnType.CSharpTypeName == "void")
            {
                sb.AppendLine($"            InternalCalls.{parsedClass.Name}_{method.Name}({argsStr});");
            }
            else
            {
                sb.AppendLine($"            return InternalCalls.{parsedClass.Name}_{method.Name}({argsStr});");
            }

            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private void GenerateEnum(ParsedEnum parsedEnum, string gemName, string outputDirectory)
        {
            var sb = new StringBuilder();

            // File header
            AppendFileHeader(sb);

            sb.AppendLine("using System;");
            sb.AppendLine();
            sb.AppendLine($"namespace {_namespace}.{gemName}");
            sb.AppendLine("{");

            // Enum documentation
            if (!string.IsNullOrEmpty(parsedEnum.Documentation))
            {
                sb.AppendLine("    /// <summary>");
                sb.AppendLine($"    /// {parsedEnum.Documentation}");
                sb.AppendLine("    /// </summary>");
            }

            sb.AppendLine($"    public enum {parsedEnum.Name}");
            sb.AppendLine("    {");

            // Generate enum values
            for (int i = 0; i < parsedEnum.Values.Count; i++)
            {
                var value = parsedEnum.Values[i];

                if (!string.IsNullOrEmpty(value.Documentation))
                {
                    sb.AppendLine("        /// <summary>");
                    sb.AppendLine($"        /// {value.Documentation}");
                    sb.AppendLine("        /// </summary>");
                }

                var valueSuffix = value.Value.HasValue ? $" = {value.Value.Value}" : "";
                var comma = i < parsedEnum.Values.Count - 1 ? "," : "";
                sb.AppendLine($"        {value.Name}{valueSuffix}{comma}");
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            var outputPath = Path.Combine(outputDirectory, $"{parsedEnum.Name}.g.cs");
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
                Console.WriteLine($"[CSharpGen] {message}");
            }
        }
    }
}
