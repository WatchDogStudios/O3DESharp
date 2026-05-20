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
using ClangSharp;
using ClangSharp.Interop;

namespace O3DESharp.BindingGenerator.Parsing
{
    /// <summary>
    /// Parses C++ headers using ClangSharp to extract binding declarations
    /// </summary>
    public class O3DEHeaderParser
    {
        private readonly TypeMapper _typeMapper;
        private readonly bool _verbose;
        private readonly bool _requireExportAttribute;

        /// <summary>
        /// AzCore headers that libclang -include's before every parse, so
        /// the standard O3DE types (AZ::Vector3, AZ::Quaternion,
        /// AZ::Data::AssetId, AZ::EntityId, AZStd::string, ...) are
        /// always in scope - even for gem headers that take those types
        /// as parameters but don't include them transitively. Ordered so
        /// the EBus/Component foundation comes first (used by the math
        /// types via Crc/Uuid) and asset/string/container types follow.
        ///
        /// libclang resolves these names via the -I paths from
        /// BuildIncludePaths (which includes Code/Framework/AzCore), so
        /// they get found by the same probe mechanism gem headers use.
        /// Missing entries get a "file not found" warning but don't fail
        /// the parse - so adding more here is safe.
        /// </summary>
        private static readonly string[] AzCorePreludeHeaders = new[]
        {
            // EBus / Component foundation (most-included roots in AzCore;
            // pulling them first ensures everything downstream sees them)
            "AzCore/EBus/EBus.h",
            "AzCore/Component/ComponentBus.h",
            "AzCore/Component/EntityId.h",
            "AzCore/Component/Entity.h",
            "AzCore/Component/TransformBus.h",

            // Math types - the most frequent "no type named X in namespace AZ"
            "AzCore/Math/Vector2.h",
            "AzCore/Math/Vector3.h",
            "AzCore/Math/Vector4.h",
            "AzCore/Math/Quaternion.h",
            "AzCore/Math/Transform.h",
            "AzCore/Math/Color.h",
            "AzCore/Math/Matrix3x3.h",
            "AzCore/Math/Matrix4x4.h",
            "AzCore/Math/Aabb.h",
            "AzCore/Math/Crc.h",
            "AzCore/Math/Uuid.h",

            // Asset framework - AZ::Data::Asset, AZ::Data::AssetId
            "AzCore/Asset/AssetCommon.h",

            // String + container types - AZStd::string, AZStd::vector etc.
            "AzCore/std/string/string.h",
            "AzCore/std/string/string_view.h",
            "AzCore/std/containers/vector.h",
            "AzCore/std/containers/unordered_map.h",
            "AzCore/std/smart_ptr/shared_ptr.h",
            "AzCore/std/smart_ptr/intrusive_ptr.h",

            // Misc widely-used utilities
            "AzCore/RTTI/RTTI.h",
            "AzCore/Outcome/Outcome.h",
        };

        public O3DEHeaderParser(bool requireExportAttribute = false, bool verbose = false)
        {
            _typeMapper = new TypeMapper();
            _verbose = verbose;
            _requireExportAttribute = requireExportAttribute;
        }

