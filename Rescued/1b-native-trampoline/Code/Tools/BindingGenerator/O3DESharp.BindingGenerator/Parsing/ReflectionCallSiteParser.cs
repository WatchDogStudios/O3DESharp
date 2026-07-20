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
using ClangSharp;
using ClangSharp.Interop;

namespace O3DESharp.BindingGenerator.Parsing
{
    /// <summary>
    /// One recovered reflection call-site: a single
    /// <c>-&gt;Method("Name", &amp;C::Symbol, ...)</c> (or Property/Event/
    /// Constructor) expression found in a gem's Reflect() implementation.
    /// </summary>
    public sealed class ReflectionCallSite
    {
        /// <summary>
        /// "Method", "Property", "Event", or "Constructor" - the reflection
        /// builder method name that was called (AZ::BehaviorContext::
        /// ClassBuilder::Method / Property / EBusBuilder::Event / etc. all
        /// share this fluent-chain shape; see BehaviorContext.h /
        /// BehaviorClassBuilder.inl).
        /// </summary>
        public string ReflectionKind { get; set; } = string.Empty;

        /// <summary>
        /// The reflected (script-visible) name - the first string-literal
        /// argument to the call, e.g. "GetLength". This is what
        /// BehaviorContextReflector/ManifestMethodEntry.reflectedName also
        /// captures at runtime; it is the join key back to the C++-side
        /// manifest (see ReflectionCallSiteParser class doc for why this,
        /// not the decl name, has to be the key).
        /// </summary>
        public string ReflectedName { get; set; } = string.Empty;

        /// <summary>
        /// The owning class as spelled at the call site's enclosing
        /// Reflect() scope, e.g. "Vector3". Empty for EBus-global /
        /// non-class reflection blocks (BehaviorContext::Method(...) at
        /// namespace scope, or EBus builder chains where the class is the
        /// bus itself rather than a C++ type - those still populate
        /// EnclosingBusName instead).
        /// </summary>
        public string OwningClassName { get; set; } = string.Empty;

        /// <summary>
        /// Populated instead of/alongside OwningClassName when this call
        /// site is inside an EBus<...>::Behavior chain (i.e. reflects an
        /// EBus event, not a class method). The 1B v1 classifier always
        /// marks these NonBindableReason::EBusAddressedById OR (for
        /// broadcast events) simply doesn't attempt to bind them - see
        /// NativeBindingGenerator.cs's classifier - but the parser still
        /// records them for census/coverage-report purposes (spec §8 R7).
        /// </summary>
        public string EnclosingBusName { get; set; } = string.Empty;

        /// <summary>
        /// The recovered qualified C++ symbol from the
        /// <c>&amp;C::Method</c> argument, e.g. "AZ::Vector3::GetLength".
        /// Empty if the second argument to Method(...)/Event(...) was NOT
        /// a plain address-of-member-or-free-function expression (a
        /// lambda, a bound AZStd::function, a macro-expanded wrapper,
        /// etc.) - see ClassifyMethodArgument. An empty symbol here is
        /// the parser-side signal that feeds
        /// NonBindableReason::ReflectedViaLambda.
        /// </summary>
        public string NativeQualifiedSymbol { get; set; } = string.Empty;

        /// <summary>
        /// True only when the second argument was recognized as exactly
        /// <c>&amp;Qualified::Name</c> (a UnaryOperator '&amp;' over a
        /// DeclRefExpr / MemberExpr naming a function or method). False
        /// for every other shape (lambda, functor object, overload-
        /// disambiguating cast, nullptr, etc.) - those are exactly the
        /// "reflected via lambda/wrapper" non-bindable case from the spec.
        /// </summary>
        public bool IsPlainMemberPointer { get; set; }

        /// <summary>
        /// True if the reflection call-site chain this belongs to also
        /// contains an ->Overload(...)/m_overload marker OR the same
        /// ReflectedName occurs more than once for the same
        /// OwningClassName within one gem's parsed call sites (arity/type
        /// overloading reflected as repeat ->Method() calls with the same
        /// script name - the common O3DE pattern for exposing C++
        /// overloads to script under one name). The join step
        /// (NativeBindingGenerator.BindingClassifier) is what actually
        /// aggregates duplicates across the whole parse and sets this;
        /// the parser leaves it false and only records raw call sites.
        /// </summary>
        public bool LooksOverloaded { get; set; }

