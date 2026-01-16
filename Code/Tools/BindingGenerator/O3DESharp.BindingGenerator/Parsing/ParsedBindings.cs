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
    /// Container for all parsed binding declarations from C++ headers
    /// </summary>
    public class ParsedBindings
    {
        /// <summary>
        /// Gem this binding belongs to
        /// </summary>
        public string GemName { get; set; } = string.Empty;

        /// <summary>
        /// Parsed class declarations
        /// </summary>
        public List<ParsedClass> Classes { get; set; } = new List<ParsedClass>();

        /// <summary>
        /// Parsed function declarations
        /// </summary>
        public List<ParsedFunction> Functions { get; set; } = new List<ParsedFunction>();

        /// <summary>
        /// Parsed enum declarations
        /// </summary>
        public List<ParsedEnum> Enums { get; set; } = new List<ParsedEnum>();
    }

    /// <summary>
    /// Represents a parsed C++ class
    /// </summary>
    public class ParsedClass
    {
        /// <summary>
        /// C++ class name
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Fully qualified C++ name (with namespace)
        /// </summary>
        public string QualifiedName { get; set; } = string.Empty;

        /// <summary>
        /// C++ namespace
        /// </summary>
        public string Namespace { get; set; } = string.Empty;

        /// <summary>
        /// XML documentation comment
        /// </summary>
        public string? Documentation { get; set; }

        /// <summary>
        /// Methods to export
        /// </summary>
        public List<ParsedMethod> Methods { get; set; } = new List<ParsedMethod>();

        /// <summary>
        /// Properties/fields to export
        /// </summary>
        public List<ParsedProperty> Properties { get; set; } = new List<ParsedProperty>();

        /// <summary>
        /// Base class name (if any)
        /// </summary>
        public string? BaseClass { get; set; }

        /// <summary>
        /// Source file where this class was declared
        /// </summary>
        public string SourceFile { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents a parsed C++ method
    /// </summary>
    public class ParsedMethod
    {
        /// <summary>
        /// Method name
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Return type
        /// </summary>
        public ParsedType ReturnType { get; set; } = new ParsedType();

        /// <summary>
        /// Method parameters
        /// </summary>
        public List<ParsedParameter> Parameters { get; set; } = new List<ParsedParameter>();

        /// <summary>
        /// Whether this is a static method
        /// </summary>
        public bool IsStatic { get; set; }

        /// <summary>
        /// Whether this is a const method
        /// </summary>
        public bool IsConst { get; set; }

        /// <summary>
        /// XML documentation comment
        /// </summary>
        public string? Documentation { get; set; }
    }

    /// <summary>
    /// Represents a parsed C++ property/field
    /// </summary>
    public class ParsedProperty
    {
        /// <summary>
        /// Property name
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Property type
        /// </summary>
        public ParsedType Type { get; set; } = new ParsedType();

        /// <summary>
        /// Whether this property is read-only
        /// </summary>
        public bool IsReadOnly { get; set; }

        /// <summary>
        /// XML documentation comment
        /// </summary>
        public string? Documentation { get; set; }
    }

    /// <summary>
    /// Represents a standalone parsed function
    /// </summary>
    public class ParsedFunction
    {
        /// <summary>
        /// Function name
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Fully qualified name (with namespace)
        /// </summary>
        public string QualifiedName { get; set; } = string.Empty;

        /// <summary>
        /// C++ namespace
        /// </summary>
        public string Namespace { get; set; } = string.Empty;

        /// <summary>
        /// Return type
        /// </summary>
        public ParsedType ReturnType { get; set; } = new ParsedType();

        /// <summary>
        /// Function parameters
        /// </summary>
        public List<ParsedParameter> Parameters { get; set; } = new List<ParsedParameter>();

        /// <summary>
        /// XML documentation comment
        /// </summary>
        public string? Documentation { get; set; }

        /// <summary>
        /// Source file where this function was declared
        /// </summary>
        public string SourceFile { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents a parsed C++ enum
    /// </summary>
    public class ParsedEnum
    {
        /// <summary>
        /// Enum name
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Fully qualified name (with namespace)
        /// </summary>
        public string QualifiedName { get; set; } = string.Empty;

        /// <summary>
        /// C++ namespace
        /// </summary>
        public string Namespace { get; set; } = string.Empty;

        /// <summary>
        /// Underlying integer type
        /// </summary>
        public string UnderlyingType { get; set; } = "int";

        /// <summary>
        /// Enum values
        /// </summary>
        public List<ParsedEnumValue> Values { get; set; } = new List<ParsedEnumValue>();

        /// <summary>
        /// XML documentation comment
        /// </summary>
        public string? Documentation { get; set; }

        /// <summary>
        /// Source file where this enum was declared
        /// </summary>
        public string SourceFile { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents a parsed enum value
    /// </summary>
    public class ParsedEnumValue
    {
        /// <summary>
        /// Enum value name
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Enum value (if explicitly set)
        /// </summary>
        public long? Value { get; set; }

        /// <summary>
        /// XML documentation comment
        /// </summary>
        public string? Documentation { get; set; }
    }

    /// <summary>
    /// Represents a method/function parameter
    /// </summary>
    public class ParsedParameter
    {
        /// <summary>
        /// Parameter name
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Parameter type
        /// </summary>
        public ParsedType Type { get; set; } = new ParsedType();

        /// <summary>
        /// Default value (if any)
        /// </summary>
        public string? DefaultValue { get; set; }
    }

    /// <summary>
    /// Represents a C++ type
    /// </summary>
    public class ParsedType
    {
        /// <summary>
        /// Original C++ type name
        /// </summary>
        public string CppTypeName { get; set; } = string.Empty;

        /// <summary>
        /// Mapped C# type name
        /// </summary>
        public string CSharpTypeName { get; set; } = string.Empty;

        /// <summary>
        /// Whether this is a pointer type
        /// </summary>
        public bool IsPointer { get; set; }

        /// <summary>
        /// Whether this is a reference type
        /// </summary>
        public bool IsReference { get; set; }

        /// <summary>
        /// Whether this is a const type
        /// </summary>
        public bool IsConst { get; set; }

        /// <summary>
        /// Whether this type requires marshaling
        /// </summary>
        public bool RequiresMarshaling { get; set; }
    }
}
