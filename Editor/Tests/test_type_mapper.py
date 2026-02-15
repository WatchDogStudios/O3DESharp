#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""
Unit tests for TypeMapper class and type mapping utilities.

Tests C++ to C# type mapping including AZ types, primitives, and special cases.
"""

import pytest

from csharp_binding_generator import (
    TypeMapper,
    TYPE_MAPPINGS,
    escape_identifier,
    sanitize_namespace,
    CSHARP_KEYWORDS,
)


@pytest.mark.unit
class TestTypeMapper:
    """Test suite for TypeMapper class."""

    def test_map_basic_primitives(self):
        """Test mapping of basic C++ primitive types."""
        mapper = TypeMapper()
        
        assert mapper.map_type("int") == "int"
        assert mapper.map_type("float") == "float"
        assert mapper.map_type("double") == "double"
        assert mapper.map_type("bool") == "bool"
        assert mapper.map_type("void") == "void"

    def test_map_sized_integers(self):
        """Test mapping of sized integer types."""
        mapper = TypeMapper()
        
        assert mapper.map_type("int8") == "sbyte"
        assert mapper.map_type("int16") == "short"
        assert mapper.map_type("int32") == "int"
        assert mapper.map_type("int64") == "long"
        assert mapper.map_type("uint8") == "byte"
        assert mapper.map_type("uint16") == "ushort"
        assert mapper.map_type("uint32") == "uint"
        assert mapper.map_type("uint64") == "ulong"

    def test_map_az_types(self):
        """Test mapping of AZ:: namespace types."""
        mapper = TypeMapper()
        
        assert mapper.map_type("AZ::Vector3") == "Vector3"
        assert mapper.map_type("AZ::Vector2") == "Vector2"
        assert mapper.map_type("AZ::Vector4") == "Vector4"
        assert mapper.map_type("AZ::Quaternion") == "Quaternion"
        assert mapper.map_type("AZ::Transform") == "Transform"
        assert mapper.map_type("AZ::Color") == "Color"
        assert mapper.map_type("AZ::EntityId") == "ulong"

    def test_map_az_sized_integers(self):
        """Test mapping of AZ:: sized integer types."""
        mapper = TypeMapper()
        
        assert mapper.map_type("AZ::u8") == "byte"
        assert mapper.map_type("AZ::u16") == "ushort"
        assert mapper.map_type("AZ::u32") == "uint"
        assert mapper.map_type("AZ::u64") == "ulong"
        assert mapper.map_type("AZ::s8") == "sbyte"
        assert mapper.map_type("AZ::s16") == "short"
        assert mapper.map_type("AZ::s32") == "int"
        assert mapper.map_type("AZ::s64") == "long"

    def test_map_string_types(self):
        """Test mapping of string types."""
        mapper = TypeMapper()
        
        assert mapper.map_type("string") == "string"
        assert mapper.map_type("AZStd::string") == "string"

    def test_map_const_types(self):
        """Test mapping of const-qualified types."""
        mapper = TypeMapper()
        
        assert mapper.map_type("const int") == "int"
        assert mapper.map_type("const AZ::Vector3") == "Vector3"
        assert mapper.map_type("const float") == "float"

    def test_map_pointer_types(self):
        """Test mapping of pointer types."""
        mapper = TypeMapper()
        
        # Pointers are stripped for mapping
        assert mapper.map_type("int*") == "int"
        assert mapper.map_type("AZ::Vector3*") == "Vector3"
        assert mapper.map_type("const AZ::Vector3*") == "Vector3"

    def test_map_reference_types(self):
        """Test mapping of reference types."""
        mapper = TypeMapper()
        
        # References are stripped for mapping
        assert mapper.map_type("int&") == "int"
        assert mapper.map_type("const AZ::Vector3&") == "Vector3"
        assert mapper.map_type("float&") == "float"

    def test_map_complex_qualifiers(self):
        """Test mapping types with complex qualifiers."""
        mapper = TypeMapper()
        
        assert mapper.map_type("const AZ::Vector3* const&") == "Vector3"
        assert mapper.map_type("const int* const") == "int"

    def test_map_unknown_type(self):
        """Test mapping of unknown types (should pass through)."""
        mapper = TypeMapper()
        
        assert mapper.map_type("UnknownType") == "UnknownType"
        assert mapper.map_type("MyCustomClass") == "MyCustomClass"

    def test_custom_mappings(self):
        """Test TypeMapper with custom type mappings."""
        custom_mappings = {
            "MyType": "MyCSharpType",
            "CustomVector": "Vector3"
        }
        mapper = TypeMapper(custom_mappings)
        
        assert mapper.map_type("MyType") == "MyCSharpType"
        assert mapper.map_type("CustomVector") == "Vector3"
        # Should still have default mappings
        assert mapper.map_type("int") == "int"

    def test_is_primitive_basic_types(self):
        """Test is_primitive for basic types."""
        mapper = TypeMapper()
        
        assert mapper.is_primitive("int")
        assert mapper.is_primitive("float")
        assert mapper.is_primitive("double")
        assert mapper.is_primitive("bool")
        assert mapper.is_primitive("string")
        assert mapper.is_primitive("byte")
        assert mapper.is_primitive("sbyte")

    def test_is_primitive_complex_types(self):
        """Test is_primitive for complex types."""
        mapper = TypeMapper()
        
        assert not mapper.is_primitive("Vector3")
        assert not mapper.is_primitive("Quaternion")
        assert not mapper.is_primitive("Transform")
        assert not mapper.is_primitive("MyCustomClass")

    def test_needs_marshalling_primitives(self):
        """Test needs_marshalling for primitive types."""
        mapper = TypeMapper()
        
        assert not mapper.needs_marshalling("int")
        assert not mapper.needs_marshalling("float")
        assert not mapper.needs_marshalling("double")
        assert not mapper.needs_marshalling("bool")

    def test_needs_marshalling_pointers(self):
        """Test needs_marshalling for pointer types."""
        mapper = TypeMapper()
        
        assert mapper.needs_marshalling("int*")
        assert mapper.needs_marshalling("float*")
        assert mapper.needs_marshalling("AZ::Vector3*")

    def test_needs_marshalling_references(self):
        """Test needs_marshalling for reference types."""
        mapper = TypeMapper()
        
        assert mapper.needs_marshalling("int&")
        assert mapper.needs_marshalling("const AZ::Vector3&")

    def test_needs_marshalling_complex_types(self):
        """Test needs_marshalling for complex types."""
        mapper = TypeMapper()
        
        assert mapper.needs_marshalling("AZ::Vector3")
        assert mapper.needs_marshalling("AZ::Quaternion")
        assert mapper.needs_marshalling("UnknownType")


@pytest.mark.unit
class TestEscapeIdentifier:
    """Test suite for escape_identifier function."""

    def test_escape_csharp_keywords(self):
        """Test escaping of C# keywords."""
        assert escape_identifier("class") == "@class"
        assert escape_identifier("namespace") == "@namespace"
        assert escape_identifier("using") == "@using"
        assert escape_identifier("event") == "@event"
        assert escape_identifier("string") == "@string"

    def test_no_escape_regular_identifiers(self):
        """Test that regular identifiers are not escaped."""
        assert escape_identifier("MyClass") == "MyClass"
        assert escape_identifier("position") == "position"
        assert escape_identifier("GetValue") == "GetValue"

    def test_case_sensitivity(self):
        """Test that keyword matching is case-insensitive."""
        assert escape_identifier("Class") == "@Class"
        assert escape_identifier("NAMESPACE") == "@NAMESPACE"

    def test_all_keywords_in_set(self):
        """Test a sample of keywords from CSHARP_KEYWORDS."""
        keywords_to_test = ["abstract", "bool", "byte", "class", "const", 
                           "delegate", "double", "enum", "float", "int",
                           "interface", "null", "object", "string", "void"]
        
        for keyword in keywords_to_test:
            result = escape_identifier(keyword)
            assert result == f"@{keyword}"


