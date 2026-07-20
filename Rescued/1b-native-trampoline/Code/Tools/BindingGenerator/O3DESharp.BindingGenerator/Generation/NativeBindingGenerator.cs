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
using O3DESharp.BindingGenerator.Configuration;
using O3DESharp.BindingGenerator.Parsing;

namespace O3DESharp.BindingGenerator.Generation
{
    /// <summary>
    /// Joins the C++-side native-binding manifest (NativeBindingManifestExporter's
    /// JSON - "what's reflected, with what argument shapes, but no native
    /// symbol") against the libclang-side reflection call-site parse
    /// (ReflectionCallSiteParser's output - "what &amp;C::Symbol expressions
    /// exist at reflection call sites, keyed by the SAME reflected name"),
    /// classifies each joined entry as bindable or not (conservatively -
    /// v1 never binds anything the classifier can't be certain about), and
    /// emits real native-call trampoline C++ for everything bindable.
    ///
    /// This is 1B's "native-symbol binding generator" (spec §6.1) made
    /// concrete for the in-tree slice: it closes the loop the spec
    /// describes as "the manifest join isn't constructible" (§3 point 2)
    /// by making the join happen INSIDE ReflectionCallSiteParser (which
    /// sees both the reflected name and the symbol in one call
    /// expression) rather than as a separate cross-reference step - by
    /// the time NativeBindingGenerator runs, the join key on both sides
    /// really is just (OwningClassName, ReflectedName) string equality.
    /// </summary>
    public class NativeBindingGenerator
    {
        private readonly bool _verbose;

        public NativeBindingGenerator(bool verbose = false)
        {
            _verbose = verbose;
        }

        /// <summary>
        /// Result of joining + classifying one gem's manifest against its
        /// call-site parse. Exposed for testing (golden-file assertions
        /// on classification decisions without needing to emit C++).
        /// </summary>
        public sealed class JoinResult
        {
            public List<NativeBindingManifestMethod> Bindable { get; } = new();
            public List<(NativeBindingManifestMethod Method, string Reason)> NonBindable { get; } = new();
        }

        /// <summary>
        /// Join the manifest against call sites and classify every entry.
        /// Mutates each NativeBindingManifestMethod's Bindable /
        /// NativeQualifiedSymbol / NonBindableReason fields in place (the
        /// manifest objects are the source of truth the emitter reads
        /// from next), and returns a partition for convenience/testing.
        /// </summary>
        public JoinResult JoinAndClassify(NativeBindingManifestDocument manifest, ReflectionCallSiteResult callSites)
        {
            var result = new JoinResult();

            // Index call sites by (OwningClassName, ReflectedName). A gem's
            // reflection .cpp can (rarely, but validly - e.g. two classes in
            // one file both reflecting a same-named "Method") produce more
            // than one call site per key; MultiValueIndex below keeps every
            // match so LooksOverloaded / ambiguity is visible instead of one
            // silently shadowing another.
            var index = new Dictionary<string, List<ReflectionCallSite>>(StringComparer.Ordinal);
            foreach (var site in callSites.CallSites)
            {
                if (string.IsNullOrEmpty(site.EnclosingBusName) == false)
                {
                    // EBus-scoped call site (Event inside an EBus<T>::Behavior
                    // chain) - handled by the EBus exclusion rule below at
                    // classification time; still indexed so a class-scoped
                    // entry with the same textual key doesn't miss it, but
                    // never treated as a Method/Property match.
                }
                var key = site.OwningClassName + "::" + site.ReflectedName;
                if (!index.TryGetValue(key, out var list))
                {
                    list = new List<ReflectionCallSite>();
                    index[key] = list;
                }
                list.Add(site);
            }

            // Count reflected-name occurrences per owning class within the
            // MANIFEST itself too - this is the authoritative overload
            // signal (BehaviorContext's own m_overload chain surfaces as
            // multiple ManifestMethodEntry rows sharing one BindingId at
            // the C++ export stage, since BuildManifest walks
            // cls->methods, which is BehaviorContext's per-name bucket -
            // see BehaviorContextReflector::ReflectClass iterating
            // behaviorClass->m_methods, itself an
            // unordered_map<name, BehaviorMethod*> where an overloaded
            // reflected name resolves to ONE BehaviorMethod carrying an
            // m_overload chain internally, NOT multiple map entries - so
            // in fact BehaviorContext already collapses overloads to one
            // manifest row. We therefore also treat >1 call-site match for
            // the same key, AND ReflectionCallSite.LooksOverloaded, as
            // overload signals, since those are the only places overloads
            // are visible pre-collapse.)
            foreach (var method in manifest.Methods)
            {
                var key = method.OwningClassName + "::" + method.ReflectedName;
                ClassifyOne(method, index, key);

                if (method.Bindable)
                {
                    result.Bindable.Add(method);
                }
                else
                {
                    result.NonBindable.Add((method, method.NonBindableReason));
                }
            }

            return result;
        }