        /// <summary>Source file this call site was found in (for diagnostics).</summary>
        public string SourceFile { get; set; } = string.Empty;

        /// <summary>1-based source line (for diagnostics / stable sorting).</summary>
        public uint SourceLine { get; set; }
    }

    /// <summary>
    /// Container for every reflection call site recovered from one gem's
    /// reflection .cpp files.
    /// </summary>
    public sealed class ReflectionCallSiteResult
    {
        public string GemName { get; set; } = string.Empty;
        public List<ReflectionCallSite> CallSites { get; } = new List<ReflectionCallSite>();
    }

    /// <summary>
    /// Parses a gem's REFLECTION .cpp FILES (not headers - see
    /// O3DEHeaderParser, which only ever visits header ClassDecl/
    /// FunctionDecl and never opens a .cpp or visits a CallExpr) to
    /// recover the native C++ symbol backing each BehaviorContext
    /// reflection entry.
    ///
    /// THIS IS THE PIECE THE 1B SPEC IDENTIFIES AS MISSING (§3 point 2 /
    /// §6.1): AZ::BehaviorContext stores no native symbol at runtime -
    /// BehaviorMethodImpl's functor captures a raw fn-pointer inside a
    /// type-erased AZStd::function, and only the reflected (script) name
    /// is queryable from a live BehaviorContext (BehaviorMethod has no
    /// "give me back the &C::Method you were constructed from" accessor).
    /// The ONLY place the qualified symbol still exists as readable text
    /// is the call-site source itself:
    ///
    ///     behaviorContext->Class&lt;Vector3&gt;("Vector3")
    ///         ->Method("GetLength", &amp;Vector3::GetLength, ...)
    ///
    /// This parser walks that .cpp with libclang, looking for CallExpr
    /// nodes whose callee is named Method/Property/Event/Constructor
    /// (BehaviorContext::ClassBuilder's fluent-chain methods - see
    /// AzCore/RTTI/BehaviorContext.h / BehaviorClassBuilder.inl), and
    /// extracts:
    ///   - the first string-literal argument (the reflected/script name)
    ///   - the second argument, IF it is a plain `&Qualified::Symbol`
    ///     UnaryOperator('&') over a DeclRefExpr/MemberExpr - the
    ///     qualified name of THAT decl is the native symbol.
    ///
    /// WHY THE JOIN KEY IS THE REFLECTED NAME, NOT THE C++ DECL NAME:
    /// the reflected script-name legitimately diverges from the C++
    /// symbol (`->Method("ScriptFriendlyName", &amp;C::InternalCppName)`
    /// is a real, supported O3DE pattern - see spec §3 point 2). Since
    /// BehaviorContextReflector (the runtime C++ side) can only ever see
    /// the reflected name, and THIS parser is the only place that can see
    /// both the reflected name AND the C++ symbol simultaneously (they're
    /// adjacent arguments in the same call expression), the join must
    /// happen HERE at parse time by keying on (owning class spelling,
    /// reflected name) exactly as they appear in this one call site - not
    /// as a later cross-reference between two independently-produced
    /// lists. NativeBindingGenerator.cs's join against the C++-side
    /// manifest then only has to match strings, not re-derive anything.
    ///
    /// SCOPE / LIMITATIONS (v1, matches the spec's "conservative
    /// classifier"):
    ///   - Only recognizes literal `&Class::Method` / `&FreeFunction`
    ///     as the reflected callable. Lambdas
    ///     (`->Method("X", []{...})`), `AZStd::function` wrappers, and
    ///     macro-expanded indirections (AZ_EBUS_BEHAVIOR_BINDER-style
    ///     thunks) are recorded with IsPlainMemberPointer=false and an
    ///     empty NativeQualifiedSymbol - the generator's classifier turns
    ///     that into NonBindableReason::ReflectedViaLambda.
    ///   - Does not attempt overload resolution/disambiguation casts
    ///     (`static_cast<Ret(Class::*)(Args)>(&Class::Method)`) in v1;
    ///     these are detected (the cast wraps a nested UnaryOperator '&')
    ///     but treated the same as "found a symbol" for the base name -
    ///     actual overload safety is enforced downstream by
    ///     LooksOverloaded / the generator's per-class duplicate-name
    ///     scan, which is a stronger and simpler signal than trying to
    ///     parse the cast's parameter-type-list text.
    ///   - Only visits .cpp translation units named on the CALLER'S
    ///     "reflection sources" list (see ParseReflectionSources) - unlike
    ///     O3DEHeaderParser this does NOT walk a gem's entire header set,
    ///     since Reflect() implementations live in .cpp files by O3DE
    ///     convention and re-parsing every header as a full TU here would
    ///     duplicate O3DEHeaderParser's work for no benefit.
    /// </summary>
    public class ReflectionCallSiteParser
    {
        private readonly bool _verbose;

