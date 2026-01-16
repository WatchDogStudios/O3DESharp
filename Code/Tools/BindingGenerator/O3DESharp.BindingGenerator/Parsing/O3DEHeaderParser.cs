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
                Log("No header files to parse");
                return bindings;
            }

            Log($"Parsing {headerFiles.Count} header files for gem '{gemName}'");

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

            // Parse each header file
            foreach (var headerFile in headerFiles)
            {
                try
                {
                    ParseHeaderFile(headerFile, args.ToArray(), bindings);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error parsing {headerFile}: {ex.Message}");
                    if (_verbose)
                    {
                        Console.WriteLine(ex.StackTrace);
                    }
                }
            }

            Log($"Parsed {bindings.Classes.Count} classes, {bindings.Functions.Count} functions, {bindings.Enums.Count} enums");

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
                var cursor = translationUnit.Cursor;

                // Visit all children of the translation unit
                cursor.VisitChildren((childCursor, parent, _) =>
                {
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

        private unsafe void ProcessClass(CXCursor cursor, ParsedBindings bindings, string sourceFile)
        {
            var parsedClass = new ParsedClass
            {
                Name = cursor.Spelling.ToString(),
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
                    var method = ProcessMethod(childCursor);
                    parsedClass.Methods.Add(method);
                }
                else if (childCursor.Kind == CXCursorKind.CXCursor_FieldDecl && shouldExport)
                {
                    var property = ProcessProperty(childCursor);
                    parsedClass.Properties.Add(property);
                }

                return CXChildVisitResult.CXChildVisit_Continue;
            }, default(CXClientData));

            bindings.Classes.Add(parsedClass);
            Log($"  Found class: {parsedClass.QualifiedName} with {parsedClass.Methods.Count} methods");
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