        /// <summary>
        /// Parse C++ headers to extract binding declarations
        /// </summary>
        /// <param name="headerFiles">List of header files to parse</param>
        /// <param name="includePaths">Include directories for compilation</param>
        /// <param name="defines">Preprocessor defines</param>
        /// <param name="gemName">Name of the gem being parsed</param>
        /// <returns>Parsed binding declarations</returns>
        public ParsedBindings ParseHeaders(List<string> headerFiles, List<string> includePaths, List<string> defines, string gemName)
        {
            var bindings = new ParsedBindings { GemName = gemName };

            if (headerFiles.Count == 0)
            {
                // Unconditional - "no headers" is the kind of empty-output
                // case where silence makes the run look stuck.
                Console.WriteLine($"  [{gemName}] No header files to parse");
                return bindings;
            }

            // Unconditional header-count log so the editor's progress
            // view shows the upcoming work size for this gem (e.g.
            // EMotionFX with 247 headers is going to be slow, and the
            // user deserves to know that BEFORE they wait 3 minutes).
            Console.WriteLine($"  [{gemName}] Parsing {headerFiles.Count} header files...");

            // Build clang command line arguments
            var args = new List<string>();

            // Add include paths
            foreach (var includePath in includePaths)
            {
                args.Add($"-I{includePath}");
            }

            // Add defines
            foreach (var define in defines)
            {
                args.Add($"-D{define}");
            }

            // Add standard C++ flags
            args.Add("-std=c++20");
            args.Add("-xc++");
            args.Add("-Wno-pragma-once-outside-header");
            args.Add("-ferror-limit=0");        // Continue parsing despite errors
            args.Add("-Wno-everything");         // Suppress all warnings

            // MSVC-compatibility flags. Without these, libclang parses
            // Microsoft's standard-library headers (<limits>, <type_traits>,
            // <chrono>, ...) as if they were portable C++ - which fails
            // because MSVC's STL uses Microsoft-specific intrinsics,
            // attributes, and implicit-include conventions. The textbook
            // failure (seen in the EMotionFX log):
            //
            //   <MSVC>/include/limits:223:
            //     error: use of undeclared identifier 'CHAR_MIN'
            //
            // CHAR_MIN comes from <climits>; MSVC's <limits> normally
            // depends on transitive includes that libclang doesn't expand
            // the same way the MSVC frontend does. Setting the MSVC
            // compatibility flags + explicit target triple closes the gap.
            args.Add("-fms-extensions");
            args.Add("-fms-compatibility");
            // Pin the MSVC ABI version compiler-side. We don't have a way
            // to detect the user's MSVC version cheaply here, but 19.40
            // covers VS 2022 17.10 + VS 2026 - both ship MSVC 14.40+
            // headers that need this set. Older toolchains accept the
            // newer version as a superset.
            args.Add("-fms-compatibility-version=19.40");
            // The target triple tells libclang to look for MSVC's
            // built-in macros (_MSC_VER, _WIN32, etc.) and to use MSVC
            // calling conventions. Without it, headers gated on _MSC_VER
            // get parsed under the wrong code path.
            args.Add("--target=x86_64-pc-windows-msvc");

            // Pre-include the C-style headers that MSVC's C++ standard
            // headers depend on for compiler-intrinsic macros. This
            // matters before AzCorePreludeHeaders because the AzCore
            // headers themselves include <limits>, <chrono>, etc.
            // transitively - if those C++ headers fail to parse, all the
            // AzCore types they declare go missing too. <climits> covers
            // CHAR_MIN / INT_MAX / etc.; <cstddef> covers size_t /
            // ptrdiff_t; <cstdint> covers fixed-width integer types.
            args.Add("-include"); args.Add("climits");
            args.Add("-include"); args.Add("cstddef");
            args.Add("-include"); args.Add("cstdint");

            // Force-include common AzCore headers so types like AZ::Vector3,
            // AZ::Data::AssetId, AZ::EntityId, and the basic STL replacements
            // are in scope for every parse. Without this, headers that
            // "don't include what they use" (a common O3DE anti-pattern -
            // e.g. EMotionFX's MotionExtractionBus.h takes an AZ::Vector3
            // parameter while only including ComponentBus.h) fail with
            //   error: no type named 'Vector3' in namespace 'AZ'
            // and the affected class gets skipped with "no bindable members".
            //
            // libclang resolves these names through the same -I paths we
            // already added (BuildIncludePaths includes AzCore framework
            // dir), so we can just name them by the canonical AzCore include
            // path. Headers that ARE missing get silently skipped by the
            // OS file-not-found path, which is fine - they're advisory.
            foreach (var pre in AzCorePreludeHeaders)
            {
                args.Add("-include");
                args.Add(pre);
            }

            // Parse each header file. Unconditional [N/M] progress lines
            // (and elapsed timing per file) so the editor log shows a
            // moving cursor even with --verbose off. Without this, a
            // single slow header inside a 200-file gem looks like the
            // generator is wedged: there's no output between gem start
            // and gem complete, sometimes for minutes.
            var clangArgs = args.ToArray();
            int total = headerFiles.Count;
            int index = 0;
            var gemSw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var headerFile in headerFiles)
            {
                index++;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // Trim the path to just the filename for the progress
                // line so it's readable; verbose mode (gated below)
                // still prints the full path inside ParseHeaderFile.
                var shortName = System.IO.Path.GetFileName(headerFile);
                Console.WriteLine($"  [{gemName}] ({index}/{total}) {shortName}");
                try
                {
                    ParseHeaderFile(headerFile, clangArgs, bindings);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [{gemName}] Error parsing {headerFile}: {ex.Message}");
                    if (_verbose)
                    {
                        Console.WriteLine(ex.StackTrace);
                    }
                }
                sw.Stop();
                // Only print the timing line when a file was actually
                // slow (>500ms). Per-file noise for the common case
                // (<50ms libclang parses) would drown out the signal.
                if (sw.ElapsedMilliseconds > 500)
                {
                    Console.WriteLine($"  [{gemName}]   ^^ {sw.ElapsedMilliseconds} ms");
                }
            }
            gemSw.Stop();