        /// <summary>
        /// Reflection builder-chain method names we look for as the
        /// CallExpr's callee. Mirrors AZ::BehaviorContext's
        /// ClassBuilder/EBusBuilder/GlobalMethodBuilder fluent API
        /// surface (Method, Property, Constructor, Event) - see
        /// AzCore/RTTI/BehaviorContext.h. "Attribute" is deliberately
        /// excluded: it doesn't reflect a callable.
        /// </summary>
        private static readonly HashSet<string> ReflectionBuilderMethodNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Method", "Property", "Constructor", "Event",
        };

        public ReflectionCallSiteParser(bool verbose = false)
        {
            _verbose = verbose;
        }

        /// <summary>
        /// Parse a gem's reflection .cpp files and recover call sites.
        /// </summary>
        /// <param name="reflectionCppFiles">
        /// .cpp files known (by convention/config - see
        /// BindingConfig's ReflectionSources list, or a naming heuristic
        /// such as "*Component.cpp"/"*System.cpp" containing a
        /// ReflectContext-taking Reflect method) to contain
        /// BehaviorContext reflection calls.
        /// </param>
        public ReflectionCallSiteResult ParseReflectionSources(
            List<string> reflectionCppFiles, List<string> includePaths, List<string> defines, string gemName)
        {
            var result = new ReflectionCallSiteResult { GemName = gemName };

            if (reflectionCppFiles.Count == 0)
            {
                Console.WriteLine($"  [{gemName}] No reflection .cpp files to parse for call-site recovery");
                return result;
            }

            Console.WriteLine($"  [{gemName}] Recovering native symbols from {reflectionCppFiles.Count} reflection .cpp file(s)...");

            var args = new List<string>();
            foreach (var includePath in includePaths) args.Add($"-I{includePath}");
            foreach (var define in defines) args.Add($"-D{define}");
            args.Add("-std=c++20");
            args.Add("-xc++");
            args.Add("-Wno-pragma-once-outside-header");
            args.Add("-ferror-limit=0");
            args.Add("-Wno-everything");
            args.Add("-fms-extensions");
            args.Add("-fms-compatibility");
            args.Add("-fms-compatibility-version=19.40");
            args.Add("--target=x86_64-pc-windows-msvc");
            args.Add("-include"); args.Add("climits");
            args.Add("-include"); args.Add("cstddef");
            args.Add("-include"); args.Add("cstdint");
            // Unlike O3DEHeaderParser we deliberately do NOT pass
            // CXTranslationUnit_SkipFunctionBodies below - the whole
            // point of this parser is to walk INSIDE Reflect()'s
            // function body to find the Method(...)/Event(...) call
            // expressions, which SkipFunctionBodies would elide.
            var clangArgs = args.ToArray();

            foreach (var cppFile in reflectionCppFiles)
            {
                try
                {
                    ParseOneFile(cppFile, clangArgs, result);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [{gemName}] Error recovering call sites from {cppFile}: {ex.Message}");
                    if (_verbose) Console.WriteLine(ex.StackTrace);
                }
            }

            Console.WriteLine($"  [{gemName}] Recovered {result.CallSites.Count} reflection call site(s) " +
                $"({CountWithSymbol(result)} with a resolved native symbol)");

            return result;
        }

        private static int CountWithSymbol(ReflectionCallSiteResult result)
        {
            int n = 0;
            foreach (var cs in result.CallSites)
            {
                if (cs.IsPlainMemberPointer) n++;
            }
            return n;
        }

