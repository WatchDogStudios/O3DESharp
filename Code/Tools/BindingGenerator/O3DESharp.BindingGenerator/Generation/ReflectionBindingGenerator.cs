/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using O3DESharp.BindingGenerator.Configuration;

namespace O3DESharp.BindingGenerator.Generation
{
    /// <summary>
    /// JSON-driven binding generator. Consumes reflection_data.json
    /// (produced at runtime by the C++ ReflectionDataExporter) and
    /// emits C# wrappers that dispatch through O3DE.Core's
    /// NativeReflection at runtime.
    ///
    /// Why this exists alongside ClangSharp: ClangSharp parses C++
    /// headers, which means dealing with MSVC compat, cross-gem
    /// include paths, AzCore preludes, editor-vs-runtime filtering,
    /// and the constant friction of "the header includes X but the
    /// header for X isn't on our search path". The BehaviorContext
    /// JSON path sidesteps all of that:
    ///   - Reflection data is what O3DE gems explicitly chose to
    ///     expose to scripting (Lua / ScriptCanvas / Python all
    ///     consume the same data). It's the canonical public API.
    ///   - Editor-vs-runtime is decided by the gem author: editor-
    ///     only types simply aren't in the runtime BehaviorContext.
    ///   - Cross-gem dependencies are resolved by reflection - if
    ///     a type isn't reflected, no wrapper gets generated for it
    ///     and nothing tries to call it.
    ///   - Generated wrappers are guaranteed to work at runtime:
    ///     every call dispatches through NativeReflection.InvokeMethod
    ///     / BroadcastEBusEvent / GetProperty, which use the same
    ///     BehaviorContext we generated from.
    ///
    /// The single trade-off is that the JSON has to be produced
    /// first - which the gem already does automatically via
    /// AutoExportReflectionData on editor startup.
    ///
    /// Output shape per gem:
    ///   &lt;output&gt;/&lt;GemName&gt;/Classes/&lt;ClassName&gt;.g.cs       - one per reflected class
    ///   &lt;output&gt;/&lt;GemName&gt;/EBuses/&lt;BusName&gt;.g.cs           - one per reflected EBus
    ///   &lt;output&gt;/&lt;GemName&gt;/Globals.g.cs                       - global methods + properties
    /// </summary>
    public class ReflectionBindingGenerator
    {
        private readonly bool _verbose;
        private readonly string _rootNamespace;

        public ReflectionBindingGenerator(string rootNamespace = "O3DE.Generated", bool verbose = false)
        {
            _rootNamespace = rootNamespace;
            _verbose = verbose;
        }

        /// <summary>
        /// Generate C# wrappers from a reflection_data.json file.
        /// </summary>
        /// <param name="jsonPath">Absolute path to reflection_data.json.</param>
        /// <param name="outputDir">Absolute path to the output directory.</param>
        /// <param name="includeGems">If non-empty, only generate wrappers for
        /// types whose source_gem_name is in this set. Empty = all gems.</param>
        /// <returns>Counts of what was generated.</returns>
        public ReflectionGenerationResult Generate(string jsonPath, string outputDir, ISet<string>? includeGems = null)
        {
            if (!File.Exists(jsonPath))
            {
                return new ReflectionGenerationResult
                {
                    Success = false,
                    ErrorMessage = $"Reflection data file not found: {jsonPath}. " +
                                   $"Launch the O3DE editor at least once to trigger AutoExportReflectionData, " +
                                   $"or pass --reflection-data &lt;path&gt; explicitly."
                };
            }

            Console.WriteLine($"[ReflectionGen] Loading {jsonPath}");
            ReflectionDocument doc;
            try
            {
                var json = File.ReadAllText(jsonPath);
                doc = JsonSerializer.Deserialize<ReflectionDocument>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new ReflectionDocument();
            }
            catch (Exception e)
            {
                return new ReflectionGenerationResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to parse {jsonPath}: {e.Message}"
                };
            }

            Console.WriteLine(
                $"[ReflectionGen] Loaded: {doc.Classes.Count} classes, " +
                $"{doc.EBuses.Count} ebuses, {doc.GlobalMethods.Count} global methods, " +
                $"{doc.GlobalProperties.Count} global properties");

