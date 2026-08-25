/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace O3DESharp.SourceGenerators
{
    /// <summary>
    /// Emits the ABI adapter: the five [UnmanagedCallersOnly] thunks that back
    /// O3DE.Interop.ManagedExports, the GetManagedExports entry point that
    /// packs their addresses into the struct, and one ScriptTypeRegistry
    /// registration per ScriptComponent subclass in the compilation.
    ///
    /// Runs only when build_property.O3DESharpEmitHostExports is "true", which
    /// only O3DE.Core.csproj sets. ManagedExports is a single well-known type;
    /// if every consumer assembly emitted its own copy, the name would resolve
    /// ambiguously as soon as two of them were referenced together.
    ///
    /// Almost nothing varies by host mode, and that is the honest size of the
    /// difference: [UnmanagedCallersOnly(EntryPoint = ...)] is legal in both
    /// modes and only takes effect when compiling to a native library, so one
    /// thunk shape serves Coral and NativeAOT alike. Only the HostMode constant
    /// differs. The real per-mode divergence lives in ManagedExportsImpl's
    /// #if O3DE_HOST_NATIVEAOT (HotReloadSwap) and in which C++ IManagedHost
    /// implementation consumes the struct.
    /// </summary>
    [Generator]
    public sealed class HostExportsGenerator : IIncrementalGenerator
    {
        private const string ScriptComponentFullName = "O3DE.ScriptComponent";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Build-mode inputs. Both are published by O3DE.Core.csproj as
            // CompilerVisibleProperty; without that they are simply absent and
            // the generator does nothing.
            var buildMode = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) =>
            {
                provider.GlobalOptions.TryGetValue("build_property.O3DESharpEmitHostExports", out var emit);
                provider.GlobalOptions.TryGetValue("build_property.O3DESharpHostMode", out var mode);
                return new BuildMode(
                    string.Equals(emit, "true", System.StringComparison.OrdinalIgnoreCase),
                    string.IsNullOrEmpty(mode) ? "Coral" : mode!);
            });

            // Every concrete ScriptComponent subclass gets a registry factory.
            // A direct `new T()` is visible to the AOT compiler; the
            // Assembly.GetType + Activator.CreateInstance it replaces is not.
            var scriptTypes = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                    transform: static (ctx, _) => TryGetScriptComponent(ctx))
                .Where(static name => name is not null)
                .Select(static (name, _) => name!)
                .Collect();

            context.RegisterSourceOutput(
                buildMode.Combine(scriptTypes),
                static (sourceContext, pair) =>
                {
                    var (mode, types) = pair;
                    if (!mode.EmitHostExports)
                    {
                        return;
                    }

                    sourceContext.AddSource(
                        "O3DE.Interop.ManagedExports.g.cs",
                        SourceText.From(EmitExports(mode), Encoding.UTF8));

                    sourceContext.AddSource(
                        "O3DE.Interop.GeneratedScriptTypes.g.cs",
                        SourceText.From(EmitScriptTypes(types), Encoding.UTF8));
                });
        }

        // -----------------------------------------------------------
        // Semantic resolution
        // -----------------------------------------------------------

        private static string? TryGetScriptComponent(GeneratorSyntaxContext context)
        {
            var classSyntax = (ClassDeclarationSyntax)context.Node;
            if (context.SemanticModel.GetDeclaredSymbol(classSyntax) is not INamedTypeSymbol symbol)
            {
                return null;
            }

            // Abstract types cannot be constructed, and a type without a public
            // parameterless constructor has no `new T()` to emit.
            if (symbol.IsAbstract || symbol.IsStatic || symbol.IsGenericType)
            {
                return null;
            }
            if (!symbol.InstanceConstructors.Any(c =>
                    c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
            {
                return null;
            }

            for (var baseType = symbol.BaseType; baseType is not null; baseType = baseType.BaseType)
            {
                if (baseType.ToDisplayString() == ScriptComponentFullName)
                {
                    return symbol.ToDisplayString();
                }
            }
            return null;
        }

        // -----------------------------------------------------------
        // Emit
        // -----------------------------------------------------------

        private static void AppendHeader(StringBuilder sb)
        {
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//   This file was generated by O3DESharp.SourceGenerators (HostExportsGenerator).");
            sb.AppendLine("//   DO NOT EDIT.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine();
        }

        private static string EmitExports(BuildMode mode)
        {
            var sb = new StringBuilder();
            AppendHeader(sb);
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Runtime.InteropServices;");
            sb.AppendLine();
            sb.AppendLine("namespace O3DE.Interop");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// The [UnmanagedCallersOnly] thunks backing ManagedExports, plus the");
            sb.AppendLine("    /// single entry point that exchanges the two ABI structs.");
            sb.AppendLine("    ///");
            sb.AppendLine("    /// Bodies live in ManagedExportsImpl - these are UTF-8 marshaling");
            sb.AppendLine("    /// wrappers only. None of them may throw: an exception crossing an");
            sb.AppendLine("    /// [UnmanagedCallersOnly] boundary terminates the process.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static unsafe class ManagedExportsThunks");
            sb.AppendLine("    {");
            sb.AppendLine($"        /// <summary>Build mode this assembly was generated for.</summary>");
            sb.AppendLine($"        public const string HostMode = \"{mode.HostMode}\";");
            sb.AppendLine();

            sb.AppendLine("        [UnmanagedCallersOnly(EntryPoint = \"O3DESharp_CreateInstance\")]");
            sb.AppendLine("        public static int O3DESharp_CreateInstance(byte* utf8TypeName)");
            sb.AppendLine("        {");
            sb.AppendLine("            string? name = Marshal.PtrToStringUTF8((IntPtr)utf8TypeName);");
            sb.AppendLine("            return name is null ? 0 : ManagedExportsImpl.CreateInstance(name);");
            sb.AppendLine("        }");
            sb.AppendLine();

            sb.AppendLine("        [UnmanagedCallersOnly(EntryPoint = \"O3DESharp_InvokeLifecycle\")]");
            sb.AppendLine("        public static int O3DESharp_InvokeLifecycle(int handle, int lifecycleId, float arg)");
            sb.AppendLine("        {");
            sb.AppendLine("            return ManagedExportsImpl.InvokeLifecycle(handle, lifecycleId, arg);");
            sb.AppendLine("        }");
            sb.AppendLine();

            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Writes the handler's JSON result into outBuffer and returns the");
            sb.AppendLine("        /// number of bytes the result needs (snprintf semantics), so a short");
            sb.AppendLine("        /// buffer is a retry rather than a silent truncation. 0 means no");
            sb.AppendLine("        /// handler took the event; -1 means the dispatch itself failed.");
            sb.AppendLine("        /// Keeping the buffer caller-owned is what lets ManagedExports stay");
            sb.AppendLine("        /// at five fields with no Free export.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        [UnmanagedCallersOnly(EntryPoint = \"O3DESharp_DispatchEBusEvent\")]");
            sb.AppendLine("        public static int O3DESharp_DispatchEBusEvent(");
            sb.AppendLine("            long token, byte* utf8EventName, byte* utf8ArgsJson, byte* outBuffer, int outCapacity)");
            sb.AppendLine("        {");
            sb.AppendLine("            string eventName = Marshal.PtrToStringUTF8((IntPtr)utf8EventName) ?? string.Empty;");
            sb.AppendLine("            string argsJson = Marshal.PtrToStringUTF8((IntPtr)utf8ArgsJson) ?? \"[]\";");
            sb.AppendLine();
            sb.AppendLine("            string? result = ManagedExportsImpl.DispatchEBusEvent(token, eventName, argsJson);");
            sb.AppendLine("            if (result is null)");
            sb.AppendLine("            {");
            sb.AppendLine("                return 0;");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(result);");
            sb.AppendLine("            if (outBuffer != null && outCapacity > utf8.Length)");
            sb.AppendLine("            {");
            sb.AppendLine("                Marshal.Copy(utf8, 0, (IntPtr)outBuffer, utf8.Length);");
            sb.AppendLine("                outBuffer[utf8.Length] = 0;");
            sb.AppendLine("            }");
            sb.AppendLine("            // Always the required length including the NUL, so the caller can");
            sb.AppendLine("            // size a buffer and call again.");
            sb.AppendLine("            return utf8.Length + 1;");
            sb.AppendLine("        }");
            sb.AppendLine();

            sb.AppendLine("        [UnmanagedCallersOnly(EntryPoint = \"O3DESharp_DestroyInstance\")]");
            sb.AppendLine("        public static void O3DESharp_DestroyInstance(int handle)");
            sb.AppendLine("        {");
            sb.AppendLine("            ManagedExportsImpl.DestroyInstance(handle);");
            sb.AppendLine("        }");
            sb.AppendLine();

            sb.AppendLine("        [UnmanagedCallersOnly(EntryPoint = \"O3DESharp_HotReloadSwap\")]");
            sb.AppendLine("        public static int O3DESharp_HotReloadSwap()");
            sb.AppendLine("        {");
            sb.AppendLine("            return ManagedExportsImpl.HotReloadSwap();");
            sb.AppendLine("        }");
            sb.AppendLine();

            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// The one call that exchanges the ABI. C++ hands in a populated");
            sb.AppendLine("        /// NativeImports and an empty ManagedExports; this stores the former");
            sb.AppendLine("        /// and fills the latter. Returns 1 on success, 0 on version mismatch.");
            sb.AppendLine("        ///");
            sb.AppendLine("        /// Under NativeAOT this is the exported symbol the host resolves with");
            sb.AppendLine("        /// GetProcAddress/dlsym. Under Coral the host resolves it through");
            sb.AppendLine("        /// CoralNativeThunkHost instead; the code is identical either way.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        [UnmanagedCallersOnly(EntryPoint = \"O3DESharp_GetManagedExports\")]");
            sb.AppendLine("        public static int O3DESharp_GetManagedExports(NativeImports* imports, ManagedExports* exports)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (imports == null || exports == null)");
            sb.AppendLine("            {");
            sb.AppendLine("                return 0;");
            sb.AppendLine("            }");
            sb.AppendLine("            if (imports->Version != HostAbi.Version)");
            sb.AppendLine("            {");
            sb.AppendLine("                // Refuse rather than reinterpret: an unrecognised version means");
            sb.AppendLine("                // every pointer after the first divergence is garbage.");
            sb.AppendLine("                return 0;");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            NativeImportsStore.Imports = *imports;");
            sb.AppendLine("#if O3DE_HOST_NATIVEAOT");
            sb.AppendLine("            // Under Coral, InternalCalls is populated by AddInternalCall/");
            sb.AppendLine("            // UploadInternalCalls and NativeImportsStore is descriptive only.");
            sb.AppendLine("            // Under NativeAOT there is no Coral to do that, so this is the");
            sb.AppendLine("            // only thing that ever assigns InternalCalls' function pointers.");
            sb.AppendLine("            NativeImportsWiring.Apply(in NativeImportsStore.Imports);");
            sb.AppendLine("#endif");
            sb.AppendLine("            GeneratedScriptTypes.RegisterAll();");
            sb.AppendLine();
            sb.AppendLine("            exports->Version = HostAbi.Version;");
            sb.AppendLine("            exports->CreateInstance = (IntPtr)(delegate* unmanaged<byte*, int>)&O3DESharp_CreateInstance;");
            sb.AppendLine("            exports->InvokeLifecycle = (IntPtr)(delegate* unmanaged<int, int, float, int>)&O3DESharp_InvokeLifecycle;");
            sb.AppendLine("            exports->DispatchEBusEvent = (IntPtr)(delegate* unmanaged<long, byte*, byte*, byte*, int, int>)&O3DESharp_DispatchEBusEvent;");
            sb.AppendLine("            exports->DestroyInstance = (IntPtr)(delegate* unmanaged<int, void>)&O3DESharp_DestroyInstance;");
            sb.AppendLine("            exports->HotReloadSwap = (IntPtr)(delegate* unmanaged<int>)&O3DESharp_HotReloadSwap;");
            sb.AppendLine("            return 1;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Holds the NativeImports handed over at init. Under Coral this is");
            sb.AppendLine("    /// descriptive only - InternalCalls is still populated by Coral's own");
            sb.AppendLine("    /// AddInternalCall/UploadInternalCalls and nothing about that path");
            sb.AppendLine("    /// changes. Under NativeAOT it is the sole source of the pointers,");
            sb.AppendLine("    /// consumed by NativeImportsWiring.Apply immediately after this is set.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static class NativeImportsStore");
            sb.AppendLine("    {");
            sb.AppendLine("        public static NativeImports Imports;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string EmitScriptTypes(ImmutableArray<string> types)
        {
            var sb = new StringBuilder();
            AppendHeader(sb);
            sb.AppendLine("namespace O3DE.Interop");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// One ScriptTypeRegistry entry per concrete ScriptComponent subclass");
            sb.AppendLine("    /// in this compilation. Each factory is a direct `new T()`, which the");
            sb.AppendLine("    /// AOT compiler can see; the Assembly.GetType + Activator.CreateInstance");
            sb.AppendLine("    /// it replaces is exactly what it cannot.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static class GeneratedScriptTypes");
            sb.AppendLine("    {");
            sb.AppendLine("        /// <summary>Idempotent: Register replaces, so a hot-reload re-run is safe.</summary>");
            sb.AppendLine("        public static void RegisterAll()");
            sb.AppendLine("        {");
            foreach (var type in types.Distinct().OrderBy(t => t, System.StringComparer.Ordinal))
            {
                sb.AppendLine($"            ScriptTypeRegistry.Register(\"{type}\", static () => new global::{type}());");
            }
            if (types.Length == 0)
            {
                sb.AppendLine("            // No ScriptComponent subclasses in this compilation.");
            }
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Build-mode inputs, as a record so the incremental generator caches it by
    /// value - a sealed class would invalidate the cache on every keystroke.
    /// </summary>
    internal sealed record BuildMode(bool EmitHostExports, string HostMode);
}