            // Unconditional summary line - tells the user this gem's
            // parse step finished and how long it took. Code-gen is
            // typically fast after this, so "summary then Generated
            // bindings 1 sec later" is the normal cadence.
            Console.WriteLine(
                $"  [{gemName}] Parsed {bindings.Classes.Count} classes, " +
                $"{bindings.Functions.Count} functions, {bindings.Enums.Count} enums " +
                $"in {gemSw.Elapsed.TotalSeconds:F1}s");

            return bindings;
        }

        private unsafe void ParseHeaderFile(string headerFile, string[] clangArgs, ParsedBindings bindings)
        {
            if (!File.Exists(headerFile))
            {
                Console.WriteLine($"Warning: Header file not found: {headerFile}");
                return;
            }

            Log($"Parsing: {headerFile}");

            // Create a clang index
            using var index = CXIndex.Create();

            // Parse the translation unit
            var translationUnitError = CXTranslationUnit.TryParse(
                index,
                headerFile,
                clangArgs,
                Array.Empty<CXUnsavedFile>(),
                CXTranslationUnit_Flags.CXTranslationUnit_SkipFunctionBodies,
                out var translationUnit);

            if (translationUnitError != CXErrorCode.CXError_Success)
            {
                Console.WriteLine($"Failed to parse {headerFile}: {translationUnitError}");
                return;
            }

            using (translationUnit)
            {
                // Log diagnostics in verbose mode - but ONLY for errors
                // whose source location is the header we asked libclang
                // to parse. Diagnostics from MSVC's <limits>, <chrono>,
                // <type_traits>, etc., are noise: they're either things
                // libclang can't model the same way the MSVC frontend
                // does (compiler-intrinsic constexpr issues) or quirks
                // in MSVC's own STL headers that we have no way to fix
                // from outside. The parser recovers past them and still
                // finds the user's classes; printing them just makes
                // the log unscannable.
                //
                // We compare the diagnostic's source-file path against
                // the file we passed to TryParse. Cross-file errors
                // (in #included transitive headers from any framework)
                // get suppressed. Errors from the user's gem header
                // itself - which is what we actually care about - still
                // surface so a real syntax error in user code doesn't
                // hide silently.
                if (_verbose)
                {
                    var numDiags = translationUnit.NumDiagnostics;
                    int relevantErrors = 0;
                    int suppressedErrors = 0;
                    var headerFileNormalized = NormalizeSourceFile(headerFile);
                    for (uint i = 0; i < numDiags; i++)
                    {
                        using var diag = translationUnit.GetDiagnostic(i);
                        if (diag.Severity < CXDiagnosticSeverity.CXDiagnostic_Error)
                            continue;

                        var diagFile = ExtractDiagnosticFile(diag);
                        bool isInTargetFile =
                            !string.IsNullOrEmpty(diagFile) &&
                            string.Equals(
                                NormalizeSourceFile(diagFile),
                                headerFileNormalized,
                                StringComparison.OrdinalIgnoreCase);

                        if (!isInTargetFile)
                        {
                            suppressedErrors++;
                            continue;
                        }

                        relevantErrors++;
                        if (relevantErrors <= 5)
                        {
                            Log($"  Diag: {diag.Format(CXDiagnosticDisplayOptions.CXDiagnostic_DisplaySourceLocation).ToString()}");
                        }
                    }
                    if (relevantErrors > 5)
                    {
                        Log($"  ... and {relevantErrors - 5} more errors in this file");
                    }
                    if (relevantErrors > 0)
                    {
                        Log($"  Parse errors in {Path.GetFileName(headerFile)}: {relevantErrors}");
                    }
                    if (suppressedErrors > 0)
                    {
                        // Single-line summary instead of dumping each.
                        // Mostly these are MSVC STL quirks libclang
                        // recovers past; calling it out keeps the
                        // signal that they happened without flooding
                        // the log with the actual messages.
                        Log($"  ({suppressedErrors} cross-file diagnostics suppressed)");
                    }
                }

                var cursor = translationUnit.Cursor;

                // Visit all children of the translation unit
                cursor.VisitChildren((childCursor, parent, _) =>
                {
                    // Record every non-system header that contributed a top-level
                    // declaration to this parse. This captures transitive #includes
                    // (e.g. AzCore/Math/Vector3.h pulled in by the gem's own header)
                    // so BuildCache can invalidate when those upstream files change.
                    var fileLoc = childCursor.Location;
                    if (!fileLoc.IsInSystemHeader)
                    {
                        CXFile file;
                        uint line, column, offset;
                        clang.getFileLocation(fileLoc, (void**)&file, &line, &column, &offset);
                        if (file.Handle != IntPtr.Zero)
                        {
                            var fileName = file.Name.CString;
                            if (!string.IsNullOrEmpty(fileName))
                            {
                                bindings.SourceFiles.Add(System.IO.Path.GetFullPath(fileName));
                            }
                        }
                    }

                    // Only process declarations from the main file
                    if (!childCursor.Location.IsFromMainFile)
                    {
                        return CXChildVisitResult.CXChildVisit_Continue;
                    }

                    // Check if we require the export attribute
                    if (_requireExportAttribute && !HasExportAttribute(childCursor))
                    {
                        return CXChildVisitResult.CXChildVisit_Continue;
                    }

                    // Skip private/protected declarations (only export public API)
                    if (childCursor.CXXAccessSpecifier == CX_CXXAccessSpecifier.CX_CXXPrivate ||
                        childCursor.CXXAccessSpecifier == CX_CXXAccessSpecifier.CX_CXXProtected)
                    {
                        return CXChildVisitResult.CXChildVisit_Continue;
                    }

                    // Process based on cursor kind
                    switch (childCursor.Kind)
                    {
                        case CXCursorKind.CXCursor_ClassDecl:
                        case CXCursorKind.CXCursor_StructDecl:
                            ProcessClass(childCursor, bindings, headerFile);
                            break;

                        case CXCursorKind.CXCursor_FunctionDecl:
                            ProcessFunction(childCursor, bindings, headerFile);
                            break;

                        case CXCursorKind.CXCursor_EnumDecl:
                            ProcessEnum(childCursor, bindings, headerFile);
                            break;

                        case CXCursorKind.CXCursor_Namespace:
                            // Recurse into namespaces to find nested declarations
                            return CXChildVisitResult.CXChildVisit_Recurse;
                    }

                    return CXChildVisitResult.CXChildVisit_Continue;
                }, default(CXClientData));
            }
        }