            // Group by gem so we can emit a per-gem directory + Globals file.
            // Items with empty SourceGemName get bucketed under "Core" -
            // those are types reflected by AzCore / engine framework code.
            string GemBucket(string sourceGemName) =>
                string.IsNullOrWhiteSpace(sourceGemName) ? "Core" : sourceGemName;

            var classesByGem = doc.Classes
                .Where(c => includeGems == null || includeGems.Count == 0 || includeGems.Contains(GemBucket(c.SourceGemName)))
                .GroupBy(c => GemBucket(c.SourceGemName));

            var busesByGem = doc.EBuses
                .Where(b => includeGems == null || includeGems.Count == 0 || includeGems.Contains(GemBucket(b.SourceGemName)))
                .GroupBy(b => GemBucket(b.SourceGemName));

            // Globals come in two flavors (methods + properties). Group
            // each separately by gem, then union the keys for the emit
            // loop. Avoids the tuple-with-nullable-fields awkwardness.
            var methodsByGem = doc.GlobalMethods
                .Where(m => includeGems == null || includeGems.Count == 0 || includeGems.Contains(GemBucket(m.SourceGemName)))
                .GroupBy(m => GemBucket(m.SourceGemName))
                .ToDictionary(g => g.Key, g => g.ToList());
            var propsByGem = doc.GlobalProperties
                .Where(p => includeGems == null || includeGems.Count == 0 || includeGems.Contains(GemBucket(p.SourceGemName)))
                .GroupBy(p => GemBucket(p.SourceGemName))
                .ToDictionary(g => g.Key, g => g.ToList());
            var globalsGemKeys = new HashSet<string>(methodsByGem.Keys);
            globalsGemKeys.UnionWith(propsByGem.Keys);

            int classFiles = 0, busFiles = 0, globalsFiles = 0;

            foreach (var gemGroup in classesByGem)
            {
                var gemDir = Path.Combine(outputDir, gemGroup.Key, "Classes");
                Directory.CreateDirectory(gemDir);
                int gemClassCount = 0;
                int gemSkippedCount = 0;
                foreach (var cls in gemGroup)
                {
                    // Skip C++ template specializations - they reflect as
                    // names like "AZStd::unordered_map<X, Y, AZStd::hash<X>, ...>"
                    // which sanitize to unreadable identifiers and almost
                    // never map cleanly to a C# wrapper (the underlying
                    // type machinery already routes through marshalers
                    // by TypeId at runtime).
                    if (IsTemplateSpecialization(cls.Name))
                    {
                        gemSkippedCount++;
                        continue;
                    }
                    var cs = EmitClass(cls, gemGroup.Key);
                    var safeName = SafeFileName(cls.Name);
                    File.WriteAllText(Path.Combine(gemDir, safeName + ".g.cs"), cs);
                    classFiles++;
                    gemClassCount++;
                }
                if (_verbose)
                {
                    Console.WriteLine(
                        $"  [{gemGroup.Key}] Wrote {gemClassCount} class wrappers" +
                        (gemSkippedCount > 0 ? $" ({gemSkippedCount} template specializations skipped)" : ""));
                }
            }

            foreach (var gemGroup in busesByGem)
            {
                var gemDir = Path.Combine(outputDir, gemGroup.Key, "EBuses");
                Directory.CreateDirectory(gemDir);
                foreach (var bus in gemGroup)
                {
                    var cs = EmitEBus(bus, gemGroup.Key);
                    var safeName = SafeFileName(bus.Name);
                    File.WriteAllText(Path.Combine(gemDir, safeName + ".g.cs"), cs);
                    busFiles++;
                }
                if (_verbose)
                {
                    Console.WriteLine($"  [{gemGroup.Key}] Wrote {gemGroup.Count()} EBus wrappers");
                }
            }