        private unsafe void ParseOneFile(string cppFile, string[] clangArgs, ReflectionCallSiteResult result)
        {
            if (!File.Exists(cppFile))
            {
                Console.WriteLine($"Warning: Reflection source not found: {cppFile}");
                return;
            }

            using var index = CXIndex.Create();
            var error = CXTranslationUnit.TryParse(
                index, cppFile, clangArgs, Array.Empty<CXUnsavedFile>(),
                CXTranslationUnit_Flags.CXTranslationUnit_None, // keep function bodies
                out var tu);

            if (error != CXErrorCode.CXError_Success)
            {
                Console.WriteLine($"Failed to parse {cppFile}: {error}");
                return;
            }

            using (tu)
            {
                // Track the nearest enclosing "reflection scope" name as we
                // descend: for chains hung off `behaviorContext->Class<T>("Name")`
                // or `EBus<TBus>::Behavior(...)`, the class/bus template
                // argument or string literal identifies the owner. We recover
                // it heuristically by remembering the last
                // Class<T>(...)/Behavior<T>(...) CallExpr's template argument
                // seen on the way down to each Method/Property/Event call -
                // libclang doesn't give us a direct "which chain root does
                // this belong to" query, so we track it via a simple
                // depth-first stack keyed on statement nesting.
                var scopeStack = new List<(string ClassName, string BusName)>();

                void Walk(CXCursor cursor)
                {
                    // Detect a "root" of a reflection chain: a CallExpr whose
                    // callee is a MemberExpr/templated-member named "Class"
                    // (AZ::BehaviorContext::Class<T>) or whose callee is
                    // "Behavior" on an EBus<T> specialization. Push the
                    // recovered owner name for the duration of visiting this
                    // subtree (the whole fluent ->Method()->Method()->...
                    // chain is syntactically nested inside the root CallExpr
                    // as its callee expression, so a pre-order push / post-
                    // order pop around VisitChildren captures exactly the
                    // call sites that belong to this scope).
                    bool pushed = false;

                    if (cursor.Kind == CXCursorKind.CXCursor_CallExpr)
                    {
                        var calleeName = GetCallExprMethodName(cursor);
                        if (calleeName == "Class")
                        {
                            var className = GetFirstTemplateArgTypeName(cursor) ?? GetFirstStringLiteralArg(cursor);
                            if (!string.IsNullOrEmpty(className))
                            {
                                scopeStack.Add((className!, string.Empty));
                                pushed = true;
                            }
                        }
                        else if (calleeName == "Behavior" || calleeName == "Handler")
                        {
                            var busName = GetFirstTemplateArgTypeName(cursor);
                            if (!string.IsNullOrEmpty(busName))
                            {
                                scopeStack.Add((string.Empty, busName!));
                                pushed = true;
                            }
                        }
                        else if (calleeName != null && ReflectionBuilderMethodNames.Contains(calleeName))
                        {
                            var site = ExtractCallSite(cursor, calleeName, cppFile);
                            if (site != null)
                            {
                                if (scopeStack.Count > 0)
                                {
                                    var (cn, bn) = scopeStack[scopeStack.Count - 1];
                                    site.OwningClassName = cn;
                                    site.EnclosingBusName = bn;
                                }
                                result.CallSites.Add(site);
                            }
                        }
                    }

                    cursor.VisitChildren((child, parent, clientData) =>
                    {
                        Walk(child);
                        return CXChildVisitResult.CXChildVisit_Continue;
                    }, default(CXClientData));

                    if (pushed)
                    {
                        scopeStack.RemoveAt(scopeStack.Count - 1);
                    }
                }

                Walk(tu.Cursor);

                // Second pass: mark same-class/same-name duplicates as
                // LooksOverloaded. This is a same-gem, same-parse-batch
                // heuristic only (the manifest-level classifier in
                // NativeBindingGenerator.cs does the authoritative,
                // cross-gem check against the live BehaviorContext's
                // m_overload chain) - it exists so a single-file quick
                // scan already reports a reasonable answer without
                // waiting for the join.
                var seen = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var site in result.CallSites)
                {
                    var key = site.OwningClassName + "::" + site.ReflectedName;
                    seen.TryGetValue(key, out var count);
                    seen[key] = count + 1;
                }
                foreach (var site in result.CallSites)
                {
                    var key = site.OwningClassName + "::" + site.ReflectedName;
                    if (seen.TryGetValue(key, out var count) && count > 1)
                    {
                        site.LooksOverloaded = true;
                    }
                }
            }
        }

        /// <summary>
        /// Extract a single Method/Property/Event/Constructor call site:
        /// the reflected name (first string-literal argument) and the
        /// native symbol (second argument, if a plain &amp;Symbol form).
        /// </summary>
        private unsafe ReflectionCallSite? ExtractCallSite(CXCursor callExpr, string kind, string sourceFile)
        {
            var args = GetCallArgs(callExpr);
            if (args.Count == 0)
            {
                return null;
            }

            var site = new ReflectionCallSite
            {
                ReflectionKind = kind,
                SourceFile = sourceFile,
                SourceLine = GetLine(callExpr),
            };

            // First argument: the reflected/script name string literal.
            // Constructor(...) has no name argument (it's always
            // positional args only) - falls back to "Constructor" as a
            // synthetic reflected name, matching
            // BehaviorContextReflector::ReflectClass's
            // "Constructor_%d"-style synthesis for the runtime side (kept
            // simple here; the join in NativeBindingGenerator.cs handles
            // constructors as a special case rather than joining on name).
            if (kind == "Constructor")
            {
                site.ReflectedName = "Constructor";
            }
            else
            {
                var nameArg = args[0];
                var literal = GetStringLiteralValue(nameArg);
                if (literal == null)
                {
                    // Not a literal string - can't safely recover a stable
                    // reflected name (e.g. a computed/format string). Skip;
                    // this call site simply won't appear in the manifest
                    // join and any matching runtime entry stays
                    // NonBindableReason::NoNativeSideCounterpart, the
                    // conservative default.
                    return null;
                }
                site.ReflectedName = literal;
            }

            // Second argument (first for Constructor): the &Class::Method
            // (or &FreeFunction) expression.
            int symbolArgIndex = kind == "Constructor" ? 0 : 1;
            if (args.Count > symbolArgIndex)
            {
                var (symbol, isPlain) = ClassifyMethodArgument(args[symbolArgIndex]);
                site.NativeQualifiedSymbol = symbol ?? string.Empty;
                site.IsPlainMemberPointer = isPlain;
            }

            return site;
        }

        /// <summary>
        /// Determine whether a reflection call's callable argument is a
        /// plain <c>&amp;Qualified::Symbol</c> expression (UnaryOperator
        /// '&amp;' wrapping a DeclRefExpr/MemberExpr/OverloadExpr that
        /// names a function or method), and if so, return its fully-
        /// qualified spelling. Anything else (a lambda's CXXConstructExpr/
        /// LambdaExpr, a call to construct an AZStd::function, a
        /// non-address expression) returns (null, false) - the "reflected
        /// via lambda/wrapper" case.
        /// </summary>
        private static unsafe (string? symbol, bool isPlain) ClassifyMethodArgument(CXCursor argExpr)
        {
            var inner = UnwrapImplicitAndParens(argExpr);

            // static_cast<...>(&Class::Method) overload-disambiguation
            // form: unwrap the cast to look at what's underneath, but we
            // do NOT attempt to parse the cast's target type here (see
            // class doc's Scope/Limitations) - LooksOverloaded is the
            // signal for this case, not a parsed parameter list.
            if (inner.Kind == CXCursorKind.CXCursor_CStyleCastExpr ||
                inner.Kind == CXCursorKind.CXCursor_UnexposedExpr)
            {
                // CXCursor_UnexposedExpr is libclang's catch-all for
                // implicit casts / functional-style casts that don't have
                // a dedicated cursor kind; recurse into its single child
                // if present.
                var child = FirstChild(inner);
                if (child.HasValue)
                {
                    inner = UnwrapImplicitAndParens(child.Value);
                }
            }

            if (inner.Kind == CXCursorKind.CXCursor_UnaryOperator)
            {
                // Confirm the operator is address-of ('&'). libclang's
                // CXCursor doesn't expose the operator token directly in
                // the stable API without tokenizing, so we check via the
                // cursor's spelling range's first token as a best-effort;
                // if tokenization is unavailable we still proceed (nearly
                // every UnaryOperator wrapping a DeclRefExpr/MemberExpr in
                // this position IS address-of - reflection call sites
                // don't pass `*ptr` or `-value` as callables) but flag it
                // less confidently by still requiring the child to be a
                // reference to a function/method decl below.
                var child = FirstChild(inner);
                if (child.HasValue)
                {
                    var target = UnwrapImplicitAndParens(child.Value);
                    var qualifiedName = TryGetReferencedFunctionQualifiedName(target);
                    if (qualifiedName != null)
                    {
                        return (qualifiedName, true);
                    }
                }
                return (null, false);
            }

            // Free functions can also be reflected as a bare function
            // name (no explicit '&') in some O3DE call sites - the
            // implicit function-to-pointer decay means there's no
            // UnaryOperator node at all, just a DeclRefExpr directly.
            {
                var qualifiedName = TryGetReferencedFunctionQualifiedName(inner);
                if (qualifiedName != null)
                {
                    return (qualifiedName, true);
                }
            }

            return (null, false);
        }

        private static unsafe string? TryGetReferencedFunctionQualifiedName(CXCursor cursor)
        {
            // DeclRefExpr (free function) or MemberExpr / unexposed member-
            // pointer forms (member function). In both cases the
            // referenced cursor's own USR/qualified spelling gives us the
            // qualified name; clang.getCursorReferenced resolves through
            // to the actual FunctionDecl/CXXMethodDecl.
            if (cursor.Kind != CXCursorKind.CXCursor_DeclRefExpr &&
                cursor.Kind != CXCursorKind.CXCursor_MemberRefExpr &&
                cursor.Kind != CXCursorKind.CXCursor_OverloadedDeclRef &&
                cursor.Kind != CXCursorKind.CXCursor_UnexposedExpr)
            {
                return null;
            }

            var referenced = clang.getCursorReferenced(cursor);
            if (referenced.Kind == CXCursorKind.CXCursor_FunctionDecl ||
                referenced.Kind == CXCursorKind.CXCursor_CXXMethod ||
                referenced.Kind == CXCursorKind.CXCursor_Constructor)
            {
                return BuildQualifiedName(referenced);
            }

            // OverloadedDeclRef (name resolves to more than one candidate
            // at this point in the AST, e.g. before overload resolution
            // has picked one) - can't get a single qualified name back
            // reliably. This case flows into LooksOverloaded via the
            // duplicate-name scan instead.
            return null;
        }

        private static string BuildQualifiedName(CXCursor decl)
        {
            var parts = new List<string> { decl.Spelling.ToString() };
            var parent = decl.SemanticParent;
            while (parent.Kind == CXCursorKind.CXCursor_ClassDecl ||
                   parent.Kind == CXCursorKind.CXCursor_StructDecl ||
                   parent.Kind == CXCursorKind.CXCursor_Namespace ||
                   parent.Kind == CXCursorKind.CXCursor_ClassTemplate)
            {
                var name = parent.Spelling.ToString();
                if (!string.IsNullOrEmpty(name))
                {
                    parts.Insert(0, name);
                }
                parent = parent.SemanticParent;
            }
            return string.Join("::", parts);
        }

        private static unsafe CXCursor UnwrapImplicitAndParens(CXCursor cursor)
        {
            var current = cursor;
            // ImplicitCastExpr / ParenExpr both show up as
            // CXCursor_UnexposedExpr in libclang's stable cursor kinds
            // most of the time; peel through single-child wrappers until
            // we hit something with either 0 or >1 children, or a kind we
            // recognize as terminal.
            for (int i = 0; i < 8; i++) // bounded - reflection call sites are not deeply nested
            {
                if (current.Kind == CXCursorKind.CXCursor_UnexposedExpr ||
                    current.Kind == CXCursorKind.CXCursor_ParenExpr)
                {
                    var child = FirstChild(current);
                    if (child.HasValue)
                    {
                        current = child.Value;
                        continue;
                    }
                }
                break;
            }
            return current;
        }

        private static unsafe CXCursor? FirstChild(CXCursor cursor)
        {
            CXCursor? result = null;
            cursor.VisitChildren((child, parent, clientData) =>
            {
                result = child;
                return CXChildVisitResult.CXChildVisit_Break;
            }, default(CXClientData));
            return result;
        }

        private static unsafe List<CXCursor> GetCallArgs(CXCursor callExpr)
        {
            // CXCursor_CallExpr's children are: [calleeExpr, arg0, arg1, ...]
            // for a plain call, or just [arg0, arg1, ...] with the callee
            // folded into CXCursor_MemberRefExpr for `->Method(a, b)`
            // chains (the "callee" is itself the MemberRefExpr child, and
            // its OWN children are the object expression - i.e. the rest
            // of the chain - so we can't just skip "child 0" blindly).
            // We instead collect every child, then drop the first child
            // IF it is a callee-shaped node (MemberRefExpr / DeclRefExpr /
            // UnexposedExpr wrapping either) - everything else is a real
            // argument.
            var children = new List<CXCursor>();
            callExpr.VisitChildren((child, parent, clientData) =>
            {
                children.Add(child);
                return CXChildVisitResult.CXChildVisit_Continue;
            }, default(CXClientData));

            if (children.Count == 0)
            {
                return children;
            }

            var first = UnwrapImplicitAndParens(children[0]);
            bool firstIsCallee =
                first.Kind == CXCursorKind.CXCursor_MemberRefExpr ||
                first.Kind == CXCursorKind.CXCursor_DeclRefExpr ||
                first.Kind == CXCursorKind.CXCursor_UnexposedExpr;

            return firstIsCallee ? children.GetRange(1, children.Count - 1) : children;
        }

        private static unsafe string? GetCallExprMethodName(CXCursor callExpr)
        {
            var children = new List<CXCursor>();
            callExpr.VisitChildren((child, parent, clientData) =>
            {
                children.Add(child);
                return CXChildVisitResult.CXChildVisit_Break; // only need the first (callee)
            }, default(CXClientData));

            if (children.Count == 0)
            {
                return null;
            }

            var callee = UnwrapImplicitAndParens(children[0]);
            if (callee.Kind == CXCursorKind.CXCursor_MemberRefExpr)
            {
                return callee.Spelling.ToString();
            }
            if (callee.Kind == CXCursorKind.CXCursor_DeclRefExpr)
            {
                return callee.Spelling.ToString();
            }
            return null;
        }

        private static unsafe string? GetFirstTemplateArgTypeName(CXCursor callExpr)
        {
            // For Class<T>("Name") / EBus<TBus>::Behavior(...), the
            // template argument type is attached to the callee's
            // ReferencedType; walk the callee's DeclRefExpr and read its
            // type's template-argument-0 spelling. libclang's stable API
            // exposes this through clang.Type_getNumTemplateArguments /
            // Type_getTemplateArgumentAsType on the callee cursor's type
            // when available; fall back to null (caller then tries the
            // string-literal heuristic instead) since not every clang
            // build exposes template-argument introspection uniformly.
            try
            {
                var children = new List<CXCursor>();
                callExpr.VisitChildren((child, parent, clientData) =>
                {
                    children.Add(child);
                    return CXChildVisitResult.CXChildVisit_Break;
                }, default(CXClientData));
                if (children.Count == 0) return null;

                var callee = UnwrapImplicitAndParens(children[0]);
                var referenced = clang.getCursorReferenced(callee);
                var type = referenced.Type;
                if (type.NumTemplateArguments > 0)
                {
                    var argType = clang.Type_getTemplateArgumentAsType(type, 0);
                    var name = argType.Spelling.ToString();
                    return string.IsNullOrEmpty(name) ? null : name;
                }
            }
            catch
            {
                // Best-effort only - see method doc.
            }
            return null;
        }

        private static unsafe string? GetFirstStringLiteralArg(CXCursor callExpr)
        {
            var args = GetCallArgs(callExpr);
            return args.Count > 0 ? GetStringLiteralValue(args[0]) : null;
        }

        private static unsafe string? GetStringLiteralValue(CXCursor cursor)
        {
            var inner = UnwrapImplicitAndParens(cursor);
            if (inner.Kind == CXCursorKind.CXCursor_StringLiteral)
            {
                var spelling = inner.Spelling.ToString();
                // libclang's Spelling for a StringLiteral is the raw
                // source token including quotes, e.g. "\"GetLength\"" -
                // strip the surrounding quotes.
                if (spelling.Length >= 2 && spelling[0] == '"' && spelling[^1] == '"')
                {
                    return spelling.Substring(1, spelling.Length - 2);
                }
                return spelling;
            }
            return null;
        }

        private static unsafe uint GetLine(CXCursor cursor)
        {
            var loc = cursor.Location;
            CXFile file;
            uint line, column, offset;
            clang.getFileLocation(loc, (void**)&file, &line, &column, &offset);
            return line;
        }
    }
}
