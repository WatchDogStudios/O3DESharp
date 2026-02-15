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
using System.Text.RegularExpressions;
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

        /// <summary>
        /// C# reserved keywords that need @ escaping when used as identifiers.
        /// </summary>
        private static readonly HashSet<string> CSharpKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch",
            "char", "checked", "class", "const", "continue", "decimal", "default",
            "delegate", "do", "double", "else", "enum", "event", "explicit",
            "extern", "false", "finally", "fixed", "float", "for", "foreach",
            "goto", "if", "implicit", "in", "int", "interface", "internal",
            "is", "lock", "long", "namespace", "new", "null", "object",
            "operator", "out", "override", "params", "private", "protected",
            "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
            "sizeof", "stackalloc", "static", "string", "struct", "switch",
            "this", "throw", "true", "try", "typeof", "uint", "ulong",
            "unchecked", "unsafe", "ushort", "using", "virtual", "void",
            "volatile", "while",
        };

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

            // Clean stale .g.cs files from previous runs to avoid compilation errors
            CleanStaleFiles(outputDirectory);

            var safeGemName = SanitizeIdentifier(bindings.GemName);

            Log($"Generating C# code for gem '{bindings.GemName}' to {outputDirectory}");

            // Filter classes/functions/enums with valid names before generating
            var validClasses = bindings.Classes
                .Where(c => IsValidCSharpIdentifier(SanitizeIdentifier(c.Name)))
                .ToList();
            var validFunctions = bindings.Functions
                .Where(f => IsValidCSharpIdentifier(SanitizeIdentifier(f.Name)))
                .ToList();
            var validEnums = bindings.Enums
                .Where(e => IsValidCSharpIdentifier(SanitizeIdentifier(e.Name)))
                .ToList();

            // Generate InternalCalls file
            GenerateInternalCalls(validClasses, validFunctions, safeGemName, outputDirectory);

            // Generate wrapper classes
            foreach (var parsedClass in validClasses)
            {
                GenerateWrapperClass(parsedClass, safeGemName, outputDirectory);
            }

            // Generate enums
            foreach (var parsedEnum in validEnums)
            {
                GenerateEnum(parsedEnum, safeGemName, outputDirectory);
            }

            Log($"Generated {validClasses.Count} wrapper classes and {validEnums.Count} enums");
        }

        private void GenerateInternalCalls(List<ParsedClass> classes, List<ParsedFunction> functions, string gemName, string outputDirectory)
        {
            var sb = new StringBuilder();

            // File header
            AppendFileHeader(sb);

            sb.AppendLine("using System;");
            sb.AppendLine("using System.Runtime.InteropServices;");
            sb.AppendLine("using O3DE;");
            sb.AppendLine();
            sb.AppendLine($"namespace {_namespace}.{gemName}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine($"    /// Internal calls to native {gemName} C++ functions.");
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
            foreach (var parsedClass in classes)
            {
                var safeClassName = SanitizeIdentifier(parsedClass.Name);
                var validMethods = GetValidMethods(parsedClass);

                if (validMethods.Count == 0)
                    continue;

                sb.AppendLine($"        // ============================================================");
                sb.AppendLine($"        // {safeClassName} Functions");
                sb.AppendLine($"        // ============================================================");
                sb.AppendLine();

                foreach (var method in validMethods)
                {
                    GenerateInternalCallDeclaration(sb, safeClassName, method);
                }

                sb.AppendLine();
            }

            // Generate function pointers for standalone functions
            if (functions.Count > 0)
            {
                sb.AppendLine($"        // ============================================================");
                sb.AppendLine($"        // Standalone Functions");
                sb.AppendLine($"        // ============================================================");
                sb.AppendLine();

                foreach (var function in functions)
                {
                    GenerateStandaloneFunctionDeclaration(sb, function);
                }
            }

            sb.AppendLine("#pragma warning restore 0649");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            var outputPath = Path.Combine(outputDirectory, "InternalCalls.g.cs");
            File.WriteAllText(outputPath, sb.ToString());
            Log($"  Generated: {outputPath}");
        }

        private void GenerateInternalCallDeclaration(StringBuilder sb, string className, ParsedMethod method)
        {
            var safeMethodName = SanitizeIdentifier(method.Name);
            var marshalTypes = new List<string>();

            // Add 'this' pointer for non-static methods
            if (!method.IsStatic)
            {
                marshalTypes.Add("IntPtr");
            }

            // Add parameter marshal types
            foreach (var param in method.Parameters)
            {
                marshalTypes.Add(GetMarshalType(param.Type));
            }

            var returnMarshalType = GetMarshalType(method.ReturnType);

            // Build delegate signature: delegate* unmanaged<param1, param2, ..., returnType>
            var allTypes = new List<string>(marshalTypes) { returnMarshalType };
            var typesStr = string.Join(", ", allTypes);

            sb.AppendLine($"        internal static delegate* unmanaged<{typesStr}> {className}_{safeMethodName};");
        }

        private void GenerateStandaloneFunctionDeclaration(StringBuilder sb, ParsedFunction function)
        {
            var safeName = SanitizeIdentifier(function.Name);
            var marshalTypes = function.Parameters.Select(p => GetMarshalType(p.Type)).ToList();
            var returnMarshalType = GetMarshalType(function.ReturnType);

            var allTypes = new List<string>(marshalTypes) { returnMarshalType };
            var typesStr = string.Join(", ", allTypes);

            sb.AppendLine($"        internal static delegate* unmanaged<{typesStr}> {safeName};");
        }

        private void GenerateWrapperClass(ParsedClass parsedClass, string gemName, string outputDirectory)
        {
            var safeClassName = SanitizeIdentifier(parsedClass.Name);
            var validMethods = GetValidMethods(parsedClass);
            var validProperties = GetValidProperties(parsedClass);

            // Skip if nothing to generate
            if (validMethods.Count == 0 && validProperties.Count == 0)
                return;

            var sb = new StringBuilder();

            // File header
            AppendFileHeader(sb);

            sb.AppendLine("using System;");
            sb.AppendLine("using System.Runtime.InteropServices;");
            sb.AppendLine("using O3DE;");
            sb.AppendLine();
            sb.AppendLine($"namespace {_namespace}.{gemName}");
            sb.AppendLine("{");

            // Class documentation
            if (!string.IsNullOrEmpty(parsedClass.Documentation))
            {
                sb.AppendLine("    /// <summary>");
                sb.AppendLine($"    /// {EscapeXml(parsedClass.Documentation)}");
                sb.AppendLine("    /// </summary>");
            }

            var hasInstanceMembers = validMethods.Any(m => !m.IsStatic) || validProperties.Count > 0;

            sb.AppendLine($"    public class {safeClassName} : IDisposable");
            sb.AppendLine("    {");

            // Native handle for instance methods
            if (hasInstanceMembers)
            {
                sb.AppendLine("        /// <summary>");
                sb.AppendLine("        /// Pointer to the native C++ object.");
                sb.AppendLine("        /// </summary>");
                sb.AppendLine("        internal IntPtr _nativeHandle;");
                sb.AppendLine();
                sb.AppendLine("        /// <summary>");
                sb.AppendLine($"        /// Wraps an existing native C++ {parsedClass.Name} pointer.");
                sb.AppendLine("        /// </summary>");
                sb.AppendLine($"        public {safeClassName}(IntPtr nativeHandle)");
                sb.AppendLine("        {");
                sb.AppendLine("            _nativeHandle = nativeHandle;");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        /// <inheritdoc/>");
                sb.AppendLine("        public void Dispose()");
                sb.AppendLine("        {");
                sb.AppendLine("            _nativeHandle = IntPtr.Zero;");
                sb.AppendLine("        }");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("        /// <inheritdoc/>");
                sb.AppendLine("        public void Dispose() { }");
                sb.AppendLine();
            }

            // Generate properties
            foreach (var property in validProperties)
            {
                GenerateProperty(sb, property);
            }

            if (validProperties.Count > 0 && validMethods.Count > 0)
            {
                sb.AppendLine();
            }

            // Generate methods
            foreach (var method in validMethods)
            {
                GenerateMethod(sb, safeClassName, method);
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            var safeFileName = SanitizeFileName(safeClassName);
            var outputPath = Path.Combine(outputDirectory, $"{safeFileName}.g.cs");
            File.WriteAllText(outputPath, sb.ToString());
            Log($"  Generated: {outputPath}");
        }

        private void GenerateProperty(StringBuilder sb, ParsedProperty property)
        {
            var safeName = SanitizeIdentifier(property.Name);
            var csType = property.Type.CSharpTypeName;

            if (!string.IsNullOrEmpty(property.Documentation))
            {
                sb.AppendLine("        /// <summary>");
                sb.AppendLine($"        /// {EscapeXml(property.Documentation)}");
                sb.AppendLine("        /// </summary>");
            }

            sb.AppendLine($"        public {csType} {safeName} {{ get; set; }}");
            sb.AppendLine();
        }

        private void GenerateMethod(StringBuilder sb, string className, ParsedMethod method)
        {
            var safeMethodName = SanitizeIdentifier(method.Name);

            if (!string.IsNullOrEmpty(method.Documentation))
            {
                sb.AppendLine("        /// <summary>");
                sb.AppendLine($"        /// {EscapeXml(method.Documentation)}");
                sb.AppendLine("        /// </summary>");

                // Add parameter documentation
                foreach (var param in method.Parameters)
                {
                    var safePName = SanitizeParameterName(param.Name, method.Parameters.IndexOf(param));
                    sb.AppendLine($"        /// <param name=\"{safePName}\">{(param.DefaultValue != null ? $"(default: {EscapeXml(param.DefaultValue)})" : "")}</param>");
                }
            }

            // Build parameter list with safe names
            var paramList = new List<string>();
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < method.Parameters.Count; i++)
            {
                var param = method.Parameters[i];
                var safePName = SanitizeParameterName(param.Name, i);

                // Ensure unique name
                while (usedNames.Contains(safePName))
                {
                    safePName += "_";
                }
                usedNames.Add(safePName);

                paramList.Add(FormatParameter(param, safePName));
            }

            var staticModifier = method.IsStatic ? "static " : "";
            var paramStr = string.Join(", ", paramList);
            var csReturnType = method.ReturnType.CSharpTypeName;

            sb.AppendLine($"        public {staticModifier}unsafe {csReturnType} {safeMethodName}({paramStr})");
            sb.AppendLine("        {");

            // Method body - call the internal call
            var args = new List<string>();
            if (!method.IsStatic)
            {
                args.Add("_nativeHandle");
            }

            // Re-derive safe parameter names for args (must match the parameter list)
            usedNames.Clear();
            for (int i = 0; i < method.Parameters.Count; i++)
            {
                var param = method.Parameters[i];
                var safePName = SanitizeParameterName(param.Name, i);
                while (usedNames.Contains(safePName))
                {
                    safePName += "_";
                }
                usedNames.Add(safePName);
                args.Add(safePName);
            }

            var argsStr = string.Join(", ", args);

            if (csReturnType == "void")
            {
                sb.AppendLine($"            InternalCalls.{className}_{safeMethodName}({argsStr});");
            }
            else
            {
                sb.AppendLine($"            return InternalCalls.{className}_{safeMethodName}({argsStr});");
            }

            sb.AppendLine("        }");
            sb.AppendLine();
        }

        /// <summary>
        /// Format a parameter with safe name and optional default value
        /// </summary>
        private string FormatParameter(ParsedParameter param, string safeName)
        {
            var csType = param.Type.CSharpTypeName;
            var result = $"{csType} {safeName}";

            if (param.DefaultValue != null)
            {
                var defaultValue = ConvertDefaultValue(param.DefaultValue, csType);
                if (defaultValue != null)
                {
                    result += $" = {defaultValue}";
                }
            }

            return result;
        }

        /// <summary>
        /// Convert C++ default value to C# syntax
        /// </summary>
        private static string? ConvertDefaultValue(string cppDefault, string csharpType)
        {
            if (string.IsNullOrWhiteSpace(cppDefault))
                return null;

            var trimmed = cppDefault.Trim();

            // nullptr -> null
            if (trimmed == "nullptr" || trimmed == "NULL")
                return "null";

            // true/false stay the same
            if (trimmed == "true" || trimmed == "false")
                return trimmed;

            // Numeric literals
            if (double.TryParse(trimmed.TrimEnd('f', 'F', 'd', 'D', 'l', 'L', 'u', 'U'), out _))
            {
                var cleanValue = trimmed.TrimEnd('f', 'F', 'd', 'D', 'l', 'L', 'u', 'U');

                if (csharpType == "float")
                    return cleanValue + "f";
                if (csharpType == "double")
                    return cleanValue + "d";
                if (csharpType == "long" || csharpType == "Int64")
                    return cleanValue + "L";
                if (csharpType == "ulong" || csharpType == "UInt64")
                    return cleanValue + "UL";
                return cleanValue;
            }

            // String literals
            if ((trimmed.StartsWith("\"") && trimmed.EndsWith("\"")) ||
                (trimmed.StartsWith("'") && trimmed.EndsWith("'")))
                return trimmed;

            // Default struct/class (e.g., {} -> default)
            if (trimmed == "{}" || trimmed == "{ }")
                return "default";

            // If we can't safely convert it, omit the default
            return null;
        }

        private void GenerateEnum(ParsedEnum parsedEnum, string gemName, string outputDirectory)
        {
            var safeEnumName = SanitizeIdentifier(parsedEnum.Name);

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
                sb.AppendLine($"    /// {EscapeXml(parsedEnum.Documentation)}");
                sb.AppendLine("    /// </summary>");
            }

            sb.AppendLine($"    public enum {safeEnumName}");
            sb.AppendLine("    {");

            // Generate enum values
            for (int i = 0; i < parsedEnum.Values.Count; i++)
            {
                var value = parsedEnum.Values[i];
                var safeValueName = SanitizeIdentifier(value.Name);

                if (!IsValidCSharpIdentifier(safeValueName))
                    continue;

                if (!string.IsNullOrEmpty(value.Documentation))
                {
                    sb.AppendLine("        /// <summary>");
                    sb.AppendLine($"        /// {EscapeXml(value.Documentation)}");
                    sb.AppendLine("        /// </summary>");
                }

                var valueSuffix = value.Value.HasValue ? $" = {value.Value.Value}" : "";
                var comma = i < parsedEnum.Values.Count - 1 ? "," : "";
                sb.AppendLine($"        {safeValueName}{valueSuffix}{comma}");
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            var safeFileName = SanitizeFileName(safeEnumName);
            var outputPath = Path.Combine(outputDirectory, $"{safeFileName}.g.cs");
            File.WriteAllText(outputPath, sb.ToString());
            Log($"  Generated: {outputPath}");
        }

        // ============================================================
        // Name Sanitization Utilities
        // ============================================================

        /// <summary>
        /// Sanitize a C++ name to be a valid C# identifier.
        /// Strips namespaces, template args, and replaces invalid characters.
        /// </summary>
        private static string SanitizeIdentifier(string cppName)
        {
            if (string.IsNullOrEmpty(cppName))
                return "_Unknown";

            // Strip template arguments <...>
            var name = Regex.Replace(cppName, @"<[^>]*>", "");

            // Take the last part if namespace-qualified (AZ::Render::Foo -> Foo)
            if (name.Contains("::"))
            {
                var parts = name.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries);
                name = parts.Length > 0 ? parts[parts.Length - 1] : "_Unknown";
            }

            // Replace invalid characters with underscore
            name = Regex.Replace(name, @"[^a-zA-Z0-9_]", "_");

            // Collapse repeated underscores
            name = Regex.Replace(name, @"_+", "_").Trim('_');

            // Ensure starts with letter or underscore
            if (name.Length == 0 || char.IsDigit(name[0]))
            {
                name = "_" + name;
            }

            // Escape C# keywords
            if (CSharpKeywords.Contains(name))
            {
                name = "@" + name;
            }

            return name;
        }

        /// <summary>
        /// Sanitize a parameter name, providing a fallback if empty.
        /// </summary>
        private static string SanitizeParameterName(string name, int index)
        {
            if (string.IsNullOrWhiteSpace(name))
                return $"arg{index}";

            var safe = SanitizeIdentifier(name);

            // Avoid conflict with type names by making lowercase first char
            if (safe.Length > 0 && char.IsUpper(safe[0]) && !safe.StartsWith("@"))
            {
                safe = char.ToLowerInvariant(safe[0]) + safe.Substring(1);
            }

            return safe;
        }

        /// <summary>
        /// Check if a string is a valid C# identifier.
        /// </summary>
        private static bool IsValidCSharpIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            var check = name.StartsWith("@") ? name.Substring(1) : name;

            if (check.Length == 0 || (!char.IsLetter(check[0]) && check[0] != '_'))
                return false;

            return check.All(c => char.IsLetterOrDigit(c) || c == '_');
        }

        /// <summary>
        /// Sanitize a name for use as a filename.
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            // Remove @ prefix if present
            var clean = name.StartsWith("@") ? name.Substring(1) : name;
            // Remove any remaining characters that are invalid in filenames
            clean = Regex.Replace(clean, @"[<>:""/\\|?*]", "_");
            // Limit length
            if (clean.Length > 120)
                clean = clean.Substring(0, 120);
            return clean;
        }

        /// <summary>
        /// Get the appropriate unmanaged interop type for a ParsedType.
        /// </summary>
        private static string GetMarshalType(ParsedType type)
        {
            var csType = type.CSharpTypeName;

            // Types that need special interop marshaling
            if (type.IsPointer || csType == "IntPtr")
                return "IntPtr";

            // Reference types passed as pointers in unmanaged code
            if (type.IsReference && type.RequiresMarshaling)
                return "IntPtr";

            // String → IntPtr in unmanaged calling convention
            if (csType == "string")
                return "IntPtr";

            // Value types stay as-is
            return csType;
        }

        /// <summary>
        /// Get the valid (bindable) methods from a parsed class.
        /// </summary>
        private static List<ParsedMethod> GetValidMethods(ParsedClass parsedClass)
        {
            return parsedClass.Methods
                .Where(m => IsValidCSharpIdentifier(SanitizeIdentifier(m.Name)))
                .Where(m => IsValidCSharpType(m.ReturnType.CSharpTypeName))
                .Where(m => m.Parameters.All(p => IsValidCSharpType(p.Type.CSharpTypeName)))
                .ToList();
        }

        /// <summary>
        /// Get the valid (bindable) properties from a parsed class.
        /// </summary>
        private static List<ParsedProperty> GetValidProperties(ParsedClass parsedClass)
        {
            return parsedClass.Properties
                .Where(p => IsValidCSharpIdentifier(SanitizeIdentifier(p.Name)))
                .Where(p => IsValidCSharpType(p.Type.CSharpTypeName))
                .ToList();
        }

        /// <summary>
        /// Check if a C# type name is valid (not a leaked C++ type).
        /// </summary>
        private static bool IsValidCSharpType(string csType)
        {
            if (string.IsNullOrEmpty(csType))
                return false;

            // Reject C++ syntax that leaked through type mapping
            if (csType.Contains("::") || csType.Contains("<") || csType.Contains(">"))
                return false;

            // Reject known un-mappable C++ types
            var invalid = new HashSet<string>(StringComparer.Ordinal)
            {
                "align_val_t", "nothrow_t", "type_info",
                "va_list", "__va_list_tag", "size_type",
                "NativeString", "Bool32", // Coral types not available in generated code
            };
            if (invalid.Contains(csType))
                return false;

            return true;
        }

        /// <summary>
        /// Clean stale generated files from the output directory.
        /// This prevents compilation errors from leftover files of previous runs.
        /// </summary>
        private void CleanStaleFiles(string outputDirectory)
        {
            if (!Directory.Exists(outputDirectory))
                return;

            foreach (var file in Directory.GetFiles(outputDirectory, "*.g.cs"))
            {
                try
                {
                    File.Delete(file);
                    Log($"  Cleaned stale file: {Path.GetFileName(file)}");
                }
                catch
                {
                    // Ignore deletion failures
                }
            }
        }

        /// <summary>
        /// Escape text for use in XML documentation comments.
        /// </summary>
        private static string EscapeXml(string text)
        {
            return text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
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
