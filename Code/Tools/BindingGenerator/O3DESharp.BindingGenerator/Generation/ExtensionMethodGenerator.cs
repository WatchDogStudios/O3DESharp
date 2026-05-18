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
    /// Generates extension methods for fluent API patterns
    /// </summary>
    public class ExtensionMethodGenerator
    {
        private readonly bool _verbose;
        private readonly string _namespace;

        // Methods that return void and are good candidates for fluent chaining
        private static readonly HashSet<string> FluentCandidatePrefixes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Set", "Add", "Remove", "Enable", "Disable", "Apply", "Update", "Configure",
            "Register", "Unregister", "Attach", "Detach", "Init", "Reset", "Clear"
        };

        // Methods that should be excluded from fluent API
        private static readonly HashSet<string> ExcludedMethods = new(StringComparer.OrdinalIgnoreCase)
        {
            "Dispose", "Finalize", "Clone", "GetHashCode", "ToString", "Equals"
        };

        public ExtensionMethodGenerator(string @namespace, bool verbose = false)
        {
            _namespace = @namespace;
            _verbose = verbose;
        }

        /// <summary>
        /// Generate extension methods for fluent API patterns
        /// </summary>
        /// <param name="bindings">Parsed binding declarations</param>
        /// <param name="outputDirectory">Output directory for generated files</param>
        public void Generate(ParsedBindings bindings, string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);

            Log($"Generating extension methods for gem '{bindings.GemName}' to {outputDirectory}");

            var sb = new StringBuilder();

            // File header
            AppendFileHeader(sb);

            sb.AppendLine("using System;");
            sb.AppendLine();
            sb.AppendLine($"namespace {_namespace}.{bindings.GemName}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Extension methods for fluent API patterns.");
            sb.AppendLine("    /// Allows chaining method calls like: entity.WithPosition(pos).WithRotation(rot).WithScale(scale)");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static class FluentExtensions");
            sb.AppendLine("    {");

            int methodCount = 0;
            foreach (var parsedClass in bindings.Classes)
            {
                var extensionMethods = GenerateClassExtensions(parsedClass);
                if (extensionMethods.Count > 0)
                {
                    sb.AppendLine($"        // ============================================================");
                    sb.AppendLine($"        // {parsedClass.Name} Extensions");
                    sb.AppendLine($"        // ============================================================");
                    sb.AppendLine();

                    foreach (var method in extensionMethods)
                    {
                        sb.Append(method);
                        methodCount++;
                    }
                }
            }

            if (methodCount == 0)
            {
                sb.AppendLine("        // No fluent API extension methods generated for this gem.");
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            var outputPath = Path.Combine(outputDirectory, "FluentExtensions.g.cs");
            File.WriteAllText(outputPath, sb.ToString());
            Log($"  Generated: {outputPath} ({methodCount} extension methods)");
        }

        private List<string> GenerateClassExtensions(ParsedClass parsedClass)
        {
            var extensions = new List<string>();

            // Generate "With" pattern for setters
            foreach (var method in parsedClass.Methods)
            {
                // Skip static methods, excluded methods, and methods with return values
                if (method.IsStatic || ExcludedMethods.Contains(method.Name))
                    continue;

                // Skip methods whose name libclang couldn't extract as a
                // valid C# identifier (anonymous, operator overload, or a
                // metaprogramming-generated decl that came through with an
                // empty/garbled name). Emitting "self.{name}(...)" with an
                // empty name produces CS1001 "Identifier expected".
                if (!IsValidCSharpIdentifier(method.Name))
                    continue;

                // Generate fluent wrapper for void methods starting with Set
                if (method.ReturnType.CSharpTypeName == "void" &&
                    method.Name.StartsWith("Set", StringComparison.OrdinalIgnoreCase))
                {
                    extensions.Add(GenerateWithExtension(parsedClass, method));
                }

                // Generate fluent wrapper for methods that are candidates
                if (method.ReturnType.CSharpTypeName == "void" &&
                    IsFluentCandidate(method.Name))
                {
                    extensions.Add(GenerateFluentExtension(parsedClass, method));
                }
            }

            // Generate builder pattern helpers for properties. EMotionFX's
            // JointSelectionWidget (and a few similar types) had unnamed
            // bitfield / anonymous-member properties slip through libclang's
            // walk - their prop.Name was the empty string. The previous
            // code happily emitted "public static T With(this T self, ...) {
            // self. = value; }" which is a 16-CS1001-error invalid file.
            // Guard the loop AND the per-prop generator below; whichever
            // gem first surfaces a similar pattern won't break the build.
            foreach (var prop in parsedClass.Properties)
            {
                if (prop.IsReadOnly)
                    continue;
                if (!IsValidCSharpIdentifier(prop.Name))
                    continue;
                extensions.Add(GeneratePropertyWithExtension(parsedClass, prop));
            }

            return extensions.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct().ToList();
        }

        /// <summary>
        /// Minimum-viable C# identifier validator: non-empty, starts with
        /// letter or underscore (optionally preceded by an @ verbatim
        /// prefix), and the remaining characters are all letter/digit/
        /// underscore. Mirrors the implementation in CSharpCodeGenerator
        /// rather than reaching across files - the two generators evolve
        /// independently and we don't want to couple them just for a
        /// six-line check.
        /// </summary>
        private static bool IsValidCSharpIdentifier(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            var check = name!.StartsWith("@") ? name.Substring(1) : name;
            if (check.Length == 0 || (!char.IsLetter(check[0]) && check[0] != '_'))
                return false;
            for (int i = 1; i < check.Length; i++)
            {
                if (!char.IsLetterOrDigit(check[i]) && check[i] != '_')
                    return false;
            }
            return true;
        }

        private string GenerateWithExtension(ParsedClass parsedClass, ParsedMethod method)
        {
            // Belt-and-suspenders: if the loop-level filter ever misses a
            // bad name (refactor regression, callers outside
            // GenerateClassExtensions, ...), refuse to emit rather than
            // ship a CS1001-invalid file. method.Name.Substring(3) below
            // would also throw on a 0-2 char name; guard that too.
            if (!IsValidCSharpIdentifier(method.Name) || method.Name.Length <= 3)
                return string.Empty;

            var sb = new StringBuilder();

            // Convert "SetFoo" to "WithFoo"
            var withName = "With" + method.Name.Substring(3);
            var paramList = string.Join(", ", method.Parameters.Select((p, i) => $"{p.Type.CSharpTypeName} {SafeParamName(p.Name, i)}"));
            var argList = string.Join(", ", method.Parameters.Select((p, i) => SafeParamName(p.Name, i)));

            sb.AppendLine($"        /// <summary>");
            sb.AppendLine($"        /// Fluent wrapper for {method.Name}. Calls {method.Name} and returns the instance for chaining.");
            sb.AppendLine($"        /// </summary>");
            sb.AppendLine($"        public static {parsedClass.Name} {withName}(this {parsedClass.Name} self{(paramList.Length > 0 ? ", " + paramList : "")})");
            sb.AppendLine($"        {{");
            sb.AppendLine($"            self.{method.Name}({argList});");
            sb.AppendLine($"            return self;");
            sb.AppendLine($"        }}");
            sb.AppendLine();

            return sb.ToString();
        }

        private string GenerateFluentExtension(ParsedClass parsedClass, ParsedMethod method)
        {
            // Belt-and-suspenders against bad method names slipping past
            // the loop-level filter. Same rationale as GenerateWithExtension.
            if (!IsValidCSharpIdentifier(method.Name))
                return string.Empty;

            // Avoid duplicating if it's already a Set method
            if (method.Name.StartsWith("Set", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            var sb = new StringBuilder();

            var fluentName = method.Name + "Fluent";
            var paramList = string.Join(", ", method.Parameters.Select((p, i) => $"{p.Type.CSharpTypeName} {SafeParamName(p.Name, i)}"));
            var argList = string.Join(", ", method.Parameters.Select((p, i) => SafeParamName(p.Name, i)));

            sb.AppendLine($"        /// <summary>");
            sb.AppendLine($"        /// Fluent wrapper for {method.Name}. Calls {method.Name} and returns the instance for chaining.");
            sb.AppendLine($"        /// </summary>");
            sb.AppendLine($"        public static {parsedClass.Name} {fluentName}(this {parsedClass.Name} self{(paramList.Length > 0 ? ", " + paramList : "")})");
            sb.AppendLine($"        {{");
            sb.AppendLine($"            self.{method.Name}({argList});");
            sb.AppendLine($"            return self;");
            sb.AppendLine($"        }}");
            sb.AppendLine();

            return sb.ToString();
        }

        private string GeneratePropertyWithExtension(ParsedClass parsedClass, ParsedProperty prop)
        {
            // Belt-and-suspenders against bad property names slipping
            // past the loop-level filter. The EMotionFX regression that
            // motivated this fix was JointSelectionWidget producing
            // "self. = value;" because libclang gave us a ParsedProperty
            // with an empty Name (looks like an unnamed bitfield or
            // anonymous member that propagated through unscathed).
            if (!IsValidCSharpIdentifier(prop.Name))
                return string.Empty;

            var sb = new StringBuilder();

            var withName = "With" + prop.Name;

            sb.AppendLine($"        /// <summary>");
            sb.AppendLine($"        /// Fluent setter for {prop.Name}. Sets the property and returns the instance for chaining.");
            sb.AppendLine($"        /// </summary>");
            sb.AppendLine($"        public static {parsedClass.Name} {withName}(this {parsedClass.Name} self, {prop.Type.CSharpTypeName} value)");
            sb.AppendLine($"        {{");
            sb.AppendLine($"            self.{prop.Name} = value;");
            sb.AppendLine($"            return self;");
            sb.AppendLine($"        }}");
            sb.AppendLine();

            return sb.ToString();
        }

        private bool IsFluentCandidate(string methodName)
        {
            foreach (var prefix in FluentCandidatePrefixes)
            {
                if (methodName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

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

        /// <summary>
        /// Sanitize a parameter name: provide fallback for empty names, escape C# keywords,
        /// and strip invalid characters.
        /// </summary>
        private static string SafeParamName(string name, int index)
        {
            if (string.IsNullOrWhiteSpace(name))
                return $"arg{index}";

            // Strip namespaces, template args, invalid characters
            var safe = Regex.Replace(name, @"<[^>]*>", "");
            if (safe.Contains("::"))
            {
                var parts = safe.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries);
                safe = parts.Length > 0 ? parts[parts.Length - 1] : $"arg{index}";
            }
            safe = Regex.Replace(safe, @"[^a-zA-Z0-9_]", "_");
            safe = Regex.Replace(safe, @"_+", "_").Trim('_');

            if (safe.Length == 0 || char.IsDigit(safe[0]))
                safe = "_" + safe;

            if (safe.Length == 0)
                return $"arg{index}";

            // Lowercase first char for parameter naming convention
            if (char.IsUpper(safe[0]))
                safe = char.ToLowerInvariant(safe[0]) + safe.Substring(1);

            // Escape C# keywords
            if (CSharpKeywords.Contains(safe))
                safe = "@" + safe;

            return safe;
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
                Console.WriteLine($"[ExtensionGen] {message}");
            }
        }
    }
}