            foreach (var gemKey in globalsGemKeys)
            {
                var gemDir = Path.Combine(outputDir, gemKey);
                Directory.CreateDirectory(gemDir);
                var methods = methodsByGem.TryGetValue(gemKey, out var mList) ? mList : new List<ReflectionGlobalMethod>();
                var props = propsByGem.TryGetValue(gemKey, out var pList) ? pList : new List<ReflectionGlobalProperty>();
                if (methods.Count == 0 && props.Count == 0) continue;
                var cs = EmitGlobals(methods, props, gemKey);
                File.WriteAllText(Path.Combine(gemDir, "Globals.g.cs"), cs);
                globalsFiles++;
                if (_verbose)
                {
                    Console.WriteLine($"  [{gemKey}] Wrote Globals ({methods.Count} methods, {props.Count} properties)");
                }
            }

            return new ReflectionGenerationResult
            {
                Success = true,
                ClassFilesWritten = classFiles,
                BusFilesWritten = busFiles,
                GlobalsFilesWritten = globalsFiles,
                TotalClasses = doc.Classes.Count,
                TotalEBuses = doc.EBuses.Count,
                TotalGlobals = doc.GlobalMethods.Count + doc.GlobalProperties.Count,
            };
        }

        // -----------------------------------------------------------------
        // Per-entity emitters
        // -----------------------------------------------------------------

