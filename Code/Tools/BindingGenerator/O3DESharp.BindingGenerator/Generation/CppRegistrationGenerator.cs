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
    /// Generates C++ Coral registration code with hot reload support
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
            GenerateHotReloadHeader(bindings, outputDirectory);
        }

        private void GenerateRegistrationFile(ParsedBindings bindings, string outputDirectory)
        {
            var sb = new StringBuilder();

            // File header
            AppendFileHeader(sb);

            sb.AppendLine("#include <Coral/Assembly.hpp>");
            sb.AppendLine("#include <AzCore/Console/ILogger.h>");
            sb.AppendLine($"#include \"{bindings.GemName}_HotReload.g.h\"");
            sb.AppendLine();
            sb.AppendLine($"namespace {bindings.GemName}::Generated");
            sb.AppendLine("{");

            // Forward declarations for binding functions. The function name MUST
            // match the corresponding InternalCalls field name on the C# side -
            // CSharpCodeGenerator.InternalCallFieldName is the single source of
            // truth for that mangling so overloads disambiguate consistently on
            // both sides of the interop. The C++ signature mirrors the
            // delegate* unmanaged<...> shape on the C# side, with each marshal
            // type mapped back to its C++ equivalent (IntPtr -> void*,
            // Vector3 -> InteropVector3, etc.). The implementations live in
            // hand-written code elsewhere; this file only forward-declares
            // and registers.
            foreach (var parsedClass in bindings.Classes)
            {
                foreach (var method in parsedClass.Methods)
                {
                    var funcName = CSharpCodeGenerator.InternalCallFieldName(parsedClass.Name, method, parsedClass.Methods);
                    sb.AppendLine($"    {BuildCppSignature(funcName, method)};");
                }
            }

            foreach (var function in bindings.Functions)
            {
                sb.AppendLine($"    {BuildCppSignature(function.Name, function)};");
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

            // Register each class's methods. Same overload-aware name as the
            // forward-decl block above.
            foreach (var parsedClass in bindings.Classes)
            {
                sb.AppendLine($"        // {parsedClass.Name} bindings");
                foreach (var method in parsedClass.Methods)
                {
                    var funcName = CSharpCodeGenerator.InternalCallFieldName(parsedClass.Name, method, parsedClass.Methods);
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
            sb.AppendLine();

            // Unregister function for hot reload
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Unregister all bindings before assembly unload (for hot reload)");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    void UnregisterBindings(Coral::ManagedAssembly* assembly)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (assembly == nullptr)");
            sb.AppendLine("        {");
            sb.AppendLine("            AZLOG_ERROR(\"UnregisterBindings - Assembly is null\");");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        AZLOG_INFO(\"Unregistering {bindings.GemName} bindings from assembly '%s'\", assembly->GetName().data());");
            sb.AppendLine();
            sb.AppendLine("        // Notify hot reload manager that assembly is being unloaded");
            sb.AppendLine($"        HotReloadCallbacks::OnBeforeUnload(\"{bindings.GemName}\");");
            sb.AppendLine("    }");
            sb.AppendLine();

            // Hot reload entry point
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Perform hot reload of the bindings");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    bool HotReload(Coral::ManagedAssembly* oldAssembly, Coral::ManagedAssembly* newAssembly)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (oldAssembly != nullptr)");
            sb.AppendLine("        {");
            sb.AppendLine("            UnregisterBindings(oldAssembly);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        if (newAssembly != nullptr)");
            sb.AppendLine("        {");
            sb.AppendLine("            RegisterBindings(newAssembly);");
            sb.AppendLine($"            HotReloadCallbacks::OnAfterLoad(\"{bindings.GemName}\");");
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        return false;");
            sb.AppendLine("    }");

            sb.AppendLine("}");

            var outputPath = Path.Combine(outputDirectory, "BindingRegistration.g.cpp");
            File.WriteAllText(outputPath, sb.ToString());
            Log($"  Generated: {outputPath}");
        }

        private void GenerateHotReloadHeader(ParsedBindings bindings, string outputDirectory)
        {
            var sb = new StringBuilder();

            // File header
            AppendFileHeader(sb);

            sb.AppendLine("#pragma once");
            sb.AppendLine();
            sb.AppendLine("#include <AzCore/std/string/string.h>");
            sb.AppendLine("#include <AzCore/std/function/function.h>");
            sb.AppendLine();
            sb.AppendLine($"namespace {bindings.GemName}::Generated");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Hot reload callback interface for C# scripting");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    struct HotReloadCallbacks");
            sb.AppendLine("    {");
            sb.AppendLine("        using BeforeUnloadCallback = AZStd::function<void(const AZStd::string&)>;");
            sb.AppendLine("        using AfterLoadCallback = AZStd::function<void(const AZStd::string&)>;");
            sb.AppendLine();
            sb.AppendLine("        static BeforeUnloadCallback s_beforeUnloadCallback;");
            sb.AppendLine("        static AfterLoadCallback s_afterLoadCallback;");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Set the callback to invoke before assembly unload");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        static void SetBeforeUnloadCallback(BeforeUnloadCallback callback)");
            sb.AppendLine("        {");
            sb.AppendLine("            s_beforeUnloadCallback = AZStd::move(callback);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Set the callback to invoke after assembly load");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        static void SetAfterLoadCallback(AfterLoadCallback callback)");
            sb.AppendLine("        {");
            sb.AppendLine("            s_afterLoadCallback = AZStd::move(callback);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Invoke before unload callback");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        static void OnBeforeUnload(const AZStd::string& gemName)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (s_beforeUnloadCallback)");
            sb.AppendLine("            {");
            sb.AppendLine("                s_beforeUnloadCallback(gemName);");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Invoke after load callback");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        static void OnAfterLoad(const AZStd::string& gemName)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (s_afterLoadCallback)");
            sb.AppendLine("            {");
            sb.AppendLine("                s_afterLoadCallback(gemName);");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    };");
            sb.AppendLine();
            sb.AppendLine("    // Static callback storage");
            sb.AppendLine("    inline HotReloadCallbacks::BeforeUnloadCallback HotReloadCallbacks::s_beforeUnloadCallback;");
            sb.AppendLine("    inline HotReloadCallbacks::AfterLoadCallback HotReloadCallbacks::s_afterLoadCallback;");
            sb.AppendLine("}");

            var outputPath = Path.Combine(outputDirectory, $"{bindings.GemName}_HotReload.g.h");
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
            sb.AppendLine("// Generated by O3DESharp.BindingGenerator");
            sb.AppendLine();
        }

        private void Log(string message)
        {
            if (_verbose)
            {
                Console.WriteLine($"[CppGen] {message}");
            }
        }

        // -------------------------------------------------------------------
        // C++ signature emission
        //
        // Bridges the C# marshal types CSharpCodeGenerator.GetMarshalType
        // produces (the actual types in the InternalCalls delegate* unmanaged
        // signature) to C++ types suitable for a forward declaration. Both
        // ends of the interop must agree on layout, so:
        //   - IntPtr (C#)            -> void* (C++)
        //   - bool/int/short/...     -> matching size-preserving C++ scalar
        //   - Vector3 / Quaternion   -> InteropVector3 / InteropQuaternion
        //                              (defined in ScriptBindings.h)
        // Anything we don't recognise falls back to "void*" plus a /* comment */
        // showing the original type so a human can still see what was intended.
        // -------------------------------------------------------------------

        /// <summary>Public for testing.</summary>
        public static string BuildCppSignature(string funcName, ParsedMethod method)
        {
            var sb = new StringBuilder();
            sb.Append(MapMarshalTypeToCpp(GetMarshalType(method.ReturnType)));
            sb.Append(' ').Append(funcName).Append('(');

            var parts = new List<string>();
            if (!method.IsStatic)
            {
                parts.Add("void* thisPtr");
            }
            for (int i = 0; i < method.Parameters.Count; i++)
            {
                var p = method.Parameters[i];
                var cpp = MapMarshalTypeToCpp(GetMarshalType(p.Type));
                var paramName = string.IsNullOrEmpty(p.Name) ? $"arg{i}" : SanitizeCppIdentifier(p.Name);
                parts.Add($"{cpp} {paramName}");
            }
            sb.Append(string.Join(", ", parts));
            sb.Append(')');
            return sb.ToString();
        }

        /// <summary>Public for testing. ParsedFunction overload (no 'this').</summary>
        public static string BuildCppSignature(string funcName, ParsedFunction function)
        {
            var sb = new StringBuilder();
            sb.Append(MapMarshalTypeToCpp(GetMarshalType(function.ReturnType)));
            sb.Append(' ').Append(funcName).Append('(');

            var parts = new List<string>();
            for (int i = 0; i < function.Parameters.Count; i++)
            {
                var p = function.Parameters[i];
                var cpp = MapMarshalTypeToCpp(GetMarshalType(p.Type));
                var paramName = string.IsNullOrEmpty(p.Name) ? $"arg{i}" : SanitizeCppIdentifier(p.Name);
                parts.Add($"{cpp} {paramName}");
            }
            sb.Append(string.Join(", ", parts));
            sb.Append(')');
            return sb.ToString();
        }

        /// <summary>
        /// Same as <see cref="CSharpCodeGenerator.GetMarshalType"/>, but
        /// re-implemented here rather than exposed to avoid widening the
        /// public surface of the C# code generator. Keep in sync with the
        /// C# side: any change there needs to match here.
        /// </summary>
        private static string GetMarshalType(ParsedType type)
        {
            var csType = type.CSharpTypeName;
            if (type.IsPointer || type.IsReference || csType == "IntPtr")
                return "IntPtr";
            if (csType == "string")
                return "IntPtr";
            return csType;
        }

        private static string MapMarshalTypeToCpp(string marshal)
        {
            return marshal switch
            {
                "void"   => "void",
                "bool"   => "bool",
                "byte"   => "AZ::u8",
                "sbyte"  => "AZ::s8",
                "short"  => "AZ::s16",
                "ushort" => "AZ::u16",
                "int"    => "AZ::s32",
                "uint"   => "AZ::u32",
                "long"   => "AZ::s64",
                "ulong"  => "AZ::u64",
                "float"  => "float",
                "double" => "double",
                "char"   => "char",
                "nint"   => "AZ::s64",
                "nuint"  => "AZ::u64",
                "IntPtr" => "void*",
                "Vector2" => "O3DESharp::InteropVector2",
                "Vector3" => "O3DESharp::InteropVector3",
                "Quaternion" => "O3DESharp::InteropQuaternion",
                _ => $"void* /* {marshal} */",
            };
        }

        private static string SanitizeCppIdentifier(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "arg";
            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (i == 0)
                {
                    sb.Append(char.IsLetter(c) || c == '_' ? c : '_');
                }
                else
                {
                    sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
                }
            }
            return sb.ToString();
        }
    }
}
