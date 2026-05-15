/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System.Collections.Generic;

namespace O3DESharp.BindingGenerator.Parsing
{
    /// <summary>
    /// Maps C++ types to C# types for binding generation
    /// </summary>
    public class TypeMapper
    {
        private readonly Dictionary<string, string> _typeMap;
        private static readonly System.Text.RegularExpressions.Regex WhitespaceRegex =
            new System.Text.RegularExpressions.Regex(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Set of C# type names that are known blittable value types safe for unmanaged interop.
        /// Types in this set can be used directly in delegate* unmanaged signatures.
        /// </summary>
        private static readonly HashSet<string> KnownBlittableTypes = new HashSet<string>(System.StringComparer.Ordinal)
        {
            // C# built-in primitive types
            "void", "bool", "byte", "sbyte", "short", "ushort", "int", "uint",
            "long", "ulong", "float", "double", "char", "nint", "nuint",
            // System types
            "IntPtr", "Guid",
            // O3DE.Core math types (StructLayout.Sequential structs)
            "Vector2", "Vector3", "Quaternion",
        };

        public TypeMapper()
        {
            _typeMap = new Dictionary<string, string>
            {
                // Basic types
                ["void"] = "void",
                ["bool"] = "bool",
                ["char"] = "byte",
                ["signed char"] = "sbyte",
                ["unsigned char"] = "byte",
                ["short"] = "short",
                ["unsigned short"] = "ushort",
                ["int"] = "int",
                ["unsigned int"] = "uint",
                ["long"] = "int",
                ["unsigned long"] = "uint",
                ["long long"] = "long",
                ["unsigned long long"] = "ulong",
                ["float"] = "float",
                ["double"] = "double",
                ["size_t"] = "nuint",
                ["int8_t"] = "sbyte",
                ["uint8_t"] = "byte",
                ["int16_t"] = "short",
                ["uint16_t"] = "ushort",
                ["int32_t"] = "int",
                ["uint32_t"] = "uint",
                ["int64_t"] = "long",
                ["uint64_t"] = "ulong",

                // String types — mapped to IntPtr because unmanaged delegates (delegate* unmanaged<>)
                // cannot use managed 'string' type. Users must marshal via Marshal.PtrToStringAnsi() etc.
                ["const char*"] = "IntPtr",
                ["char*"] = "IntPtr",
                ["AZStd::string"] = "IntPtr",
                ["AZStd::string_view"] = "IntPtr",

                // O3DE math types that have matching C# structs in O3DE.Core
                ["AZ::Vector2"] = "Vector2",
                ["AZ::Vector3"] = "Vector3",
                ["AZ::Quaternion"] = "Quaternion",

                // O3DE math types that don't have C# struct equivalents yet → IntPtr
                // AZ::Transform is a 3x4 matrix with complex layout, not the same as O3DE.Core Transform class
                ["AZ::Transform"] = "IntPtr",
                ["AZ::Vector4"] = "IntPtr",
                ["AZ::Matrix3x3"] = "IntPtr",
                ["AZ::Matrix3x4"] = "IntPtr",
                ["AZ::Matrix4x4"] = "IntPtr",
                ["AZ::Color"] = "IntPtr",
                ["AZ::Aabb"] = "IntPtr",
                ["AZ::Obb"] = "IntPtr",
                ["AZ::Plane"] = "IntPtr",

                // O3DE core types
                ["AZ::EntityId"] = "ulong",
                ["AZ::ComponentId"] = "ulong",
                ["AZ::Uuid"] = "Guid",
                ["AZ::Crc32"] = "uint",
                ["AZ::TypeId"] = "Guid",
                ["AZ::IO::PathView"] = "IntPtr",
                ["AZ::IO::Path"] = "IntPtr",
                ["AZ::IO::FixedMaxPath"] = "IntPtr",
                ["AZ::Name"] = "IntPtr",
                ["AZ::Data::AssetId"] = "IntPtr",

                // O3DE Atom/RPI opaque pointer types → all IntPtr
                // These are typically shared_ptr or similar smart pointers in C++
                ["AZ::RPI::ViewPtr"] = "IntPtr",
                ["AZ::RPI::ViewportContextPtr"] = "IntPtr",
                ["AZ::RPI::ScenePtr"] = "IntPtr",
                ["AZ::RPI::RenderPipelinePtr"] = "IntPtr",
                ["AZ::RPI::ShaderResourceGroupPtr"] = "IntPtr",

                // Additional sized types that Clang may emit
                ["std::size_t"] = "nuint",
                ["std::ptrdiff_t"] = "nint",
                ["std::intptr_t"] = "nint",
                ["std::uintptr_t"] = "nuint",
                ["ptrdiff_t"] = "nint",
                ["intptr_t"] = "nint",
                ["uintptr_t"] = "nuint",
                ["ssize_t"] = "nint",
                ["wchar_t"] = "char",

                // O3DE sized type aliases
                ["AZ::u8"] = "byte",
                ["AZ::u16"] = "ushort",
                ["AZ::u32"] = "uint",
                ["AZ::u64"] = "ulong",
                ["AZ::s8"] = "sbyte",
                ["AZ::s16"] = "short",
                ["AZ::s32"] = "int",
                ["AZ::s64"] = "long",

                // Coral interop types
                ["Coral::NativeString"] = "NativeString",
                ["Coral::Bool32"] = "Bool32",
            };
        }

        /// <summary>
        /// Map a C++ type to a C# type
        /// </summary>
        /// <param name="cppType">C++ type name</param>
        /// <returns>Parsed type information</returns>
        public ParsedType MapType(string cppType)
        {
            var parsedType = new ParsedType
            {
                CppTypeName = cppType
            };

            // Normalize the type (remove extra spaces)
            var normalizedType = NormalizeType(cppType);

            // Check for const
            if (normalizedType.StartsWith("const "))
            {
                parsedType.IsConst = true;
                normalizedType = normalizedType.Substring(6).Trim();
            }

            // Check for pointer
            if (normalizedType.EndsWith("*"))
            {
                parsedType.IsPointer = true;
                normalizedType = normalizedType.Substring(0, normalizedType.Length - 1).Trim();
            }

            // Check for reference
            if (normalizedType.EndsWith("&"))
            {
                parsedType.IsReference = true;
                normalizedType = normalizedType.Substring(0, normalizedType.Length - 1).Trim();
            }

            // Remove const again if it appears after pointer/reference
            if (normalizedType.EndsWith("const"))
            {
                parsedType.IsConst = true;
                normalizedType = normalizedType.Substring(0, normalizedType.Length - 5).Trim();
            }

            // Strip template arguments for lookup (e.g., AZStd::shared_ptr<AZ::RPI::View> → AZStd::shared_ptr)
            // But first try the full type with templates
            if (_typeMap.TryGetValue(normalizedType, out var csType))
            {
                parsedType.CSharpTypeName = csType;
            }
            else
            {
                // Try stripping template arguments
                var templateIdx = normalizedType.IndexOf('<');
                if (templateIdx >= 0)
                {
                    var baseType = normalizedType.Substring(0, templateIdx).Trim();
                    // Smart pointers and containers → IntPtr
                    if (IsSmartPointerOrContainer(baseType))
                    {
                        parsedType.CSharpTypeName = "IntPtr";
                        parsedType.RequiresMarshaling = true;
                    }
                    else if (_typeMap.TryGetValue(baseType, out var baseCs))
                    {
                        parsedType.CSharpTypeName = baseCs;
                    }
                    else
                    {
                        // Unknown templated type → IntPtr
                        parsedType.CSharpTypeName = "IntPtr";
                        parsedType.RequiresMarshaling = true;
                    }
                }
                else
                {
                    // Unknown non-templated type → IntPtr
                    // At the interop boundary, all C++ objects are passed by pointer/handle
                    parsedType.CSharpTypeName = "IntPtr";
                    parsedType.RequiresMarshaling = true;
                }
            }

            // Pointers always degrade to IntPtr at the interop boundary.
            // The user can manually marshal back via Marshal.PtrToStructure /
            // friends if they need the typed view.
            if (parsedType.IsPointer)
            {
                parsedType.CSharpTypeName = "IntPtr";
            }

            // References to non-blittable types still degrade to IntPtr - we
            // can't ref-pass something whose layout C# doesn't understand.
            // But for blittable types we KEEP the typed name plus the
            // IsReference flag so CSharpCodeGenerator can emit a proper
            // 'ref T' / 'in T' wrapper signature and pass the address to
            // the InternalCalls delegate. The previous behavior collapsed
            // 'AZ::Vector3&' to 'Vector3' (value), silently dropping the
            // by-reference semantics - a real ABI mismatch.
            if (parsedType.IsReference && !IsBlittableType(parsedType.CSharpTypeName))
            {
                parsedType.CSharpTypeName = "IntPtr";
            }

            return parsedType;
        }

        /// <summary>
        /// Check if a C# type name is a known blittable value type.
        /// </summary>
        public static bool IsBlittableType(string csType)
        {
            return KnownBlittableTypes.Contains(csType);
        }

        /// <summary>
        /// Add a custom type mapping
        /// </summary>
        /// <param name="cppType">C++ type name</param>
        /// <param name="csharpType">C# type name</param>
        public void AddMapping(string cppType, string csharpType)
        {
            _typeMap[cppType] = csharpType;
        }

        /// <summary>
        /// Check if a type is a known O3DE interop type
        /// </summary>
        /// <param name="cppType">C++ type name</param>
        /// <returns>True if this is a known interop type</returns>
        public bool IsInteropType(string cppType)
        {
            var normalized = NormalizeType(cppType);
            return normalized.StartsWith("Coral::") || _typeMap.ContainsKey(normalized);
        }

        private string NormalizeType(string type)
        {
            // Remove extra whitespace
            return WhitespaceRegex.Replace(type.Trim(), " ");
        }

        /// <summary>
        /// Check if a C++ base type is a smart pointer or container type.
        /// These are all represented as IntPtr in C# interop.
        /// </summary>
        private static bool IsSmartPointerOrContainer(string baseType)
        {
            // AZStd smart pointers and containers
            if (baseType.StartsWith("AZStd::") || baseType.StartsWith("std::"))
            {
                return true;
            }

            // Common pointer/handle suffixes
            if (baseType.EndsWith("Ptr") || baseType.EndsWith("Handle") || baseType.EndsWith("Ref"))
            {
                return true;
            }

            return false;
        }
    }
}