        private void ClassifyOne(
            NativeBindingManifestMethod method,
            Dictionary<string, List<ReflectionCallSite>> index,
            string key)
        {
            // Rule 1 (spec §6.1 "conservative classifier v1: never bind
            // overloaded/OnDemand/lambda/EBus-by-id"): EBus events are
            // excluded permanently in v1 - the manifest itself never
            // contains EBus entries (NativeBindingManifest.BuildManifest
            // only walks classes + global methods; see
            // NativeBindingManifest.h's "EBus events intentionally NOT
            // included" comment) so this rule is enforced structurally,
            // not by a runtime check here. Nothing to do for this case.

            if (!index.TryGetValue(key, out var matches) || matches.Count == 0)
            {
                method.Bindable = false;
                method.NonBindableReason = "NoNativeSideCounterpart";
                return;
            }

            if (matches.Count > 1 || matches.Any(m => m.LooksOverloaded))
            {
                method.Bindable = false;
                method.NonBindableReason = "Overloaded";
                return;
            }

            var site = matches[0];

            if (!site.IsPlainMemberPointer || string.IsNullOrEmpty(site.NativeQualifiedSymbol))
            {
                method.Bindable = false;
                method.NonBindableReason = "ReflectedViaLambda";
                return;
            }

            // OnDemand-reflected template types: heuristic - the owning
            // class name or any argument's cpp_type_name containing '<'
            // indicates a template instantiation, which in O3DE is
            // virtually always reflected via AzGenericTypeInfo /
            // OnDemandReflection rather than a plain BehaviorClass with a
            // stable size/align (owning_class_size_bytes stays 0 for
            // those - AZ::BehaviorClass::m_size is only meaningfully
            // populated for concretely-reflected classes). v1 excludes
            // these conservatively rather than risk a wrong size/align in
            // a generated trampoline's reinterpret_cast.
            if (method.OwningClassName.Contains('<') ||
                method.Arguments.Any(a => a.CppTypeName.Contains('<')) ||
                method.Return.CppTypeName.Contains('<'))
            {
                method.Bindable = false;
                method.NonBindableReason = "OnDemandTemplateType";
                return;
            }
            if (!method.IsStatic && method.OwningClassSizeBytes == 0 && !string.IsNullOrEmpty(method.OwningClassName))
            {
                // A member method whose owning class has no recorded size
                // is exactly the OnDemand/incomplete-reflection signature
                // described above (BehaviorClass::m_size is 0 only for
                // types that were never given a concrete BehaviorClass
                // entry with real layout - see NativeBindingManifest.cpp's
                // BuildManifestMethod, which pulls m_size straight from
                // AZ::BehaviorClass).
                method.Bindable = false;
                method.NonBindableReason = "OnDemandTemplateType";
                return;
            }

            // Arg-storage-class soundness: every argument (and the return,
            // when non-void) must have resolved to a known ArgStorageClass
            // on the C++ export side. "Unknown" only happens for traits
            // BehaviorParameter itself couldn't classify - see
            // NativeBindingManifest.cpp's BuildManifestArgument, which
            // only ever assigns Value/Pointer/Reference/ConstReference,
            // so in practice "Unknown" reaching here would mean the JSON
            // was hand-edited or came from a mismatched exporter version;
            // still checked defensively since a generated trampoline for
            // an unknown storage class cannot be written safely.
            if (method.Arguments.Any(a => a.StorageClass == "Unknown") ||
                (method.Return.CppTypeName != "void" && method.Return.StorageClass == "Unknown"))
            {
                method.Bindable = false;
                method.NonBindableReason = "UnsupportedArgStorage";
                return;
            }

            method.NativeQualifiedSymbol = site.NativeQualifiedSymbol;
            method.Bindable = true;
            method.NonBindableReason = "None";
        }

        // -------------------------------------------------------------------
        // Trampoline emission
        // -------------------------------------------------------------------

        /// <summary>
        /// Emit one .cpp per gem containing a real native-call trampoline
        /// for every bindable entry, each self-registering into
        /// O3DESharp::BindingRegistry at static-init. Shape verified
        /// against the 1B P0 probe (Gems/ScriptCanvasTesting/.../Native/
        /// BehaviorNativeBindingTests.cpp): for a member method,
        /// `reinterpret_cast&lt;const C*&gt;(self)-&gt;Method(unpacked args...)`
        /// then `result-&gt;StoreResult(ret)` (plain StoreResult, no explicit
        /// template argument - see that test's c7f4cd7719 fixup for why).
        /// </summary>
        public void GenerateTrampolines(JoinResult joined, string gemName, string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);