@pytest.mark.unit
class TestSanitizeNamespace:
    """Test suite for sanitize_namespace function."""

    def test_convert_cpp_namespace_to_csharp(self):
        """Test conversion of C++ :: to C# ."""
        assert sanitize_namespace("AZ::Math") == "AZ.Math"
        assert sanitize_namespace("O3DE::Core::System") == "O3DE.Core.System"

    def test_remove_invalid_characters(self):
        """Test removal of invalid namespace characters."""
        assert sanitize_namespace("My-Namespace") == "My_Namespace"
        assert sanitize_namespace("My.Invalid$Chars") == "My.Invalid_Chars"

    def test_escape_keyword_parts(self):
        """Test escaping of keyword namespace parts."""
        assert sanitize_namespace("System.String") == "System.@String"
        assert sanitize_namespace("AZ::Event::Internal") == "AZ.@Event.@Internal"

    def test_empty_namespace(self):
        """Test handling of empty namespace."""
        assert sanitize_namespace("") == ""

    def test_complex_namespace(self):
        """Test complex namespace sanitization."""
        result = sanitize_namespace("O3DE::Physics::Internal::Helpers")
        assert result == "O3DE.Physics.@Internal.Helpers"

    def test_remove_empty_parts(self):
        """Test that empty namespace parts are removed."""
        assert sanitize_namespace("A..B") == "A.B"
        assert sanitize_namespace("::A::B::") == "A.B"


