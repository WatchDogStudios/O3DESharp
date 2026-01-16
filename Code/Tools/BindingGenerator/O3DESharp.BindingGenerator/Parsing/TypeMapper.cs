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

                // String types
                ["const char*"] = "string",
                ["char*"] = "string",
                ["AZStd::string"] = "string",
                ["AZStd::string_view"] = "string",

                // O3DE math types
                ["AZ::Vector2"] = "Vector2",
                ["AZ::Vector3"] = "Vector3",
                ["AZ::Vector4"] = "Vector4",
                ["AZ::Quaternion"] = "Quaternion",
                ["AZ::Matrix3x3"] = "Matrix3x3",
                ["AZ::Matrix4x4"] = "Matrix4x4",
                ["AZ::Transform"] = "Transform",
                ["AZ::Color"] = "Color",
                ["AZ::Aabb"] = "Aabb",

                // O3DE core types
                ["AZ::EntityId"] = "ulong",
                ["AZ::ComponentId"] = "ulong",
                ["AZ::Uuid"] = "Guid",
                ["AZ::Crc32"] = "uint",

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

            // Map the base type
            if (_typeMap.TryGetValue(normalizedType, out var csType))
            {
                parsedType.CSharpTypeName = csType;
            }
            else
            {
                // Unknown type - use the C++ name as-is and mark for marshaling
                parsedType.CSharpTypeName = SimplifyTypeName(normalizedType);
                parsedType.RequiresMarshaling = true;
            }

            // Adjust for pointers/references
            if (parsedType.IsPointer && parsedType.CSharpTypeName != "string")
            {
                parsedType.CSharpTypeName = "IntPtr"; // Use IntPtr for unknown pointer types
            }

            return parsedType;
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
            return System.Text.RegularExpressions.Regex.Replace(type.Trim(), @"\s+", " ");
        }

        private string SimplifyTypeName(string qualifiedName)
        {
            // Remove namespace qualifiers for simpler names
            // e.g., "AZ::MyClass" -> "MyClass"
            var lastColonIndex = qualifiedName.LastIndexOf("::");
            if (lastColonIndex >= 0)
            {
                return qualifiedName.Substring(lastColonIndex + 2);
            }
            return qualifiedName;
        }
    }
}