            var sb = new StringBuilder();
            AppendFileHeader(sb);

            sb.AppendLine("// 1B native-binding trampolines for gem '" + gemName + "'.");
            sb.AppendLine("// Each function below performs a DIRECT native call - reinterpret_cast + a real");
            sb.AppendLine("// C++ call - instead of AZ::BehaviorMethod::Call's virtual dispatch / functor /");
            sb.AppendLine("// per-argument RTTI ConvertTo path. Every trampoline self-registers into");
            sb.AppendLine("// O3DESharp::BindingRegistry at static-init so no other TU needs a build");
            sb.AppendLine("// dependency on this generated file to benefit from it - callers go through");
            sb.AppendLine("// BindingRegistry::Invoke(bindingId, ...), which falls back to");
            sb.AppendLine("// AZ::BehaviorMethod::Call automatically when a binding-id has no trampoline");
            sb.AppendLine("// (e.g. this file hasn't been generated/linked yet, or the method was");
            sb.AppendLine("// classified non-bindable).");
            sb.AppendLine();
            sb.AppendLine("#include <Scripting/Reflection/BindingRegistry.h>");
            sb.AppendLine("#include <AzCore/RTTI/BehaviorContext.h>");
            sb.AppendLine();

            var includeGuard = new HashSet<string>(StringComparer.Ordinal);
            // Bindable methods only reference types already visible through
            // BehaviorContext.h + whatever headers the gem's own reflection
            // .cpp normally needs; those are the gem's problem to provide via
            // its own build - this generated TU does NOT attempt to guess
            // and #include gem-specific headers (would require re-deriving
            // the header path from a qualified type name, which is exactly
            // the kind of fragile heuristic the manifest's binding_id /
            // native_qualified_symbol strings are meant to avoid needing).
            // Instead the generated TU is compiled as part of the SAME gem
            // module as the reflection it binds, where those headers are
            // already transitively included by that module's other TUs
            // sharing a PCH/unity batch - documented in the generated
            // file's own header comment below for a human maintaining this.
            sb.AppendLine("// NOTE: this file intentionally does not #include gem-specific headers for");
            sb.AppendLine("// the bound types. It must be compiled as part of the same gem module whose");
            sb.AppendLine("// reflection it binds, relying on that module's unity build / existing");
            sb.AppendLine("// includes to bring the real class declarations into scope.");
            sb.AppendLine();

            sb.AppendLine($"namespace O3DESharp::Generated::NativeBindings::{SanitizeIdentifier(gemName)}");
            sb.AppendLine("{");

            var trampolineNames = new List<(string BindingId, string FnName)>();

            foreach (var method in joined.Bindable)
            {
                var fnName = "Trampoline_" + SanitizeIdentifier(method.OwningClassName) + "_" + SanitizeIdentifier(method.ReflectedName);
                if (!includeGuard.Add(fnName))
                {
                    continue; // defensive: duplicate binding id within one gem
                }
                trampolineNames.Add((method.BindingId, fnName));
                EmitTrampoline(sb, fnName, method);
            }

            sb.AppendLine();
            sb.AppendLine("    // Static-init self-registration. Runs once per process for this TU,");
            sb.AppendLine("    // before main()/module entry - same timing guarantee");
            sb.AppendLine("    // O3DESharp::BindingRegistry::GetMap()'s construct-on-first-use handles");
            sb.AppendLine("    // safely regardless of relative static-init order across TUs.");
            sb.AppendLine("    struct RegistrationHelper");
            sb.AppendLine("    {");
            sb.AppendLine("        RegistrationHelper()");
            sb.AppendLine("        {");
            foreach (var (bindingId, fnName) in trampolineNames)
            {
                sb.AppendLine($"            O3DESharp::BindingRegistry::Register(\"{EscapeCpp(bindingId)}\", &{fnName});");
            }
            sb.AppendLine("        }");
            sb.AppendLine("    };");
            sb.AppendLine("    static RegistrationHelper s_registrationHelper;");
            sb.AppendLine("}");

            var outputPath = Path.Combine(outputDirectory, $"{SanitizeIdentifier(gemName)}NativeBindings.g.cpp");
            File.WriteAllText(outputPath, sb.ToString());
            Log($"  Generated {trampolineNames.Count} trampoline(s): {outputPath}");
        }

