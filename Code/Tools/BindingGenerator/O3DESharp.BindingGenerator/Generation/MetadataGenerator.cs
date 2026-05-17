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
    /// Generates runtime reflection metadata for hot reload and dynamic binding
    /// </summary>
    public class MetadataGenerator
    {
        private readonly bool _verbose;

        public MetadataGenerator(bool verbose = false)
        {
            _verbose = verbose;
        }

        /// <summary>
        /// Convert an absolute header path into a portable form that can be
        /// embedded in the committed Metadata.g.cs without leaking the
        /// generator-machine's directory layout.
        ///
        /// Strategy: find the canonical anchor segment ("Gems/<gem>" or
        /// "Code/") in the path and strip everything before it, then
        /// normalize separators to forward slashes. Result is something like
        /// "Gems/O3DESharp/Code/Include/O3DESharp/O3DESharpBus.h" - portable
        /// across machines and idiomatic in cross-platform tooling.
        ///
        /// Falls back to Path.GetFileName(absolutePath) if no anchor is
        /// found, so the metadata still has a useful identifier even for
        /// out-of-tree headers.
        /// </summary>
        public static string NormalizeSourceFile(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath))
            {
                return string.Empty;
            }

            // Forward slashes everywhere so the result is identical on
            // Windows and Linux. Replace also collapses double-backslashes
            // that show up in escaped JSON / C# string literals.
            string normalized = absolutePath.Replace('\\', '/');

            // Anchors we try in order. "Gems/<X>/" lands the typical engine
            // gem layout. "Code/" catches the framework path
            // (Code/Framework/...). "Source/" / "Include/" are the gem-
            // internal fallbacks for paths that already start at the gem root.
            string[] anchors = new[] { "/Gems/", "/Code/", "/Source/", "/Include/" };
            foreach (string anchor in anchors)
            {
                int idx = normalized.IndexOf(anchor, System.StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    // Drop the leading '/' so the trimmed path is relative,
                    // not absolute Unix-style.
                    return normalized.Substring(idx + 1);
                }
            }

            // No anchor matched; bail out to just the filename. Better than
            // leaking a maintainer-specific absolute path.
            return System.IO.Path.GetFileName(normalized);
        }

        /// <summary>
        /// Generate C# metadata file from parsed bindings
        /// </summary>
        /// <param name="bindings">Parsed binding declarations</param>
        /// <param name="outputDirectory">Output directory for generated files</param>
        /// <param name="namespace">Root namespace</param>
        public void Generate(ParsedBindings bindings, string outputDirectory, string @namespace)
        {
            Directory.CreateDirectory(outputDirectory);

            Log($"Generating metadata for gem '{bindings.GemName}' to {outputDirectory}");

            GenerateMetadataClass(bindings, outputDirectory, @namespace);
            GenerateMetadataJson(bindings, outputDirectory);
        }

        private void GenerateMetadataClass(ParsedBindings bindings, string outputDirectory, string @namespace)
        {
            var sb = new StringBuilder();

            // File header
            AppendFileHeader(sb);

            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Reflection;");
            sb.AppendLine();
            sb.AppendLine($"namespace {@namespace}.{bindings.GemName}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Runtime reflection metadata for generated bindings.");
            sb.AppendLine("    /// Used for hot reload and dynamic binding resolution.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static class BindingMetadata");
            sb.AppendLine("    {");
            sb.AppendLine($"        /// <summary>Gem name</summary>");
            sb.AppendLine($"        public const string GemName = \"{bindings.GemName}\";");
            sb.AppendLine();
            // GeneratedAt intentionally omitted: embedding wall-clock time breaks
            // content-addressed caches and repro builds. The cache file
            // (.binding_cache.json) records the actual generation time per gem.
            sb.AppendLine();
            sb.AppendLine($"        /// <summary>Binding generator version</summary>");
            sb.AppendLine($"        public const string GeneratorVersion = \"1.0.0\";");
            sb.AppendLine();

            // Class metadata
            sb.AppendLine("        /// <summary>Metadata for all exported classes</summary>");
            sb.AppendLine("        public static readonly ClassMetadata[] Classes = new ClassMetadata[]");
            sb.AppendLine("        {");
            
            foreach (var cls in bindings.Classes)
            {
                sb.AppendLine($"            new ClassMetadata");
                sb.AppendLine($"            {{");
                sb.AppendLine($"                Name = \"{cls.Name}\",");
                sb.AppendLine($"                QualifiedName = \"{cls.QualifiedName}\",");
                sb.AppendLine($"                Namespace = \"{cls.Namespace}\",");
                sb.AppendLine($"                BaseClass = {(cls.BaseClass != null ? $"\"{cls.BaseClass}\"" : "null")},");
                // SourceFile is normalized so the committed Metadata.g.cs
                // never embeds the maintainer-specific absolute path (e.g.
                // "F:\\o3de\\Gems\\..."). Defect #1 from the 2026-05-15
                // audit - "Repo won't build for anyone else" when generated
                // metadata leaks paths into source control.
                sb.AppendLine($"                SourceFile = \"{EscapeString(NormalizeSourceFile(cls.SourceFile))}\",");
                sb.AppendLine($"                Methods = new MethodMetadata[]");
                sb.AppendLine($"                {{");
                
                foreach (var method in cls.Methods)
                {
                    sb.AppendLine($"                    new MethodMetadata");
                    sb.AppendLine($"                    {{");
                    sb.AppendLine($"                        Name = \"{method.Name}\",");
                    sb.AppendLine($"                        ReturnType = \"{method.ReturnType.CSharpTypeName}\",");
                    sb.AppendLine($"                        IsStatic = {method.IsStatic.ToString().ToLower()},");
                    sb.AppendLine($"                        IsConst = {method.IsConst.ToString().ToLower()},");
                    sb.AppendLine($"                        Parameters = new ParameterMetadata[]");
                    sb.AppendLine($"                        {{");
                    
                    foreach (var param in method.Parameters)
                    {
                        sb.AppendLine($"                            new ParameterMetadata {{ Name = \"{param.Name}\", Type = \"{param.Type.CSharpTypeName}\", DefaultValue = {(param.DefaultValue != null ? $"\"{EscapeString(param.DefaultValue)}\"" : "null")} }},");
                    }
                    
                    sb.AppendLine($"                        }}");
                    sb.AppendLine($"                    }},");
                }
                
                sb.AppendLine($"                }},");
                sb.AppendLine($"                Properties = new PropertyMetadata[]");
                sb.AppendLine($"                {{");
                
                foreach (var prop in cls.Properties)
                {
                    sb.AppendLine($"                    new PropertyMetadata {{ Name = \"{prop.Name}\", Type = \"{prop.Type.CSharpTypeName}\", IsReadOnly = {prop.IsReadOnly.ToString().ToLower()} }},");
                }
                
                sb.AppendLine($"                }}");
                sb.AppendLine($"            }},");
            }
            
            sb.AppendLine("        };");
            sb.AppendLine();

            // Enum metadata
            sb.AppendLine("        /// <summary>Metadata for all exported enums</summary>");
            sb.AppendLine("        public static readonly EnumMetadata[] Enums = new EnumMetadata[]");
            sb.AppendLine("        {");
            
            foreach (var en in bindings.Enums)
            {
                sb.AppendLine($"            new EnumMetadata");
                sb.AppendLine($"            {{");
                sb.AppendLine($"                Name = \"{en.Name}\",");
                sb.AppendLine($"                QualifiedName = \"{en.QualifiedName}\",");
                sb.AppendLine($"                UnderlyingType = \"{en.UnderlyingType}\",");
                sb.AppendLine($"                Values = new EnumValueMetadata[]");
                sb.AppendLine($"                {{");
                
                foreach (var val in en.Values)
                {
                    sb.AppendLine($"                    new EnumValueMetadata {{ Name = \"{val.Name}\", Value = {val.Value?.ToString() ?? "null"} }},");
                }
                
                sb.AppendLine($"                }}");
                sb.AppendLine($"            }},");
            }
            
            sb.AppendLine("        };");
            sb.AppendLine();

            // Function metadata
            sb.AppendLine("        /// <summary>Metadata for all exported standalone functions</summary>");
            sb.AppendLine("        public static readonly FunctionMetadata[] Functions = new FunctionMetadata[]");
            sb.AppendLine("        {");
            
            foreach (var func in bindings.Functions)
            {
                sb.AppendLine($"            new FunctionMetadata");
                sb.AppendLine($"            {{");
                sb.AppendLine($"                Name = \"{func.Name}\",");
                sb.AppendLine($"                QualifiedName = \"{func.QualifiedName}\",");
                sb.AppendLine($"                ReturnType = \"{func.ReturnType.CSharpTypeName}\",");
                sb.AppendLine($"                Parameters = new ParameterMetadata[]");
                sb.AppendLine($"                {{");
                
                foreach (var param in func.Parameters)
                {
                    sb.AppendLine($"                    new ParameterMetadata {{ Name = \"{param.Name}\", Type = \"{param.Type.CSharpTypeName}\", DefaultValue = {(param.DefaultValue != null ? $"\"{EscapeString(param.DefaultValue)}\"" : "null")} }},");
                }
                
                sb.AppendLine($"                }}");
                sb.AppendLine($"            }},");
            }
            
            sb.AppendLine("        };");
            sb.AppendLine();

            // Helper method for runtime type lookup
            sb.AppendLine("        /// <summary>Get class metadata by name</summary>");
            sb.AppendLine("        public static ClassMetadata? GetClass(string name) =>");
            sb.AppendLine("            System.Array.Find(Classes, c => c.Name == name);");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>Get enum metadata by name</summary>");
            sb.AppendLine("        public static EnumMetadata? GetEnum(string name) =>");
            sb.AppendLine("            System.Array.Find(Enums, e => e.Name == name);");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>Get function metadata by name</summary>");
            sb.AppendLine("        public static FunctionMetadata? GetFunction(string name) =>");
            sb.AppendLine("            System.Array.Find(Functions, f => f.Name == name);");

            sb.AppendLine("    }");
            sb.AppendLine();

            // Metadata structure definitions
            GenerateMetadataStructures(sb);

            sb.AppendLine("}");

            var outputPath = Path.Combine(outputDirectory, "Metadata.g.cs");
            File.WriteAllText(outputPath, sb.ToString());
            Log($"  Generated: {outputPath}");
        }

        private void GenerateMetadataStructures(StringBuilder sb)
        {
            sb.AppendLine("    /// <summary>Class metadata structure</summary>");
            sb.AppendLine("    public class ClassMetadata");
            sb.AppendLine("    {");
            sb.AppendLine("        public string Name { get; init; } = string.Empty;");
            sb.AppendLine("        public string QualifiedName { get; init; } = string.Empty;");
            sb.AppendLine("        public string Namespace { get; init; } = string.Empty;");
            sb.AppendLine("        public string? BaseClass { get; init; }");
            sb.AppendLine("        public string SourceFile { get; init; } = string.Empty;");
            sb.AppendLine("        public MethodMetadata[] Methods { get; init; } = Array.Empty<MethodMetadata>();");
            sb.AppendLine("        public PropertyMetadata[] Properties { get; init; } = Array.Empty<PropertyMetadata>();");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>Method metadata structure</summary>");
            sb.AppendLine("    public class MethodMetadata");
            sb.AppendLine("    {");
            sb.AppendLine("        public string Name { get; init; } = string.Empty;");
            sb.AppendLine("        public string ReturnType { get; init; } = string.Empty;");
            sb.AppendLine("        public bool IsStatic { get; init; }");
            sb.AppendLine("        public bool IsConst { get; init; }");
            sb.AppendLine("        public ParameterMetadata[] Parameters { get; init; } = Array.Empty<ParameterMetadata>();");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>Property metadata structure</summary>");
            sb.AppendLine("    public class PropertyMetadata");
            sb.AppendLine("    {");
            sb.AppendLine("        public string Name { get; init; } = string.Empty;");
            sb.AppendLine("        public string Type { get; init; } = string.Empty;");
            sb.AppendLine("        public bool IsReadOnly { get; init; }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>Parameter metadata structure</summary>");
            sb.AppendLine("    public class ParameterMetadata");
            sb.AppendLine("    {");
            sb.AppendLine("        public string Name { get; init; } = string.Empty;");
            sb.AppendLine("        public string Type { get; init; } = string.Empty;");
            sb.AppendLine("        public string? DefaultValue { get; init; }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>Enum metadata structure</summary>");
            sb.AppendLine("    public class EnumMetadata");
            sb.AppendLine("    {");
            sb.AppendLine("        public string Name { get; init; } = string.Empty;");
            sb.AppendLine("        public string QualifiedName { get; init; } = string.Empty;");
            sb.AppendLine("        public string UnderlyingType { get; init; } = string.Empty;");
            sb.AppendLine("        public EnumValueMetadata[] Values { get; init; } = Array.Empty<EnumValueMetadata>();");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>Enum value metadata structure</summary>");
            sb.AppendLine("    public class EnumValueMetadata");
            sb.AppendLine("    {");
            sb.AppendLine("        public string Name { get; init; } = string.Empty;");
            sb.AppendLine("        public long? Value { get; init; }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>Function metadata structure</summary>");
            sb.AppendLine("    public class FunctionMetadata");
            sb.AppendLine("    {");
            sb.AppendLine("        public string Name { get; init; } = string.Empty;");
            sb.AppendLine("        public string QualifiedName { get; init; } = string.Empty;");
            sb.AppendLine("        public string ReturnType { get; init; } = string.Empty;");
            sb.AppendLine("        public ParameterMetadata[] Parameters { get; init; } = Array.Empty<ParameterMetadata>();");
            sb.AppendLine("    }");
        }

        private void GenerateMetadataJson(ParsedBindings bindings, string outputDirectory)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"gemName\": \"{bindings.GemName}\",");
            // "generatedAt" deliberately omitted for build determinism.
            // See .binding_cache.json for actual generation timestamps.
            sb.AppendLine($"  \"generatorVersion\": \"1.0.0\",");
            sb.AppendLine($"  \"classCount\": {bindings.Classes.Count},");
            sb.AppendLine($"  \"enumCount\": {bindings.Enums.Count},");
            sb.AppendLine($"  \"functionCount\": {bindings.Functions.Count},");
            sb.AppendLine("  \"classes\": [");
            
            for (int i = 0; i < bindings.Classes.Count; i++)
            {
                var cls = bindings.Classes[i];
                sb.AppendLine($"    {{");
                sb.AppendLine($"      \"name\": \"{cls.Name}\",");
                sb.AppendLine($"      \"qualifiedName\": \"{cls.QualifiedName}\",");
                sb.AppendLine($"      \"methodCount\": {cls.Methods.Count},");
                sb.AppendLine($"      \"propertyCount\": {cls.Properties.Count}");
                sb.Append($"    }}");
                if (i < bindings.Classes.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            
            sb.AppendLine("  ],");
            sb.AppendLine("  \"enums\": [");
            
            for (int i = 0; i < bindings.Enums.Count; i++)
            {
                var en = bindings.Enums[i];
                sb.AppendLine($"    {{");
                sb.AppendLine($"      \"name\": \"{en.Name}\",");
                sb.AppendLine($"      \"valueCount\": {en.Values.Count}");
                sb.Append($"    }}");
                if (i < bindings.Enums.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            
            sb.AppendLine("  ]");
            sb.AppendLine("}");

            var outputPath = Path.Combine(outputDirectory, "metadata.json");
            File.WriteAllText(outputPath, sb.ToString());
            Log($"  Generated: {outputPath}");
        }

        private static string EscapeString(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
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
            sb.AppendLine("// Generated by O3DESharp.BindingGenerator");
            sb.AppendLine();
        }

        private void Log(string message)
        {
            if (_verbose)
            {
                Console.WriteLine($"[MetadataGen] {message}");
            }
        }
    }
}