        private unsafe bool HasExportAttribute(CXCursor cursor)
        {
            // Check for annotate attribute with "export_csharp"
            bool hasAttribute = false;

            cursor.VisitChildren((childCursor, parent, _) =>
            {
                if (childCursor.Kind == CXCursorKind.CXCursor_AnnotateAttr)
                {
                    var attrText = childCursor.Spelling.ToString();
                    // Match exact annotation text "export_csharp"
                    if (attrText.Equals("export_csharp", StringComparison.Ordinal))
                    {
                        hasAttribute = true;
                        return CXChildVisitResult.CXChildVisit_Break;
                    }
                }
                return CXChildVisitResult.CXChildVisit_Continue;
            }, default(CXClientData));

            return hasAttribute;
        }

        /// <summary>
        /// Method names that should be skipped (RTTI, allocator internals, etc.)
        /// </summary>
        private static readonly HashSet<string> SkippedMethodNames = new HashSet<string>(StringComparer.Ordinal)
        {
            // RTTI internals
            "TYPEINFO_Name", "TYPEINFO_Uuid", "TYPEINFO_Enable",
            "RTTI_GetType", "RTTI_IsTypeOf", "RTTI_IsContainType",
            "RTTI_AddressOf", "RTTI_EnumHierarchy", "RTTI_GetTypeName",
            // Allocator internals
            "AZ_CLASS_ALLOCATOR_Allocate", "AZ_CLASS_ALLOCATOR_DeAllocate",
            "CreateAzClassBase",
            // Component internals
            "GetProvidedServices", "GetIncompatibleServices",
            "GetRequiredServices", "GetDependentServices",
        };