@pytest.mark.unit
class TestTypeMappingsConstant:
    """Test suite for TYPE_MAPPINGS constant."""

    def test_type_mappings_has_all_az_types(self):
        """Test that TYPE_MAPPINGS includes all common AZ types."""
        required_types = [
            ("AZ::Vector3", "Vector3"),
            ("AZ::Vector2", "Vector2"),
            ("AZ::Vector4", "Vector4"),
            ("AZ::Quaternion", "Quaternion"),
            ("AZ::Transform", "Transform"),
            ("AZ::Color", "Color"),
            ("AZ::EntityId", "ulong"),
        ]
        
        for cpp_type, expected_csharp in required_types:
            assert TYPE_MAPPINGS[cpp_type] == expected_csharp

    def test_type_mappings_has_primitives(self):
        """Test that TYPE_MAPPINGS includes primitive types."""
        primitives = ["int", "float", "double", "bool", "void", "string"]
        
        for primitive in primitives:
            assert primitive in TYPE_MAPPINGS

    def test_type_mappings_has_sized_integers(self):
        """Test that TYPE_MAPPINGS includes sized integer types."""
        sized_ints = {
            "int8": "sbyte",
            "int16": "short",
            "int32": "int",
            "int64": "long",
            "uint8": "byte",
            "uint16": "ushort",
            "uint32": "uint",
            "uint64": "ulong",
        }
        
        for cpp_type, csharp_type in sized_ints.items():
            assert TYPE_MAPPINGS[cpp_type] == csharp_type


@pytest.mark.unit
class TestCSharpKeywordsConstant:
    """Test suite for CSHARP_KEYWORDS constant."""

    def test_keywords_set_contains_common_keywords(self):
        """Test that CSHARP_KEYWORDS contains common C# keywords."""
        common_keywords = [
            "class", "namespace", "using", "public", "private", "protected",
            "static", "void", "int", "float", "double", "bool", "string",
            "if", "else", "for", "while", "return", "new", "this",
        ]
        
        for keyword in common_keywords:
            assert keyword in CSHARP_KEYWORDS

    def test_keywords_set_is_lowercase(self):
        """Test that all keywords in CSHARP_KEYWORDS are lowercase."""
        for keyword in CSHARP_KEYWORDS:
            assert keyword.islower()

    def test_keywords_set_has_sufficient_coverage(self):
        """Test that CSHARP_KEYWORDS has reasonable coverage (>50 keywords)."""
        assert len(CSHARP_KEYWORDS) > 50


@pytest.mark.integration
class TestTypeMapperWithRealHeaders:
    """Integration tests using real O3DESharp headers."""

    def test_map_types_from_real_headers(self, o3desharp_headers):
        """Test type mapping with types found in real headers."""
        if not o3desharp_headers:
            pytest.skip("No O3DESharp headers found")
        
        mapper = TypeMapper()
        
        # Common types that should appear in O3DESharp headers
        test_types = [
            ("AZ::Vector3", "Vector3"),
            ("AZ::EntityId", "ulong"),
            ("float", "float"),
            ("int", "int"),
            ("bool", "bool"),
        ]
        
        for cpp_type, expected in test_types:
            result = mapper.map_type(cpp_type)
            assert result == expected, f"Failed to map {cpp_type} to {expected}, got {result}"