        private string EmitClass(ReflectionClass cls, string gemName)
        {
            var sb = new StringBuilder();
            EmitFileHeader(sb, $"Wrappers for AZ class {cls.Name} (gem: {gemName})");
            sb.AppendLine("using O3DE.Core.Reflection;");
            sb.AppendLine();
            sb.AppendLine($"namespace {_rootNamespace}.{SafeNamespaceSegment(gemName)}.Classes");
            sb.AppendLine("{");
            sb.AppendLine($"    /// <summary>{XmlEscape(cls.Description)}</summary>");
            sb.AppendLine($"    /// <remarks>");
            sb.AppendLine($"    /// AZ class: <c>{XmlEscape(cls.Name)}</c><br/>");
            sb.AppendLine($"    /// TypeId: <c>{cls.TypeId}</c><br/>");
            sb.AppendLine($"    /// Category: <c>{XmlEscape(cls.Category)}</c>");
            sb.AppendLine($"    /// </remarks>");
            sb.AppendLine($"    public static class {SafeIdentifier(LastNamespaceSegment(cls.Name))}");
            sb.AppendLine("    {");
            sb.AppendLine($"        public const string TypeName = \"{cls.Name}\";");

            // Methods
            foreach (var m in cls.Methods)
            {
                if (string.IsNullOrEmpty(m.Name)) continue;
                if (IsGeneratorUnsafeName(m.Name)) continue;

                var returnType = MapMarshalToCSharp(m.ReturnType);
                var methodName = SafeIdentifier(m.Name);
                var paramList = string.Join(", ", m.Parameters.Select((p, i) =>
                    $"{MapMarshalToCSharp(new ReflectionTypeInfo { MarshalType = p.MarshalType, TypeName = p.TypeName })} {SafeParamName(p.TypeName, i)}"));
                var argList = string.Join(", ", m.Parameters.Select((p, i) => SafeParamName(p.TypeName, i)));

                sb.AppendLine();
                sb.AppendLine($"        /// <summary>{XmlEscape(m.Description)}</summary>");
                if (m.IsDeprecated)
                {
                    sb.AppendLine($"        [System.Obsolete(\"{XmlEscape(m.DeprecationMessage)}\")]");
                }

                if (m.IsStatic)
                {
                    // Static method - no instance needed; dispatch via InvokeStaticMethod.
                    if (returnType == "void")
                    {
                        sb.AppendLine($"        public static void {methodName}({paramList})");
                        sb.AppendLine("        {");
                        sb.AppendLine($"            NativeReflection.InvokeStaticMethod(TypeName, \"{m.Name}\"{(string.IsNullOrEmpty(argList) ? "" : ", " + argList)});");
                        sb.AppendLine("        }");
                    }
                    else
                    {
                        sb.AppendLine($"        public static {returnType} {methodName}({paramList})");
                        sb.AppendLine("        {");
                        sb.AppendLine($"            var __result = NativeReflection.InvokeStaticMethod(TypeName, \"{m.Name}\"{(string.IsNullOrEmpty(argList) ? "" : ", " + argList)});");
                        sb.AppendLine($"            return ({returnType})(__result ?? default({returnType})!);");
                        sb.AppendLine("        }");
                    }
                }
                else
                {
                    // Instance method - first param is the NativeObject handle.
                    var firstParamSep = string.IsNullOrEmpty(paramList) ? "" : ", ";
                    if (returnType == "void")
                    {
                        sb.AppendLine($"        public static void {methodName}(NativeObject __instance{firstParamSep}{paramList})");
                        sb.AppendLine("        {");
                        sb.AppendLine($"            NativeReflection.InvokeInstanceMethod(__instance, \"{m.Name}\"{(string.IsNullOrEmpty(argList) ? "" : ", " + argList)});");
                        sb.AppendLine("        }");
                    }
                    else
                    {
                        sb.AppendLine($"        public static {returnType} {methodName}(NativeObject __instance{firstParamSep}{paramList})");
                        sb.AppendLine("        {");
                        sb.AppendLine($"            var __result = NativeReflection.InvokeInstanceMethod(__instance, \"{m.Name}\"{(string.IsNullOrEmpty(argList) ? "" : ", " + argList)});");
                        sb.AppendLine($"            return ({returnType})(__result ?? default({returnType})!);");
                        sb.AppendLine("        }");
                    }
                }
            }

            // Properties - emit get/set helpers (skipping set if readonly).
            foreach (var p in cls.Properties)
            {
                if (string.IsNullOrEmpty(p.Name)) continue;
                if (IsGeneratorUnsafeName(p.Name)) continue;

                var propType = MapMarshalToCSharp(p.Type);
                var propName = SafeIdentifier(p.Name);

                sb.AppendLine();
                sb.AppendLine($"        /// <summary>Get {XmlEscape(p.Name)} - {XmlEscape(p.Description)}</summary>");
                sb.AppendLine($"        public static {propType} Get{propName}(NativeObject __instance)");
                sb.AppendLine("        {");
                sb.AppendLine($"            return NativeReflection.GetProperty<{propType}>(__instance, \"{p.Name}\") ?? default({propType})!;");
                sb.AppendLine("        }");

                if (!p.IsReadOnly)
                {
                    sb.AppendLine();
                    sb.AppendLine($"        /// <summary>Set {XmlEscape(p.Name)}</summary>");
                    sb.AppendLine($"        public static void Set{propName}(NativeObject __instance, {propType} value)");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            NativeReflection.SetProperty(__instance, \"{p.Name}\", value!);");
                    sb.AppendLine("        }");
                }
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string EmitEBus(ReflectionEBus bus, string gemName)
        {
            var sb = new StringBuilder();
            EmitFileHeader(sb, $"Wrappers for EBus {bus.Name} (gem: {gemName})");
            sb.AppendLine("using O3DE.Core.Reflection;");
            sb.AppendLine();
            sb.AppendLine($"namespace {_rootNamespace}.{SafeNamespaceSegment(gemName)}.EBuses");
            sb.AppendLine("{");
            sb.AppendLine($"    /// <summary>{XmlEscape(bus.Description)}</summary>");
            sb.AppendLine($"    /// <remarks>");
            sb.AppendLine($"    /// EBus: <c>{XmlEscape(bus.Name)}</c><br/>");
            sb.AppendLine($"    /// Category: <c>{XmlEscape(bus.Category)}</c><br/>");
            sb.AppendLine($"    /// Address type: <c>{XmlEscape(bus.AddressType.TypeName)}</c>");
            sb.AppendLine($"    /// </remarks>");
            sb.AppendLine($"    public static class {SafeIdentifier(bus.Name)}");
            sb.AppendLine("    {");
            sb.AppendLine($"        public const string BusName = \"{bus.Name}\";");

            foreach (var e in bus.Events)
            {
                if (string.IsNullOrEmpty(e.Name)) continue;
                if (IsGeneratorUnsafeName(e.Name)) continue;

                var returnType = MapMarshalToCSharp(e.ReturnType);
                var eventName = SafeIdentifier(e.Name);
                var paramList = string.Join(", ", e.Parameters.Select((p, i) =>
                    $"{MapMarshalToCSharp(new ReflectionTypeInfo { MarshalType = p.MarshalType, TypeName = p.TypeName })} {SafeParamName(p.TypeName, i)}"));
                var argList = string.Join(", ", e.Parameters.Select((p, i) => SafeParamName(p.TypeName, i)));

                sb.AppendLine();
                if (e.IsBroadcast)
                {
                    // Broadcast event - delivered to all handlers.
                    if (returnType == "void")
                    {
                        sb.AppendLine($"        /// <summary>Broadcast {XmlEscape(e.Name)} to all handlers.</summary>");
                        sb.AppendLine($"        public static void {eventName}({paramList})");
                        sb.AppendLine("        {");
                        sb.AppendLine($"            NativeReflection.BroadcastEBusEvent(BusName, \"{e.Name}\"{(string.IsNullOrEmpty(argList) ? "" : ", " + argList)});");
                        sb.AppendLine("        }");
                    }
                    else
                    {
                        sb.AppendLine($"        /// <summary>Broadcast {XmlEscape(e.Name)} to all handlers; returns the first handler's result.</summary>");
                        sb.AppendLine($"        public static {returnType} {eventName}({paramList})");
                        sb.AppendLine("        {");
                        sb.AppendLine($"            var __r = NativeReflection.BroadcastResultEBusEvent<{returnType}>(BusName, \"{e.Name}\"{(string.IsNullOrEmpty(argList) ? "" : ", " + argList)});");
                        sb.AppendLine($"            return __r ?? default({returnType})!;");
                        sb.AppendLine("        }");
                    }
                }
                else
                {
                    // Addressed event - delivered to a specific handler by busId.
                    var addressType = string.IsNullOrEmpty(bus.AddressType.TypeName)
                        ? "ulong"
                        : "ulong"; // we marshal addresses as ulong handles uniformly via the dispatch path
                    var firstParamSep = string.IsNullOrEmpty(paramList) ? "" : ", ";
                    if (returnType == "void")
                    {
                        sb.AppendLine($"        /// <summary>Send {XmlEscape(e.Name)} to the handler addressed by busId.</summary>");
                        sb.AppendLine($"        public static void {eventName}({addressType} busId{firstParamSep}{paramList})");
                        sb.AppendLine("        {");
                        sb.AppendLine($"            NativeReflection.SendEBusEvent(BusName, \"{e.Name}\", busId{(string.IsNullOrEmpty(argList) ? "" : ", " + argList)});");
                        sb.AppendLine("        }");
                    }
                    else
                    {
                        sb.AppendLine($"        /// <summary>Send {XmlEscape(e.Name)} to the handler addressed by busId; returns the handler's result.</summary>");
                        sb.AppendLine($"        public static {returnType} {eventName}({addressType} busId{firstParamSep}{paramList})");
                        sb.AppendLine("        {");
                        sb.AppendLine($"            var __r = NativeReflection.SendResultEBusEvent<{returnType}>(BusName, \"{e.Name}\", busId{(string.IsNullOrEmpty(argList) ? "" : ", " + argList)});");
                        sb.AppendLine($"            return __r ?? default({returnType})!;");
                        sb.AppendLine("        }");
                    }
                }
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string EmitGlobals(List<ReflectionGlobalMethod> methods, List<ReflectionGlobalProperty> props, string gemName)
        {
            var sb = new StringBuilder();
            EmitFileHeader(sb, $"Global methods + properties (gem: {gemName})");
            sb.AppendLine("using O3DE.Core.Reflection;");
            sb.AppendLine();
            sb.AppendLine($"namespace {_rootNamespace}.{SafeNamespaceSegment(gemName)}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>Reflected global functions and properties from this gem.</summary>");
            sb.AppendLine("    public static class Globals");
            sb.AppendLine("    {");

            foreach (var m in methods)
            {
                if (string.IsNullOrEmpty(m.Name) || IsGeneratorUnsafeName(m.Name)) continue;
                var returnType = MapMarshalToCSharp(m.ReturnType);
                var methodName = SafeIdentifier(m.Name);
                var paramList = string.Join(", ", m.Parameters.Select((p, i) =>
                    $"{MapMarshalToCSharp(new ReflectionTypeInfo { MarshalType = p.MarshalType, TypeName = p.TypeName })} {SafeParamName(p.TypeName, i)}"));
                var argList = string.Join(", ", m.Parameters.Select((p, i) => SafeParamName(p.TypeName, i)));

                sb.AppendLine();
                sb.AppendLine($"        /// <summary>{XmlEscape(m.Description)}</summary>");
                if (m.IsDeprecated)
                {
                    sb.AppendLine($"        [System.Obsolete(\"{XmlEscape(m.DeprecationMessage)}\")]");
                }
                if (returnType == "void")
                {
                    sb.AppendLine($"        public static void {methodName}({paramList})");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            NativeReflection.InvokeGlobalMethod(\"{m.Name}\"{(string.IsNullOrEmpty(argList) ? "" : ", " + argList)});");
                    sb.AppendLine("        }");
                }
                else
                {
                    sb.AppendLine($"        public static {returnType} {methodName}({paramList})");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            var __r = NativeReflection.InvokeGlobalMethod(\"{m.Name}\"{(string.IsNullOrEmpty(argList) ? "" : ", " + argList)});");
                    sb.AppendLine($"            return ({returnType})(__r ?? default({returnType})!);");
                    sb.AppendLine("        }");
                }
            }

            foreach (var p in props)
            {
                if (string.IsNullOrEmpty(p.Name) || IsGeneratorUnsafeName(p.Name)) continue;
                var propType = MapMarshalToCSharp(p.Type);
                var propName = SafeIdentifier(p.Name);

                sb.AppendLine();
                sb.AppendLine($"        /// <summary>{XmlEscape(p.Description)}</summary>");
                sb.AppendLine($"        public static {propType} {propName}");
                sb.AppendLine("        {");
                sb.AppendLine($"            get => NativeReflection.GetGlobalProperty<{propType}>(\"{p.Name}\") ?? default({propType})!;");
                if (!p.IsReadOnly)
                {
                    sb.AppendLine($"            set => NativeReflection.SetGlobalProperty(\"{p.Name}\", value!);");
                }
                sb.AppendLine("        }");
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private static void EmitFileHeader(StringBuilder sb, string summary)
        {
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//   This file was generated by O3DESharp.BindingGenerator (reflection backend).");
            sb.AppendLine("//   DO NOT EDIT - changes will be overwritten on the next generation run.");
            sb.AppendLine($"//   {summary}");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("#pragma warning disable 1591  // Missing XML comment for publicly visible type or member");
            sb.AppendLine();
        }

        /// <summary>
        /// Map a BehaviorContext marshal_type tag to the C# type we'll
        /// use in the wrapper signature. Falls back to "object" for types
        /// we don't have a primitive mapping for - those round-trip
        /// through NativeReflection's generic dispatch path, which boxes
        /// to the reflected C++ type at the runtime boundary.
        /// </summary>
        private static string MapMarshalToCSharp(ReflectionTypeInfo type)
        {
            return type.MarshalType switch
            {
                "Void"          => "void",
                "Bool"          => "bool",
                "Int8"          => "sbyte",
                "Int16"         => "short",
                "Int32"         => "int",
                "Int64"         => "long",
                "UInt8"         => "byte",
                "UInt16"        => "ushort",
                "UInt32"        => "uint",
                "UInt64"        => "ulong",
                "Float"         => "float",
                "Double"        => "double",
                "String"        => "string",
                "EntityId"      => "ulong",  // marshaled as bus handle / opaque id
                "Vector2"       => "O3DE.Vector2",
                "Vector3"       => "O3DE.Vector3",
                "Vector4"       => "O3DE.Vector4",
                "Quaternion"    => "O3DE.Quaternion",
                "Color"         => "O3DE.Color",
                "Transform"     => "O3DE.Transform",
                "Object"        => "object",  // wrapped reflected C++ type
                "Unknown"       => "object",
                _               => "object",
            };
        }

        /// <summary>
        /// Last "::"-delimited segment of a fully-qualified C++ name.
        /// For "AZ::Render::DirectionalLightFeatureProcessor" returns
        /// "DirectionalLightFeatureProcessor".
        /// </summary>
        private static string LastNamespaceSegment(string fqn)
        {
            var idx = fqn.LastIndexOf("::", StringComparison.Ordinal);
            return idx >= 0 ? fqn.Substring(idx + 2) : fqn;
        }

        /// <summary>
        /// Sanitize a string into a valid C# identifier. Strips template
        /// brackets, replaces ::, &lt;, &gt;, comma, space with _; falls
        /// back to "Unnamed" if the result would be empty.
        /// </summary>
        private static string SafeIdentifier(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Unnamed";
            var sb = new StringBuilder(raw.Length);
            foreach (var c in raw)
            {
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else sb.Append('_');
            }
            var s = sb.ToString();
            if (s.Length == 0) return "Unnamed";
            if (char.IsDigit(s[0])) s = "_" + s;
            // Reserved C# keywords prefix-escape
            if (CSharpReservedKeywords.Contains(s)) s = "@" + s;
            return s;
        }

        /// <summary>
        /// Safe parameter name from a parameter's type_name (the C++ side
        /// doesn't reflect parameter names, just types). Pattern:
        /// "&lt;typeName&gt;_&lt;index&gt;" with sanitization.
        /// </summary>
        private static string SafeParamName(string typeName, int index)
        {
            var baseName = SafeIdentifier(string.IsNullOrEmpty(typeName) ? "arg" : typeName);
            return $"arg{index}_{baseName}";
        }

        /// <summary>
        /// Sanitize a string for use as a filename. Strips template
        /// brackets, ::, etc. Length-capped at 64 chars so paths stay
        /// short on Windows.
        /// </summary>
        private static string SafeFileName(string raw)
        {
            var sanitized = SafeIdentifier(raw);
            return sanitized.Length > 64 ? sanitized.Substring(0, 64) : sanitized;
        }

        /// <summary>
        /// Namespace-segment sanitizer (slightly more permissive than
        /// identifier - dots are allowed since C# namespaces are dotted).
        /// </summary>
        private static string SafeNamespaceSegment(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
            return SafeIdentifier(raw);
        }

        private static string XmlEscape(string s) =>
            string.IsNullOrEmpty(s)
                ? string.Empty
                : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        /// <summary>
        /// True if the type's name is a C++ template specialization.
        /// These reflect as nightmares like
        ///   AZStd::unordered_map&lt;X, Y, AZStd::hash&lt;X&gt;, ...&gt;
        /// which sanitize to unreadable filenames + identifiers and
        /// rarely map cleanly to a C# surface anyway - the underlying
        /// containers route through marshalers by TypeId at runtime.
        /// </summary>
        private static bool IsTemplateSpecialization(string name)
        {
            return !string.IsNullOrEmpty(name) && (name.Contains('<') || name.Contains('>'));
        }

        /// <summary>
        /// Methods/types whose names indicate they're internal helpers
        /// that shouldn't appear in the generated API surface. Catches
        /// operator overloads, implicit conversions, and the AZStd
        /// special-method spellings that come through reflection as
        /// noise.
        /// </summary>
        private static bool IsGeneratorUnsafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            if (name.StartsWith("operator", StringComparison.Ordinal)) return true;
            if (name == "~") return true;
            // BehaviorContext sometimes reflects compiler-generated names with
            // characters libclang already handled but our string-builder
            // would emit as invalid C#.
            if (name.Contains('<') || name.Contains('>') || name.Contains('=')) return true;
            return false;
        }

        private static readonly HashSet<string> CSharpReservedKeywords = new(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
            "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
            "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
            "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
            "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this",
            "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
            "using", "virtual", "void", "volatile", "while",
        };
    }

    public sealed class ReflectionGenerationResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public int ClassFilesWritten { get; set; }
        public int BusFilesWritten { get; set; }
        public int GlobalsFilesWritten { get; set; }
        public int TotalClasses { get; set; }
        public int TotalEBuses { get; set; }
        public int TotalGlobals { get; set; }
    }
}