        /// <summary>
        /// Method name prefixes that indicate un-bindable methods.
        /// </summary>
        private static readonly string[] SkippedMethodPrefixes = new[]
        {
            "operator", "~", "RTTI_", "TYPEINFO_", "AZ_CLASS_ALLOCATOR_",
        };

        /// <summary>
        /// Class name patterns that should be skipped entirely.
        /// </summary>
        private static readonly string[] SkippedClassPatterns = new[]
        {
            "AZStd::", "std::", "allocator", "iterator", "Iterator_VM",
        };

        /// <summary>
        /// Check if a method name should be skipped.
        /// </summary>
        private static bool ShouldSkipMethod(string methodName)
        {
            if (string.IsNullOrEmpty(methodName))
                return true;

            if (SkippedMethodNames.Contains(methodName))
                return true;

            foreach (var prefix in SkippedMethodPrefixes)
            {
                if (methodName.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Check if a class name should be skipped.
        /// </summary>
        private static bool ShouldSkipClass(string className)
        {
            if (string.IsNullOrEmpty(className))
                return true;

            // Skip template specializations
            if (className.Contains('<') || className.Contains('>'))
                return true;

            // Skip known internal/container types
            foreach (var pattern in SkippedClassPatterns)
            {
                if (className.StartsWith(pattern, StringComparison.Ordinal) ||
                    className.Contains("::" + pattern))
                    return true;
            }

            // Skip editor-only types. The host-side header file filter
            // (MultiGemBindingGenerator.IsEditorOnlyHeader) drops editor
            // headers BEFORE the parser ever sees them, but Editor*
            // classes can also be declared inline in non-editor headers
            // (one bus header containing both runtime AND editor variants
            // is a real pattern in O3DE - e.g. ActorComponentBus.h
            // declaring EditorActorComponentRequests alongside the
            // runtime ActorComponentRequests). This catches those.
            //
            // Match against the LAST segment of the fully-qualified name
            // so namespaces like "Foo::Editor::Bar" don't false-positive.
            var lastSeg = className;
            var lastColon = className.LastIndexOf("::", StringComparison.Ordinal);
            if (lastColon >= 0 && lastColon + 2 < className.Length)
            {
                lastSeg = className.Substring(lastColon + 2);
            }
            if (lastSeg.StartsWith("Editor", StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private unsafe void ProcessClass(CXCursor cursor, ParsedBindings bindings, string sourceFile)
        {
            var className = cursor.Spelling.ToString();

            // Skip classes with invalid/un-bindable names
            if (ShouldSkipClass(className))
            {
                Log($"  Skipping class: {className} (filtered)");
                return;
            }

            // Skip forward declarations (incomplete types with no definition)
            if (!cursor.IsDefinition)
            {
                return;
            }

            var parsedClass = new ParsedClass
            {
                Name = className,
                QualifiedName = cursor.CXXRecordDecl_GetQualifiedName(),
                Namespace = GetNamespace(cursor),
                Documentation = GetDocumentation(cursor),
                SourceFile = sourceFile
            };

            // Find base class
            cursor.VisitChildren((childCursor, parent, _) =>
            {
                if (childCursor.Kind == CXCursorKind.CXCursor_CXXBaseSpecifier)
                {
                    parsedClass.BaseClass = childCursor.Type.Declaration.Spelling.ToString();
                }
                return CXChildVisitResult.CXChildVisit_Continue;
            }, default(CXClientData));

            // Track method signatures to deduplicate const/non-const overloads.
            // In C++, `Foo()` and `Foo() const` are distinct overloads, but in C#
            // there's no const qualifier on methods so they'd produce duplicate definitions.
            var seenMethodSignatures = new HashSet<string>(StringComparer.Ordinal);

            // Process members
            cursor.VisitChildren((childCursor, parent, _) =>
            {
                // Skip private/protected members in classes
                if (childCursor.CXXAccessSpecifier == CX_CXXAccessSpecifier.CX_CXXPrivate ||
                    childCursor.CXXAccessSpecifier == CX_CXXAccessSpecifier.CX_CXXProtected)
                {
                    return CXChildVisitResult.CXChildVisit_Continue;
                }

                // Check export attribute if required
                bool shouldExport = !_requireExportAttribute || HasExportAttribute(childCursor);

                if (childCursor.Kind == CXCursorKind.CXCursor_CXXMethod && shouldExport)
                {
                    // Skip destructors
                    if (childCursor.Kind == CXCursorKind.CXCursor_Destructor)
                    {
                        return CXChildVisitResult.CXChildVisit_Continue;
                    }

                    var methodName = childCursor.Spelling.ToString();

                    // Filter out un-bindable methods (operators, RTTI, allocators, etc.)
                    if (ShouldSkipMethod(methodName))
                    {
                        return CXChildVisitResult.CXChildVisit_Continue;
                    }

                    // Skip pure virtual methods (= 0) — they have no implementation to call
                    if (childCursor.CXXMethod_IsPureVirtual)
                    {
                        return CXChildVisitResult.CXChildVisit_Continue;
                    }

                    var method = ProcessMethod(childCursor);

                    // Skip methods whose return type or parameter types contain un-mappable C++ types
                    if (!IsBindableMethod(method))
                    {
                        return CXChildVisitResult.CXChildVisit_Continue;
                    }

                    // Deduplicate: build a signature key from method name + C# parameter types.
                    // This prevents const/non-const overloads from producing duplicate C# methods.
                    var signatureKey = GetMethodSignatureKey(method);
                    if (!seenMethodSignatures.Add(signatureKey))
                    {
                        return CXChildVisitResult.CXChildVisit_Continue;
                    }

                    parsedClass.Methods.Add(method);
                }
                else if (childCursor.Kind == CXCursorKind.CXCursor_FieldDecl && shouldExport)
                {
                    var property = ProcessProperty(childCursor);

                    // Skip fields with un-mappable types
                    if (!IsBindableType(property.Type))
                    {
                        return CXChildVisitResult.CXChildVisit_Continue;
                    }

                    parsedClass.Properties.Add(property);
                }

                return CXChildVisitResult.CXChildVisit_Continue;
            }, default(CXClientData));

            // Only add class if it has usable public methods or properties
            if (parsedClass.Methods.Count > 0 || parsedClass.Properties.Count > 0)
            {
                bindings.Classes.Add(parsedClass);
                Log($"  Found class: {parsedClass.QualifiedName} with {parsedClass.Methods.Count} methods, {parsedClass.Properties.Count} properties");
            }
            else
            {
                Log($"  Skipping class: {parsedClass.QualifiedName} (no bindable members)");
            }
        }

        /// <summary>
        /// Check whether a parsed method can be meaningfully bound in C#.
        /// Rejects methods with C++ types that have no C# equivalent.
        /// </summary>
        private static bool IsBindableMethod(ParsedMethod method)
        {
            if (!IsBindableType(method.ReturnType))
                return false;

            foreach (var param in method.Parameters)
            {
                if (!IsBindableType(param.Type))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Build a signature key for deduplication: "MethodName(ParamType1,ParamType2,...)".
        /// This ignores const qualifiers and return types, matching C# overload resolution rules.
        /// </summary>
        private static string GetMethodSignatureKey(ParsedMethod method)
        {
            var paramTypes = string.Join(",", method.Parameters.Select(p => p.Type.CSharpTypeName));
            return $"{method.Name}({paramTypes})";
        }

        /// <summary>
        /// Check if a parsed type can be represented in C#.
        /// </summary>
        private static bool IsBindableType(ParsedType type)
        {
            var csType = type.CSharpTypeName;

            // Reject types that are clearly C++ internal types leaked through
            if (string.IsNullOrEmpty(csType))
                return false;

            // These are C++ types with no sensible C# mapping
            var unbindableTypes = new HashSet<string>(StringComparer.Ordinal)
            {
                "align_val_t", "nothrow_t", "type_info",
                "va_list", "__va_list_tag",
            };

            if (unbindableTypes.Contains(csType))
                return false;

            // Skip types containing C++ syntax that leaked through
            if (csType.Contains("::") || csType.Contains("<") || csType.Contains(">"))
                return false;

            return true;
        }

        private ParsedMethod ProcessMethod(CXCursor cursor)
        {
            var method = new ParsedMethod
            {
                Name = cursor.Spelling.ToString(),
                ReturnType = _typeMapper.MapType(cursor.ResultType.Spelling.ToString()),
                IsStatic = cursor.IsStatic,
                IsConst = cursor.CXXMethod_IsConst,
                Documentation = GetDocumentation(cursor)
            };

            // Process parameters
            for (uint i = 0; i < cursor.NumArguments; i++)
            {
                var paramCursor = cursor.GetArgument(i);
                var parameter = new ParsedParameter
                {
                    Name = paramCursor.Spelling.ToString(),
                    Type = _typeMapper.MapType(paramCursor.Type.Spelling.ToString())
                };

                method.Parameters.Add(parameter);
            }

            return method;
        }

        private ParsedProperty ProcessProperty(CXCursor cursor)
        {
            return new ParsedProperty
            {
                Name = cursor.Spelling.ToString(),
                Type = _typeMapper.MapType(cursor.Type.Spelling.ToString()),
                IsReadOnly = cursor.Type.IsConstQualified,
                Documentation = GetDocumentation(cursor)
            };
        }

        private void ProcessFunction(CXCursor cursor, ParsedBindings bindings, string sourceFile)
        {
            var function = new ParsedFunction
            {
                Name = cursor.Spelling.ToString(),
                QualifiedName = cursor.Mangling.ToString(),
                Namespace = GetNamespace(cursor),
                ReturnType = _typeMapper.MapType(cursor.ResultType.Spelling.ToString()),
                Documentation = GetDocumentation(cursor),
                SourceFile = sourceFile
            };

            // Process parameters
            for (uint i = 0; i < cursor.NumArguments; i++)
            {
                var paramCursor = cursor.GetArgument(i);
                var parameter = new ParsedParameter
                {
                    Name = paramCursor.Spelling.ToString(),
                    Type = _typeMapper.MapType(paramCursor.Type.Spelling.ToString())
                };

                function.Parameters.Add(parameter);
            }

            bindings.Functions.Add(function);
            Log($"  Found function: {function.Name}");
        }

        private unsafe void ProcessEnum(CXCursor cursor, ParsedBindings bindings, string sourceFile)
        {
            var parsedEnum = new ParsedEnum
            {
                Name = cursor.Spelling.ToString(),
                QualifiedName = cursor.EnumDecl_GetQualifiedName(),
                Namespace = GetNamespace(cursor),
                UnderlyingType = cursor.Type.Spelling.ToString(),
                Documentation = GetDocumentation(cursor),
                SourceFile = sourceFile
            };

            // Process enum values
            cursor.VisitChildren((childCursor, parent, _) =>
            {
                if (childCursor.Kind == CXCursorKind.CXCursor_EnumConstantDecl)
                {
                    var enumValue = new ParsedEnumValue
                    {
                        Name = childCursor.Spelling.ToString(),
                        Value = childCursor.EnumConstantDeclValue,
                        Documentation = GetDocumentation(childCursor)
                    };
                    parsedEnum.Values.Add(enumValue);
                }
                return CXChildVisitResult.CXChildVisit_Continue;
            }, default(CXClientData));

            bindings.Enums.Add(parsedEnum);
            Log($"  Found enum: {parsedEnum.Name} with {parsedEnum.Values.Count} values");
        }

        private string GetNamespace(CXCursor cursor)
        {
            var namespaces = new List<string>();
            var parent = cursor.SemanticParent;

            while (parent.Kind == CXCursorKind.CXCursor_Namespace)
            {
                namespaces.Insert(0, parent.Spelling.ToString());
                parent = parent.SemanticParent;
            }

            return string.Join("::", namespaces);
        }

        private string? GetDocumentation(CXCursor cursor)
        {
            var comment = cursor.BriefCommentText;
            return string.IsNullOrEmpty(comment.ToString()) ? null : comment.ToString();
        }

        private void Log(string message)
        {
            if (_verbose)
            {
                Console.WriteLine($"[Parser] {message}");
            }
        }

        /// <summary>
        /// Extract the source-file path from a libclang diagnostic, or
        /// empty string if the diagnostic has no file location (some
        /// diagnostics come from command-line arg processing, not a
        /// specific file). Mirrors the source-location extraction
        /// pattern used in the cursor walk above.
        /// </summary>
        private static unsafe string ExtractDiagnosticFile(CXDiagnostic diag)
        {
            try
            {
                var loc = diag.Location;
                CXFile file;
                uint line, column, offset;
                clang.getFileLocation(loc, (void**)&file, &line, &column, &offset);
                if (file.Handle == IntPtr.Zero)
                {
                    return string.Empty;
                }
                return file.Name.CString ?? string.Empty;
            }
            catch
            {
                // libclang can throw on malformed diagnostics in error-
                // recovery contexts; treat as "no file" so the caller
                // suppresses rather than crashing.
                return string.Empty;
            }
        }

        /// <summary>
        /// Normalize a source-file path for case-insensitive comparison.
        /// Used to match a diagnostic's source file against the header
        /// we asked libclang to parse - we want to keep diagnostics from
        /// that file, suppress everything else (MSVC STL, AzCore, etc.).
        /// </summary>
        private static string NormalizeSourceFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try
            {
                return Path.GetFullPath(path).Replace('\\', '/');
            }
            catch
            {
                return path.Replace('\\', '/');
            }
        }
    }

    // Extension methods for ClangSharp
    internal static class ClangSharpExtensions
    {
        public static string CXXRecordDecl_GetQualifiedName(this CXCursor cursor)
        {
            return cursor.Type.Spelling.ToString();
        }

        public static string EnumDecl_GetQualifiedName(this CXCursor cursor)
        {
            return cursor.Type.Spelling.ToString();
        }
    }
}