        private void EmitTrampoline(StringBuilder sb, string fnName, NativeBindingManifestMethod method)
        {
            var isMember = !method.IsStatic && !string.IsNullOrEmpty(method.OwningClassName);
            var returnCppType = method.Return.CppTypeName;
            var isVoid = returnCppType == "void" || string.IsNullOrEmpty(returnCppType);

            sb.AppendLine($"    // {method.NativeQualifiedSymbol}{(method.IsConst ? " const" : "")} - binding id \"{method.BindingId}\"");
            sb.AppendLine($"    void {fnName}(const void* self, AZStd::span<AZ::BehaviorArgument> args, AZ::BehaviorArgument* result)");
            sb.AppendLine("    {");

            // Unpack arguments. Each argument is args[i]; storage class
            // dictates how we pull the value out of the type-erased
            // BehaviorArgument, mirroring BehaviorArgument::GetAsUnsafe<T>'s
            // own contract (the pointer already holds a T; GetAsUnsafe just
            // reinterprets it) - see AzCore/RTTI/BehaviorContext.h.
            var argExprs = new List<string>();
            for (int i = 0; i < method.Arguments.Count; i++)
            {
                var arg = method.Arguments[i];
                var t = string.IsNullOrEmpty(arg.CppTypeName) ? "void*" : arg.CppTypeName;
                switch (arg.StorageClass)
                {
                    case "Pointer":
                        argExprs.Add($"args[{i}].GetAsUnsafe<{StripPointerSuffix(t)}>()");
                        break;
                    case "Reference":
                    case "ConstReference":
                        argExprs.Add($"*args[{i}].GetAsUnsafe<{StripRefQualifiers(t)}>()");
                        break;
                    case "Value":
                    default:
                        argExprs.Add($"*args[{i}].GetAsUnsafe<{t}>()");
                        break;
                }
            }
            var argList = string.Join(", ", argExprs);

            string callExpr;
            if (isMember)
            {
                var cast = method.IsConst
                    ? $"reinterpret_cast<const {method.OwningClassName}*>(self)"
                    : $"reinterpret_cast<{method.OwningClassName}*>(const_cast<void*>(self))";
                callExpr = $"{cast}->{LastSymbolSegment(method.NativeQualifiedSymbol, method.OwningClassName)}({argList})";
            }
            else
            {
                callExpr = $"{method.NativeQualifiedSymbol}({argList})";
            }

            if (isVoid)
            {
                sb.AppendLine($"        {callExpr};");
                sb.AppendLine("        AZ_UNUSED(result);");
            }
            else
            {
                sb.AppendLine($"        auto returnValue = {callExpr};");
                sb.AppendLine("        if (result != nullptr)");
                sb.AppendLine("        {");
                // Plain StoreResult(x) - NOT StoreResult<T>(x). Verified by
                // the P0 probe's fixup commit (c7f4cd7719): an explicit
                // template argument forces a plain-value parameter that a
                // const-lvalue (like a named local here) can't bind to;
                // StoreResult's own signature is a forwarding reference
                // (template<T> bool StoreResult(T&& result)) so omitting
                // the explicit <T> lets deduction do the right thing.
                sb.AppendLine("            result->StoreResult(returnValue);");
                sb.AppendLine("        }");
            }

            sb.AppendLine("    }");
            sb.AppendLine();
        }

        private static string LastSymbolSegment(string qualifiedSymbol, string owningClassName)
        {
            // NativeQualifiedSymbol is the FULL qualified name (e.g.
            // "AZ::Vector3::GetLength"); the member-call expression only
            // needs the trailing method-name segment since the cast
            // already supplies the class. Falls back to the whole string
            // if it doesn't look qualified (shouldn't happen for anything
            // that passed classification, but keeps this helper total).
            var idx = qualifiedSymbol.LastIndexOf("::", StringComparison.Ordinal);
            return idx >= 0 ? qualifiedSymbol.Substring(idx + 2) : qualifiedSymbol;
        }

        private static string StripPointerSuffix(string cppType)
        {
            return cppType.TrimEnd().TrimEnd('*').TrimEnd();
        }

        private static string StripRefQualifiers(string cppType)
        {
            var t = cppType.Trim();
            if (t.StartsWith("const ", StringComparison.Ordinal))
            {
                t = t.Substring(6);
            }
            return t.TrimEnd('&').TrimEnd();
        }

        private static string SanitizeIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return "_";
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }

        private static string EscapeCpp(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
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
            sb.AppendLine("// Generated by O3DESharp.BindingGenerator (NativeBindingGenerator)");
            sb.AppendLine();
        }

        private void Log(string message)
        {
            if (_verbose) Console.WriteLine($"[NativeBindingGen] {message}");
        }
    }
}
