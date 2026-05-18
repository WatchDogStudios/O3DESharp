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
                // Dedup by the C# class name we'd emit (the LAST segment
                // of the FQN, sanitized). The JSON sometimes contains
                // two reflected classes with the same simple name from
                // different C++ namespaces (e.g. AzCore::RenderStates
                // and Atom::RenderStates both reflected as "RenderStates").
                // C# can only have one "public static class RenderStates"
                // per namespace - first one wins.
                var seenClassNames = new HashSet<string>(StringComparer.Ordinal);
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

                    // Dedup key is the C# class name we're about to
                    // declare (LastNamespaceSegment + SafeIdentifier),
                    // not the filename. Two FQNs like AZ::RenderStates
                    // and AZ::RHI::RenderStates would have distinct
                    // filenames (AZ__RenderStates vs AZ__RHI__RenderStates)
                    // but BOTH would try to declare `public static class
                    // RenderStates` in the same C# namespace, which the
                    // compiler rejects as CS0101 "namespace already
                    // contains a definition for X".
                    var csharpClassName = SafeIdentifier(LastNamespaceSegment(cls.Name));
                    if (!seenClassNames.Add(csharpClassName))
                    {
                        if (_verbose)
                        {
                            Console.WriteLine($"  [{gemGroup.Key}] Skipping duplicate class name: {cls.Name} (collides with prior {csharpClassName})");
                        }
                        gemSkippedCount++;
                        continue;
                    }
                    var safeName = SafeFileName(cls.Name);
                    var cs = EmitClass(cls, gemGroup.Key);
                    File.WriteAllText(Path.Combine(gemDir, safeName + ".g.cs"), cs);
                    classFiles++;
                    gemClassCount++;
                }
                if (_verbose)
                {
                    Console.WriteLine(
                        $"  [{gemGroup.Key}] Wrote {gemClassCount} class wrappers" +
                        (gemSkippedCount > 0 ? $" ({gemSkippedCount} skipped - templates/duplicates)" : ""));
                }
            }

            foreach (var gemGroup in busesByGem)
            {
                var gemDir = Path.Combine(outputDir, gemGroup.Key, "EBuses");
                Directory.CreateDirectory(gemDir);
                var seenBusNames = new HashSet<string>(StringComparer.Ordinal);
                int written = 0;
                foreach (var bus in gemGroup)
                {
                    var safeName = SafeFileName(bus.Name);
                    if (!seenBusNames.Add(safeName))
                    {
                        if (_verbose)
                        {
                            Console.WriteLine($"  [{gemGroup.Key}] Skipping duplicate EBus: {bus.Name}");
                        }
                        continue;
                    }
                    var cs = EmitEBus(bus, gemGroup.Key);
                    File.WriteAllText(Path.Combine(gemDir, safeName + ".g.cs"), cs);
                    busFiles++;
                    written++;
                }
                if (_verbose)
                {
                    Console.WriteLine($"  [{gemGroup.Key}] Wrote {written} EBus wrappers");
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

            // Emit a csproj for every gem bucket that produced at least
            // one .g.cs - the per-class / per-EBus files are useless
            // on their own; the user needs a compilable project that
            // references O3DE.Core (for NativeReflection dispatch) and
            // builds + deploys to <Project>/Bin/Scripts/ where Coral
            // loads it at runtime.
            var allGemKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in classesByGem) allGemKeys.Add(g.Key);
            foreach (var g in busesByGem) allGemKeys.Add(g.Key);
            allGemKeys.UnionWith(globalsGemKeys);

            int csprojFiles = 0;
            foreach (var gemKey in allGemKeys)
            {
                var gemDir = Path.Combine(outputDir, gemKey);
                if (!Directory.Exists(gemDir)) continue;
                EmitProjectFile(gemDir, gemKey);
                csprojFiles++;
            }

            return new ReflectionGenerationResult
            {
                Success = true,
                ClassFilesWritten = classFiles,
                BusFilesWritten = busFiles,
                GlobalsFilesWritten = globalsFiles,
                CsprojFilesWritten = csprojFiles,
                TotalClasses = doc.Classes.Count,
                TotalEBuses = doc.EBuses.Count,
                TotalGlobals = doc.GlobalMethods.Count + doc.GlobalProperties.Count,
            };
        }

        /// <summary>
        /// Emit O3DE.Generated.&lt;GemName&gt;.csproj into the gem
        /// bucket's output directory. Matches the debug-friendly
        /// settings from the user-script template
        /// (DebugType=full + loose-on-disk + Debug constants), references
        /// O3DE.Core via a path relative to project root, and includes
        /// a DeployToBinScripts target so the built DLL + PDB land in
        /// &lt;Project&gt;/Bin/Scripts/ where Coral picks them up at
        /// runtime.
        ///
        /// The csproj path layout: &lt;output&gt;/&lt;Gem&gt;/&lt;Gem&gt;.csproj.
        /// Relative-to-project-root: ../../../../  (4 levels up from
        /// &lt;Project&gt;/Generated/CSharp/&lt;Gem&gt;/ - assuming the
        /// caller passed --output &lt;Project&gt;/Generated/CSharp/).
        /// </summary>
        private void EmitProjectFile(string gemDir, string gemKey)
        {
            var assemblyName = $"O3DE.Generated.{SafeIdentifier(gemKey)}";
            var rootNs = $"{_rootNamespace}.{SafeNamespaceSegment(gemKey)}";
            var sb = new StringBuilder();

            sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
            sb.AppendLine();
            sb.AppendLine("  <!--");
            sb.AppendLine("    Auto-generated by O3DESharp.BindingGenerator (reflection backend).");
            sb.AppendLine("    Compiles the per-class / per-EBus / globals .g.cs files in this");
            sb.AppendLine("    directory + subdirectories into a single assembly that scripts");
            sb.AppendLine("    can reference. The generated DLL is deployed to");
            sb.AppendLine("    <Project>/Bin/Scripts/ alongside the user script DLLs so Coral");
            sb.AppendLine("    picks it up at runtime.");
            sb.AppendLine("  -->");
            sb.AppendLine();
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine("    <TargetFramework>net9.0</TargetFramework>");
            sb.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
            sb.AppendLine("    <Nullable>enable</Nullable>");
            sb.AppendLine("    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>");
            sb.AppendLine("    <LangVersion>latest</LangVersion>");
            sb.AppendLine($"    <AssemblyName>{assemblyName}</AssemblyName>");
            sb.AppendLine($"    <RootNamespace>{rootNs}</RootNamespace>");
            sb.AppendLine("    <Configurations>Debug;Release</Configurations>");
            sb.AppendLine("    <Platforms>AnyCPU</Platforms>");
            sb.AppendLine();
            sb.AppendLine("    <!-- Suppress CS1591 for whole assembly - generated wrappers");
            sb.AppendLine("         intentionally leave many XML doc comments empty when the");
            sb.AppendLine("         reflected source had none. -->");
            sb.AppendLine("    <NoWarn>$(NoWarn);1591</NoWarn>");
            sb.AppendLine();
            sb.AppendLine("    <!-- Deploy target. <Project>/Generated/CSharp/<Gem>/ is 3");
            sb.AppendLine("         directories below the project root; ../../../Bin/Scripts");
            sb.AppendLine("         gets us there. User can override per-machine. -->");
            sb.AppendLine("    <O3DEDeployPath Condition=\"'$(O3DEDeployPath)' == ''\">$(MSBuildProjectDirectory)\\..\\..\\..\\Bin\\Scripts</O3DEDeployPath>");
            sb.AppendLine();
            sb.AppendLine("    <!-- Default PDB format for non-Debug configs. Debug overrides");
            sb.AppendLine("         to full below - external managed-debugger attach against");
            sb.AppendLine("         the embedded CoreCLR (Coral delegate-host mode) needs");
            sb.AppendLine("         full-format PDBs to handshake cleanly. -->");
            sb.AppendLine("    <DebugType>portable</DebugType>");
            sb.AppendLine("    <DebugSymbols>true</DebugSymbols>");
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine();
            sb.AppendLine("  <PropertyGroup Condition=\"'$(Configuration)' == 'Debug'\">");
            sb.AppendLine("    <Optimize>false</Optimize>");
            sb.AppendLine("    <DebugSymbols>true</DebugSymbols>");
            sb.AppendLine("    <DebugType>full</DebugType>");
            sb.AppendLine("    <PublishSingleFile>false</PublishSingleFile>");
            sb.AppendLine("    <EnableCompressionInSingleFile>false</EnableCompressionInSingleFile>");
            sb.AppendLine("    <DefineConstants>DEBUG;TRACE</DefineConstants>");
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine("  <PropertyGroup Condition=\"'$(Configuration)' == 'Release'\">");
            sb.AppendLine("    <Optimize>true</Optimize>");
            sb.AppendLine("    <DefineConstants>TRACE</DefineConstants>");
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine();
            sb.AppendLine("  <!-- Reference O3DE.Core for NativeReflection + the math types");
            sb.AppendLine("       (O3DE.Vector3 etc.) that generated wrappers use. -->");
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <Reference Include=\"O3DE.Core\">");
            sb.AppendLine("      <HintPath>$(MSBuildProjectDirectory)\\..\\..\\..\\Bin\\Scripts\\O3DE.Core.dll</HintPath>");
            sb.AppendLine("      <Private>false</Private>");
            sb.AppendLine("    </Reference>");
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine();
            sb.AppendLine("  <!-- Auto-deploy after every Build. ContinueOnError prevents a");
            sb.AppendLine("       locked Bin/Scripts/*.dll (engine running) from failing the");
            sb.AppendLine("       IDE build entirely. -->");
            sb.AppendLine("  <Target Name=\"DeployToBinScripts\" AfterTargets=\"Build\">");
            sb.AppendLine($"    <Message Text=\"O3DESharp: deploying {assemblyName}.dll -&gt; $(O3DEDeployPath)\" Importance=\"high\"/>");
            sb.AppendLine("    <MakeDir Directories=\"$(O3DEDeployPath)\"/>");
            sb.AppendLine("    <Copy SourceFiles=\"$(TargetPath)\"");
            sb.AppendLine("          DestinationFolder=\"$(O3DEDeployPath)\"");
            sb.AppendLine("          SkipUnchangedFiles=\"true\"");
            sb.AppendLine("          ContinueOnError=\"true\"/>");
            sb.AppendLine("    <Copy SourceFiles=\"$(TargetDir)$(AssemblyName).pdb\"");
            sb.AppendLine("          DestinationFolder=\"$(O3DEDeployPath)\"");
            sb.AppendLine("          SkipUnchangedFiles=\"true\"");
            sb.AppendLine("          ContinueOnError=\"true\"");
            sb.AppendLine("          Condition=\"Exists('$(TargetDir)$(AssemblyName).pdb')\"/>");
            sb.AppendLine("  </Target>");
            sb.AppendLine();
            sb.AppendLine("</Project>");

            // The csproj name matches the gem bucket so multiple gem
            // buckets coexist as siblings under Generated/CSharp/ without
            // colliding. Use SafeFileName for Windows-safety in case
            // we ever get exotic gem names with characters that break paths.
            var csprojPath = Path.Combine(gemDir, $"{SafeFileName(gemKey)}.csproj");
            File.WriteAllText(csprojPath, sb.ToString());
            if (_verbose)
            {
                Console.WriteLine($"  [{gemKey}] Wrote project: {Path.GetFileName(csprojPath)}");
            }
        }

        // -----------------------------------------------------------------
        // Per-entity emitters
        // -----------------------------------------------------------------

        private string EmitClass(ReflectionClass cls, string gemName)
        {
            var sb = new StringBuilder();
            EmitFileHeader(sb, $"Wrappers for AZ class {cls.Name} (gem: {gemName})");
            sb.AppendLine("using O3DE.Reflection;");
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
                // Skip methods whose parameter list has any MarshalType=Void
                // - C# doesn't allow void parameters and the wrapper would
                // be CS1536. See HasVoidParameter for the why.
                if (HasVoidParameter(m.Parameters)) continue;

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
                // IdentifierSuffix (not SafeIdentifier) for the property
                // name segment because we're concatenating it after "Get"
                // / "Set" - SafeIdentifier would prefix C# keywords with
                // @ (e.g. "namespace" -> "@namespace"), producing
                // "Get@namespace" which is a parse error. The Get/Set
                // prefix guarantees the result starts with a letter so
                // we don't need the @ escape.
                var propName = IdentifierSuffix(p.Name);

                sb.AppendLine();
                sb.AppendLine($"        /// <summary>Get {XmlEscape(p.Name)} - {XmlEscape(p.Description)}</summary>");
                sb.AppendLine($"        public static {propType} Get{propName}(NativeObject __instance)");
                sb.AppendLine("        {");
                // Null-forgiving (!) instead of ?? default: GetProperty<T>
                // returns T? which is just T for value types, so ?? would
                // be CS0019 "operator ?? cannot be applied to T and T".
                // ! collapses cleanly for both T (no-op) and T? (strips
                // nullability) - dispatcher's contract is that a missing
                // property returns default(T), which scripts can detect
                // via comparison if they care.
                sb.AppendLine($"            return NativeReflection.GetProperty<{propType}>(__instance, \"{p.Name}\")!;");
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
            sb.AppendLine("using O3DE.Reflection;");
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
                if (HasVoidParameter(e.Parameters)) continue;

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
                        sb.AppendLine($"            return __r!;");
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
                        sb.AppendLine($"            return __r!;");
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
            sb.AppendLine("using O3DE.Reflection;");
            sb.AppendLine();
            sb.AppendLine($"namespace {_rootNamespace}.{SafeNamespaceSegment(gemName)}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>Reflected global functions and properties from this gem.</summary>");
            sb.AppendLine("    public static class Globals");
            sb.AppendLine("    {");

            foreach (var m in methods)
            {
                if (string.IsNullOrEmpty(m.Name) || IsGeneratorUnsafeName(m.Name)) continue;
                if (HasVoidParameter(m.Parameters)) continue;
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
                sb.AppendLine($"            get => NativeReflection.GetGlobalProperty<{propType}>(\"{p.Name}\")!;");
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
        /// "arg&lt;index&gt;_&lt;typeName&gt;" with sanitization.
        ///
        /// Critical: we do NOT use SafeIdentifier here because it prefixes
        /// C# reserved words with @ (turning "string" into "@string"), and
        /// the @ is only valid at the START of an identifier. Pasting
        /// "@string" after "arg0_" gives "arg0_@string" which is a parse
        /// error. Strip-only sanitization (replace bad chars with _) is
        /// what we want since the result is always prefixed with "argN_"
        /// which guarantees it doesn't start with @ or a digit.
        /// </summary>
        private static string SafeParamName(string typeName, int index)
        {
            var src = string.IsNullOrEmpty(typeName) ? "arg" : typeName;
            var sb = new StringBuilder(src.Length);
            foreach (var c in src)
            {
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else sb.Append('_');
            }
            var sanitized = sb.Length == 0 ? "arg" : sb.ToString();
            return $"arg{index}_{sanitized}";
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
        /// Sanitize a string into an identifier-tail-safe form: bad chars
        /// replaced with _ but no @ keyword-escape prefix. Used when the
        /// result is going to be concatenated AFTER another prefix
        /// (Get/Set, etc.) which guarantees it doesn't start with a
        /// digit or a keyword - so the @ escape is unnecessary and
        /// would be a parse error mid-identifier.
        /// </summary>
        private static string IdentifierSuffix(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Unnamed";
            var sb = new StringBuilder(raw.Length);
            foreach (var c in raw)
            {
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else sb.Append('_');
            }
            return sb.Length == 0 ? "Unnamed" : sb.ToString();
        }

        /// <summary>
        /// True if any parameter on a method/event has MarshalType=Void.
        /// Methods that "take void" come through reflection as quirks of
        /// the BehaviorContext registration (a generic helper signature
        /// got reflected with void in a slot it can't actually accept).
        /// C# doesn't allow void parameters, so the wrapper would be
        /// CS1536. Skip the whole wrapper rather than emit garbage.
        /// </summary>
        private static bool HasVoidParameter(List<ReflectionParameter> parameters)
        {
            foreach (var p in parameters)
            {
                if (string.Equals(p.MarshalType, "Void", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

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
        public int CsprojFilesWritten { get; set; }
        public int TotalClasses { get; set; }
        public int TotalEBuses { get; set; }
        public int TotalGlobals { get; set; }
    }
}
