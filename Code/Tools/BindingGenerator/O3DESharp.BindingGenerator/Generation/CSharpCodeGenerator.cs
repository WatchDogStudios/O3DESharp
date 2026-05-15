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

            // Deduplicate classes that map to the same sanitized name (e.g., same class
            // name in different C++ namespaces). Keep only the first occurrence since all
            // would produce the same InternalCalls field names and wrapper class file.
            validClasses = DeduplicateByName(validClasses, c => SanitizeIdentifier(c.Name));

            // Deduplicate standalone functions with the same sanitized name
            validFunctions = DeduplicateByName(validFunctions, f => SanitizeIdentifier(f.Name));

            // Generate InternalCalls file
            GenerateInternalCalls(validClasses, validFunctions, safeGemName, outputDirectory);

            // Generate wrapper classes. Build a set of class names being emitted
            // in this gem so each generated class can reference its C++ base
            // class as a C# base when that base is also being wrapped.
            var emittedClassNames = new HashSet<string>(
                validClasses.Select(c => SanitizeIdentifier(c.Name)),
                StringComparer.Ordinal);
            foreach (var parsedClass in validClasses)
            {
                GenerateWrapperClass(parsedClass, safeGemName, outputDirectory, emittedClassNames);
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

            // Track all emitted field names to prevent any duplicates in InternalCalls
            var emittedFieldNames = new HashSet<string>(StringComparer.Ordinal);

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
                    var fieldName = InternalCallFieldName(safeClassName, method, validMethods);
                    if (!emittedFieldNames.Add(fieldName))
                        continue; // Defensive against same-class same-signature duplicates from libclang.
                    GenerateInternalCallDeclaration(sb, safeClassName, method, validMethods);
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
                    var fieldName = SanitizeIdentifier(function.Name);
                    if (!emittedFieldNames.Add(fieldName))
                        continue; // Skip duplicate field names
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

        private void GenerateInternalCallDeclaration(StringBuilder sb, string className, ParsedMethod method, IReadOnlyCollection<ParsedMethod> allMethodsInClass)
        {
            var fieldName = InternalCallFieldName(className, method, allMethodsInClass);
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

            sb.AppendLine($"        internal static delegate* unmanaged<{typesStr}> {fieldName};");
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

        private void GenerateWrapperClass(ParsedClass parsedClass, string gemName, string outputDirectory, IReadOnlyCollection<string> emittedClassNames)
        {
            var safeClassName = SanitizeIdentifier(parsedClass.Name);
            var validMethods = GetValidMethods(parsedClass);
            var validProperties = GetValidProperties(parsedClass);

            // Skip if nothing to generate
            if (validMethods.Count == 0 && validProperties.Count == 0)
                return;

            // Resolve the C++ base class to a C# base name IF we're also
            // generating a wrapper for it in this gem. We don't want to point
            // at a base wrapper that doesn't exist (would fail to compile);
            // we also can't point across gem boundaries here because the
            // generated namespace differs and resolving that is a follow-up.
            string? safeBaseClassName = null;
            if (!string.IsNullOrEmpty(parsedClass.BaseClass))
            {
                var candidate = SanitizeIdentifier(parsedClass.BaseClass!);
                if (candidate != safeClassName && emittedClassNames.Contains(candidate))
                {
                    safeBaseClassName = candidate;
                }
            }

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

            // Inheritance: if we resolved a base wrapper above, use it. The
            // base already implements IDisposable and owns the native handle,
            // so derived classes neither redeclare _nativeHandle / Dispose nor
            // re-implement IDisposable.
            if (safeBaseClassName != null)
            {
                sb.AppendLine($"    public class {safeClassName} : {safeBaseClassName}");
                sb.AppendLine("    {");

                if (hasInstanceMembers)
                {
                    sb.AppendLine("        /// <summary>");
                    sb.AppendLine($"        /// Wraps an existing native C++ {parsedClass.Name} pointer.");
                    sb.AppendLine("        /// </summary>");
                    sb.AppendLine($"        public {safeClassName}(IntPtr nativeHandle) : base(nativeHandle)");
                    sb.AppendLine("        {");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                }
            }
            else
            {
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

            // Generate methods. Pass the whole method list so each overload
            // can compute the same InternalCalls field name that GenerateInternalCalls
            // emitted for it.
            foreach (var method in validMethods)
            {
                GenerateMethod(sb, safeClassName, method, validMethods);
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

        private void GenerateMethod(StringBuilder sb, string className, ParsedMethod method, IReadOnlyCollection<ParsedMethod> allMethodsInClass)
        {
            var safeMethodName = SanitizeIdentifier(method.Name);
            var internalCallName = InternalCallFieldName(className, method, allMethodsInClass);

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

            // Re-derive safe parameter names for args. For ref/in parameters
            // to blittable types we pass the address as IntPtr - the wrapper
            // method is already marked unsafe, and the InternalCalls field's
            // delegate signature is "IntPtr" for any reference (see
            // GetMarshalType). C# Unsafe.AsPointer is the standard way to
            // get a stable address out of a ref / readonly ref.
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

                bool isByRefBlittable = param.Type.IsReference && TypeMapper.IsBlittableType(param.Type.CSharpTypeName);
                if (isByRefBlittable)
                {
                    if (param.Type.IsConst)
                    {
                        // 'in T' - need Unsafe.AsRef to drop the readonly so
                        // we can grab the address. Safe because the unmanaged
                        // signature receives it as IntPtr; the C++ side sees
                        // it as 'const T*' which it does not write through.
                        args.Add($"(IntPtr)System.Runtime.CompilerServices.Unsafe.AsPointer(ref System.Runtime.CompilerServices.Unsafe.AsRef(in {safePName}))");
                    }
                    else
                    {
                        args.Add($"(IntPtr)System.Runtime.CompilerServices.Unsafe.AsPointer(ref {safePName})");
                    }
                }
                else
                {
                    args.Add(safePName);
                }
            }

            var argsStr = string.Join(", ", args);

            if (csReturnType == "void")
            {
                sb.AppendLine($"            InternalCalls.{internalCallName}({argsStr});");
            }
            else
            {
                sb.AppendLine($"            return InternalCalls.{internalCallName}({argsStr});");
            }

            sb.AppendLine("        }");
            sb.AppendLine();
        }

        /// <summary>
        /// Format a parameter with safe name and optional default value.
        /// Honors C++ by-reference semantics for blittable types:
        ///   const T&  -> "in T"
        ///   T&        -> "ref T"
        ///   T (value) -> "T"
        /// Non-blittable references already collapsed to IntPtr in TypeMapper,
        /// so they go through the default branch unchanged. Default values
        /// can only attach to value parameters in C#; we suppress them for
        /// ref/in parameters.
        /// </summary>
        private string FormatParameter(ParsedParameter param, string safeName)
        {
            var csType = param.Type.CSharpTypeName;
            string prefix = "";
            bool isByRefBlittable = param.Type.IsReference && TypeMapper.IsBlittableType(csType);
            if (isByRefBlittable)
            {
                prefix = param.Type.IsConst ? "in " : "ref ";
            }
            var result = $"{prefix}{csType} {safeName}";

            if (param.DefaultValue != null && !isByRefBlittable)
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
        /// Get the appropriate unmanaged interop type for a ParsedType. This
        /// is the type used in the InternalCalls delegate* unmanaged<>
        /// signature - what the native side actually sees on the stack.
        ///
        /// Any reference (blittable or not) becomes IntPtr: ABI-wise C++
        /// passes a T* under the hood, and the C# wrapper hands over
        /// (IntPtr)&arg for blittable refs / a marshaled pointer for the
        /// rest. Without this rule, "AZ::Vector3&" would marshal as
        /// "Vector3" by value, silently losing by-reference semantics.
        /// </summary>
        private static string GetMarshalType(ParsedType type)
        {
            var csType = type.CSharpTypeName;

            // Pointers and any kind of reference become IntPtr at the ABI.
            if (type.IsPointer || type.IsReference || csType == "IntPtr")
                return "IntPtr";

            // String → IntPtr in unmanaged calling convention.
            if (csType == "string")
                return "IntPtr";

            // Value types stay as-is.
            return csType;
        }

        /// <summary>
        /// Get the valid (bindable) methods from a parsed class. Every overload
        /// is preserved; <see cref="InternalCallFieldName"/> is responsible for
        /// disambiguating them when emitting field / function names so the
        /// InternalCalls layer can have multiple entries for the same C# name.
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
        /// Compute the unique InternalCalls field name and BindingRegistration
        /// symbol for a method on a class. When a method's sanitized name is
        /// unique within its class the name is just <c>{ClassName}_{MethodName}</c>;
        /// when it collides (i.e. the method is overloaded) every overload gets
        /// a stable suffix derived from its arity and parameter types so each
        /// overload has its own delegate* field and AddInternalCall entry.
        ///
        /// Both <see cref="CSharpCodeGenerator"/> and
        /// <see cref="CppRegistrationGenerator"/> call this so the C# and C++
        /// sides see the same names.
        /// </summary>
        public static string InternalCallFieldName(string className, ParsedMethod method, IReadOnlyCollection<ParsedMethod> allMethodsInClass)
        {
            var safeName = SanitizeIdentifier(method.Name);
            int sameNameCount = 0;
            foreach (var m in allMethodsInClass)
            {
                if (SanitizeIdentifier(m.Name) == safeName)
                {
                    sameNameCount++;
                    if (sameNameCount > 1) break;
                }
            }

            if (sameNameCount <= 1)
            {
                // Unique within the class - no need to mangle.
                return $"{className}_{safeName}";
            }

            // Build a stable parameter-type mangle. Includes arity so that
            // even parameterless overloads coexist (e.g. Foo() vs Foo(int)).
            var sb = new StringBuilder();
            sb.Append(className).Append('_').Append(safeName).Append('_').Append(method.Parameters.Count);
            foreach (var p in method.Parameters)
            {
                sb.Append('_').Append(MangleType(p.Type.CSharpTypeName));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Turn a C# type name into a token safe to embed in an identifier.
        /// We can't just use the raw type because it may contain dots, generics,
        /// or other characters illegal in a C++/C# identifier.
        /// </summary>
        private static string MangleType(string csharpType)
        {
            if (string.IsNullOrEmpty(csharpType))
            {
                return "void";
            }

            var sb = new StringBuilder(csharpType.Length);
            foreach (var c in csharpType)
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            return sb.ToString();
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
        /// Deduplicate a list of items by a key selector.
        /// Keeps the first occurrence when multiple items map to the same key.
        /// </summary>
        private static List<T> DeduplicateByName<T>(List<T> items, Func<T, string> keySelector)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<T>();
            foreach (var item in items)
            {
                var key = keySelector(item);
                if (seen.Add(key))
                {
                    result.Add(item);
                }
            }
            return result;
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
            sb.AppendLine("// Generated by O3DESharp.BindingGenerator");
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
