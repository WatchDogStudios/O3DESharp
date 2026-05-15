#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""
C# Binding Generator Configuration and ClangSharp Orchestration

DEPRECATED: The Python-based BehaviorContext binding generator has been replaced
by the ClangSharp-based tool located at Code/Tools/BindingGenerator.

This module now provides:
- Configuration utilities for the ClangSharp binding generator
- Type mapping utilities (C++ <-> C#)
- ClangSharpInvoker class for running the binding generator tool
- Legacy data classes for backwards compatibility

For new projects, use the ClangSharp tool directly:
    dotnet run --project Code/Tools/BindingGenerator -- generate --project <path>

Or use generate_bindings.py which orchestrates the ClangSharp tool.
"""

import hashlib
import json
import logging
import os
import subprocess
import sys
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List, Optional, Set, Tuple

# Set up logging
logger = logging.getLogger("O3DESharp.BindingGenerator")


# ============================================================
# Data Classes for Configuration
# ============================================================


@dataclass
class BindingGeneratorConfig:
    """Configuration for C# binding generation."""

    # Output configuration
    output_directory: str = "Generated/CSharp"
    root_namespace: str = "O3DE"
    target_framework: str = "net8.0"

    # Generation options
    require_export_attribute: bool = False
    incremental_build: bool = True
    verbose: bool = False
    generate_core: bool = True
    generate_gems: bool = True
    separate_gem_directories: bool = True
    generate_per_gem_projects: bool = True
    
    # Gem filtering
    include_gems: List[str] = field(default_factory=list)
    exclude_gems: List[str] = field(default_factory=list)

    # Core types to always include
    core_types: Set[str] = field(
        default_factory=lambda: {
            "Vector3",
            "Vector2",
            "Vector4",
            "Quaternion",
            "Transform",
            "Color",
            "EntityId",
            "Entity",
            "Matrix",
        }
    )

    # File header
    file_header: str = ""


@dataclass
class TypeMapping:
    """Mapping between C++ and C# types."""

    cpp_type: str
    csharp_type: str
    marshal_type: str = "Default"
    needs_wrapper: bool = False


@dataclass
class GemBindingInfo:
    """Information about generated bindings for a gem."""

    gem_name: str
    output_path: str
    classes_generated: int = 0
    files_generated: int = 0
    generated_files: List[str] = field(default_factory=list)


@dataclass
class BindingGeneratorResult:
    """Result of a binding generation operation."""

    success: bool = False
    error_message: str = ""
    
    # Statistics
    classes_generated: int = 0
    ebuses_generated: int = 0
    files_written: int = 0
    processed_gems: List[str] = field(default_factory=list)
    
    # Generated files
    generated_files: List[str] = field(default_factory=list)
    
    # Warnings
    warnings: List[str] = field(default_factory=list)


# ============================================================
# Reflection Data Classes
# ============================================================


@dataclass
class ReflectedParameter:
    """Represents a parameter in a reflected method."""
    
    name: str
    type_name: str
    default_value: Optional[str] = None
    is_pointer: bool = False
    is_reference: bool = False
    is_const: bool = False
    is_out: bool = False
    is_ref: bool = False


@dataclass 
class ReflectedProperty:
    """Represents a property in a reflected class."""
    
    name: str
    type_name: str
    is_readonly: bool = False
    has_getter: bool = True
    has_setter: bool = True
    getter_name: Optional[str] = None
    setter_name: Optional[str] = None
    description: str = ""


@dataclass
class ReflectedMethod:
    """Represents a method in a reflected class or EBus."""
    
    name: str
    return_type: str = "void"
    parameters: List[ReflectedParameter] = field(default_factory=list)
    is_static: bool = False
    is_const: bool = False
    description: str = ""
    category: str = ""


@dataclass
class ReflectedEBusEvent:
    """Represents an event in a reflected EBus."""
    
    name: str
    return_type: str = "void"
    parameters: List[ReflectedParameter] = field(default_factory=list)
    is_broadcast: bool = False
    description: str = ""


@dataclass
class ReflectedEBus:
    """Represents a reflected EBus."""
    
    name: str
    events: List[ReflectedEBusEvent] = field(default_factory=list)
    gem_name: str = ""
    description: str = ""
    category: str = ""


@dataclass
class ReflectedClass:
    """Represents a reflected class from BehaviorContext."""
    
    name: str
    full_name: str = ""
    gem_name: str = ""
    methods: List[ReflectedMethod] = field(default_factory=list)
    properties: List[ReflectedProperty] = field(default_factory=list)
    base_classes: List[str] = field(default_factory=list)
    base_class: Optional[str] = None
    is_component: bool = False
    description: str = ""
    category: str = ""


def _extract_type_name(value: Any) -> str:
    """
    Extract a type name string from a JSON value.
    
    The C++ exporter outputs types as objects like:
        {"name": "", "type_name": "AZ::Vector3", "is_pointer": false, ...}
    This helper handles both string and dict forms.
    """
    if isinstance(value, str):
        return value
    if isinstance(value, dict):
        return value.get("type_name", value.get("name", "void"))
    return str(value) if value is not None else "void"


def _parse_parameter(p: Any) -> ReflectedParameter:
    """Parse a parameter from JSON data (handles both dict and string forms)."""
    if isinstance(p, str):
        return ReflectedParameter(name="", type_name=p)
    if isinstance(p, dict):
        return ReflectedParameter(
            name=p.get("name", ""),
            type_name=p.get("type_name", p.get("type", "")),
            default_value=p.get("default"),
            is_pointer=p.get("is_pointer", False),
            is_reference=p.get("is_reference", False),
            is_const=p.get("is_const", False),
            is_out=p.get("is_out", False),
            is_ref=p.get("is_ref", p.get("is_reference", False))
        )
    return ReflectedParameter(name="", type_name="void")


@dataclass
class ReflectionData:
    """Container for all reflection data."""
    
    classes: List[ReflectedClass] = field(default_factory=list)
    ebuses: List[ReflectedEBus] = field(default_factory=list)
    enums: Dict[str, List[str]] = field(default_factory=dict)
    global_methods: List[ReflectedMethod] = field(default_factory=list)
    global_properties: List[ReflectedProperty] = field(default_factory=list)
    
    @classmethod
    def from_json(cls, data: Dict[str, Any]) -> "ReflectionData":
        """Create ReflectionData from JSON dictionary exported by the C++ ReflectionDataExporter."""
        result = cls()
        
        # Parse classes
        for class_data in data.get("classes", []):
            # base_classes can be an array of strings
            base_classes = class_data.get("base_classes", [])
            if not isinstance(base_classes, list):
                base_classes = [str(base_classes)] if base_classes else []
            
            # Also support single "base_class" field
            base_class = class_data.get("base_class")
            if base_class and not base_classes:
                base_classes = [base_class]
            
            reflected_class = ReflectedClass(
                name=class_data.get("name", ""),
                full_name=class_data.get("full_name", class_data.get("name", "")),
                gem_name=class_data.get("source_gem_name", class_data.get("gem_name", "")),
                base_classes=base_classes,
                base_class=base_classes[0] if base_classes else None,
                is_component=class_data.get("is_component", False),
                description=class_data.get("description", ""),
                category=class_data.get("category", "")
            )
            
            # Parse methods
            for method_data in class_data.get("methods", []):
                params = [_parse_parameter(p) for p in method_data.get("parameters", [])]
                
                # return_type can be a string or a parameter object
                return_type = _extract_type_name(method_data.get("return_type", "void"))
                
                reflected_class.methods.append(ReflectedMethod(
                    name=method_data.get("name", ""),
                    return_type=return_type,
                    parameters=params,
                    is_static=method_data.get("is_static", False),
                    is_const=method_data.get("is_const", False),
                    description=method_data.get("description", ""),
                    category=method_data.get("category", "")
                ))
            
            # Parse constructors as methods too
            for ctor_data in class_data.get("constructors", []):
                params = [_parse_parameter(p) for p in ctor_data.get("parameters", [])]
                reflected_class.methods.append(ReflectedMethod(
                    name=reflected_class.name,
                    return_type="void",
                    parameters=params,
                    is_static=False,
                    description=ctor_data.get("description", "")
                ))
            
            # Parse properties
            for prop_data in class_data.get("properties", []):
                # value_type can be a parameter object or a string
                value_type = _extract_type_name(prop_data.get("value_type", prop_data.get("type", "")))
                
                has_getter = prop_data.get("has_getter", True)
                has_setter = prop_data.get("has_setter", True)
                
                reflected_class.properties.append(ReflectedProperty(
                    name=prop_data.get("name", ""),
                    type_name=value_type,
                    is_readonly=not has_setter,
                    has_getter=has_getter,
                    has_setter=has_setter,
                    getter_name=prop_data.get("getter"),
                    setter_name=prop_data.get("setter"),
                    description=prop_data.get("description", "")
                ))
            
            result.classes.append(reflected_class)
        
        # Parse EBuses
        for ebus_data in data.get("ebuses", []):
            ebus = ReflectedEBus(
                name=ebus_data.get("name", ""),
                gem_name=ebus_data.get("source_gem_name", ebus_data.get("gem_name", "")),
                description=ebus_data.get("description", ""),
                category=ebus_data.get("category", "")
            )
            
            for event_data in ebus_data.get("events", []):
                params = [_parse_parameter(p) for p in event_data.get("parameters", [])]
                
                # return_type can be a string or a parameter object
                return_type = _extract_type_name(event_data.get("return_type", "void"))
                
                ebus.events.append(ReflectedEBusEvent(
                    name=event_data.get("name", ""),
                    return_type=return_type,
                    parameters=params,
                    is_broadcast=event_data.get("is_broadcast", False),
                    description=event_data.get("description", "")
                ))
            
            result.ebuses.append(ebus)
        
        # Parse global methods
        for method_data in data.get("global_methods", []):
            params = [_parse_parameter(p) for p in method_data.get("parameters", [])]
            return_type = _extract_type_name(method_data.get("return_type", "void"))
            
            result.global_methods.append(ReflectedMethod(
                name=method_data.get("name", ""),
                return_type=return_type,
                parameters=params,
                is_static=True,
                description=method_data.get("description", "")
            ))
        
        # Parse global properties
        for prop_data in data.get("global_properties", []):
            value_type = _extract_type_name(prop_data.get("value_type", prop_data.get("type", "")))
            has_getter = prop_data.get("has_getter", True)
            has_setter = prop_data.get("has_setter", True)
            
            result.global_properties.append(ReflectedProperty(
                name=prop_data.get("name", ""),
                type_name=value_type,
                is_readonly=not has_setter,
                has_getter=has_getter,
                has_setter=has_setter,
                description=prop_data.get("description", "")
            ))
        
        # Parse enums
        result.enums = data.get("enums", {})
        
        return result


# ============================================================
# C# Binding Generator Class
# ============================================================


# Patterns that indicate a C++ type that should NOT get its own .g.cs wrapper
_SKIP_CLASS_PATTERNS = [
    "AZStd::unordered_map",
    "AZStd::unordered_set",
    "AZStd::map",
    "AZStd::set",
    "AZStd::vector",
    "AZStd::fixed_vector",
    "AZStd::basic_string",
    "AZStd::pair",
    "AZStd::tuple",
    "AZStd::shared_ptr",
    "AZStd::unique_ptr",
    "AZStd::optional",
    "AZStd::variant",
    "AZStd::array",
    "AZStd::deque",
    "AZStd::list",
    "AZStd::forward_list",
    "Iterator_VM",
]

# Characters that are illegal in Windows filenames
_ILLEGAL_FILENAME_CHARS = frozenset('<>:"/\\|?*')


class CSharpBindingGenerator:
    """
    Legacy Python-based generator that consumes BehaviorContext reflection
    JSON dumps and emits C# wrappers.

    *** DEPRECATED ***
    Replaced by Code/Tools/BindingGenerator (the ClangSharp-based C# tool).
    Invoke that via ClangSharpInvoker.generate_bindings instead - it parses
    headers directly, doesn't require a runtime reflection_data.json dump,
    and is the only path that receives correctness fixes going forward.

    This class is scheduled for removal once the remaining editor UI
    callers (csharp_editor_tools._generate_bindings via
    generate_bindings.BindingGenerationOrchestrator) migrate.
    """

    def __init__(self, config: BindingGeneratorConfig = None):
        import warnings as _w
        _w.warn(
            "CSharpBindingGenerator is deprecated. Use ClangSharpInvoker "
            "(which shells out to Code/Tools/BindingGenerator) instead. "
            "See Editor/Scripts/__init__.py docstring for the canonical entry points.",
            DeprecationWarning,
            stacklevel=2,
        )
        self.config = config or BindingGeneratorConfig()
        self.type_mapper = TypeMapper()
        self._generated_files: Dict[str, str] = {}
        self._skipped_classes: List[str] = []
    
    def generate_from_reflection_data(
        self,
        reflection_data: ReflectionData,
        gem_resolver=None
    ) -> Dict[str, str]:
        """
        Generate C# bindings from reflection data.
        
        Args:
            reflection_data: The reflection data to generate from
            gem_resolver: Optional gem resolver for organizing output
            
        Returns:
            Dictionary mapping relative file paths to generated content
        """
        self._generated_files = {}
        self._skipped_classes = []
        
        # Generate class wrappers (skip templates and STL containers)
        for reflected_class in reflection_data.classes:
            if self._should_skip_class(reflected_class.name):
                self._skipped_classes.append(reflected_class.name)
                continue
            self._generate_class_wrapper(reflected_class)
        
        # Generate EBus wrappers
        for ebus in reflection_data.ebuses:
            if self._should_skip_class(ebus.name):
                self._skipped_classes.append(ebus.name)
                continue
            self._generate_ebus_wrapper(ebus)
        
        if self._skipped_classes:
            logger.info(f"Skipped {len(self._skipped_classes)} unsupported template/container types")
        
        return self._generated_files

    @staticmethod
    def _should_skip_class(class_name: str) -> bool:
        """Return True if this C++ class name should be skipped (templates, STL containers, etc.)."""
        # Skip anything containing template angle brackets
        if '<' in class_name or '>' in class_name:
            return True
        # Skip known STL / AZStd container patterns
        for pattern in _SKIP_CLASS_PATTERNS:
            if class_name.startswith(pattern) or f"::{pattern}" in class_name:
                return True
        return False

    @staticmethod
    def _sanitize_cs_class_name(cpp_name: str) -> str:
        """
        Convert a C++ class name to a valid C# class name.
        
        Examples:
            "AZ::Render::MeshComponent"         -> "MeshComponent"
            "LmbrCentral::QuadShapeConfig"      -> "QuadShapeConfig"
            "ShaderSourceData::EntryPoint"       -> "ShaderSourceData_EntryPoint"
            "Multi-Position Audio Requests"      -> "MultiPositionAudioRequests"
        """
        # Strip C++ template arguments  <...>
        import re
        name = re.sub(r'<[^>]*>', '', cpp_name).strip()

        # Strip namespace prefixes like AZ::, AZ::Render::, etc.
        parts = name.split("::")
        if len(parts) <= 2:
            base = parts[-1]
        else:
            # keep the last two parts joined with underscore
            base = "_".join(parts[-2:])

        # Replace hyphens and spaces with nothing (PascalCase merge)
        # e.g. "Multi-Position Audio Requests" -> "MultiPositionAudioRequests"
        result = ""
        capitalize_next = False
        for ch in base:
            if ch in "- ":
                capitalize_next = True  # next alpha char becomes upper
                continue
            if capitalize_next and ch.isalpha():
                result += ch.upper()
                capitalize_next = False
            else:
                result += ch

        # Ensure starts with a letter or underscore
        if result and not (result[0].isalpha() or result[0] == '_'):
            result = '_' + result

        # Remove any remaining invalid C# identifier chars
        result = "".join(ch for ch in result if ch.isalnum() or ch == '_')

        return result or "Unknown"

    @staticmethod
    def _sanitize_filename(name: str) -> str:
        """
        Sanitize a string so it can be used as a filename on Windows.
        
        Replaces :: with ., removes <>, limits length.
        """
        # Replace :: with dots, remove template chars, strip spaces
        result = name.replace("::", ".").replace("<", "_").replace(">", "_")
        result = result.replace(" ", "")
        # Remove any remaining illegal characters
        result = "".join(c for c in result if c not in _ILLEGAL_FILENAME_CHARS)
        # Collapse repeated underscores / dots
        while "__" in result:
            result = result.replace("__", "_")
        result = result.strip("_.")
        # Limit filename length (leave room for .g.cs suffix + directory path)
        if len(result) > 120:
            result = result[:120]
        return result
    
    def _generate_class_wrapper(self, reflected_class: ReflectedClass):
        """Generate a C# wrapper class."""
        raw_name = reflected_class.name
        cs_class_name = self._sanitize_cs_class_name(raw_name)
        safe_filename = self._sanitize_filename(raw_name)

        namespace = f"{self.config.root_namespace}"
        if reflected_class.gem_name:
            namespace = f"{self.config.root_namespace}.{self._sanitize_namespace(reflected_class.gem_name)}"
        
        lines = [
            "// Auto-generated by O3DESharp binding generator",
            f"// Source C++ type: {raw_name}",
            "// Do not modify manually",
            "",
            "using System;",
            "using System.Runtime.InteropServices;",
            "using O3DE.Reflection;",
            "",
            f"namespace {namespace}",
            "{",
            f"    /// <summary>",
            f"    /// {reflected_class.description or raw_name}",
            f"    /// </summary>",
            f"    public class {cs_class_name} : IDisposable",
            "    {",
            f'        /// <summary>The original C++ type name used for NativeReflection lookups.</summary>',
            f'        public const string NativeTypeName = "{raw_name}";',
            "",
            f'        private NativeObject? _native;',
            "",
            f'        /// <summary>Create a new wrapper backed by a native instance.</summary>',
            f'        public {cs_class_name}()',
            "        {",
            f'            _native = NativeReflection.CreateInstance(NativeTypeName);',
            "        }",
            "",
            f'        /// <summary>Wrap an existing native object.</summary>',
            f'        public {cs_class_name}(NativeObject existingNative)',
            "        {",
            f'            _native = existingNative ?? throw new ArgumentNullException(nameof(existingNative));',
            "        }",
            "",
            "        /// <inheritdoc/>",
            "        public void Dispose()",
            "        {",
            "            _native?.Dispose();",
            "            _native = null;",
            "        }",
            "",
            "        private NativeObject Native => _native ?? throw new ObjectDisposedException(nameof(" + cs_class_name + "));",
        ]
        
        # Generate properties
        for prop in reflected_class.properties:
            lines.extend(self._generate_property(prop))
        
        # Generate methods
        for method in reflected_class.methods:
            lines.extend(self._generate_method(method, raw_name))
        
        lines.append("    }")
        lines.append("}")
        
        # Determine output path with safe filename
        gem_dir = self._sanitize_namespace(reflected_class.gem_name) if reflected_class.gem_name else "Core"
        rel_path = f"{gem_dir}/{safe_filename}.g.cs"
        
        self._generated_files[rel_path] = "\n".join(lines)
    
    def _generate_ebus_wrapper(self, ebus: ReflectedEBus):
        """Generate a C# wrapper for an EBus."""
        raw_name = ebus.name
        # Build a valid C# class name: strip "Bus" suffix, sanitize, re-add "Bus"
        base = raw_name
        if base.endswith("Bus"):
            base = base[:-3]
        cs_bus_name = self._sanitize_cs_class_name(base) + "Bus"
        safe_filename = self._sanitize_filename(cs_bus_name)

        namespace = f"{self.config.root_namespace}"
        if ebus.gem_name:
            namespace = f"{self.config.root_namespace}.{self._sanitize_namespace(ebus.gem_name)}"
        
        lines = [
            "// Auto-generated by O3DESharp binding generator",
            f"// Source C++ EBus: {raw_name}",
            "// Do not modify manually",
            "",
            "using System;",
            "using System.Runtime.InteropServices;",
            "using O3DE.Reflection;",
            "",
            f"namespace {namespace}",
            "{",
            f"    /// <summary>",
            f"    /// {ebus.description or raw_name}",
            f"    /// </summary>",
            f"    public static class {cs_bus_name}",
            "    {",
            f'        /// <summary>The original C++ EBus name used for NativeReflection lookups.</summary>',
            f'        public const string NativeBusName = "{raw_name}";',
        ]
        
        # Generate event methods
        for event in ebus.events:
            lines.extend(self._generate_ebus_event(event, raw_name))
        
        lines.append("    }")
        lines.append("}")
        
        # Determine output path with safe filename
        gem_dir = self._sanitize_namespace(ebus.gem_name) if ebus.gem_name else "Core"
        rel_path = f"{gem_dir}/EBuses/{safe_filename}.g.cs"
        
        self._generated_files[rel_path] = "\n".join(lines)
    
    def _generate_property(self, prop: ReflectedProperty) -> List[str]:
        """Generate C# property code that delegates to NativeReflection."""
        cs_type = self.type_mapper.map_type(prop.type_name)
        prop_name = self._escape_identifier(prop.name)
        # Use the original C++ property name for the reflection lookup
        native_prop_name = prop.name

        getter_body = (
            f'NativeReflection.GetProperty<{cs_type}>(Native, "{native_prop_name}")'
        )
        # For non-nullable value types the API returns T? — provide a default
        if cs_type in ("int", "uint", "long", "ulong", "float", "double",
                       "bool", "byte", "sbyte", "short", "ushort", "char"):
            getter_body += f" ?? default"

        lines = [
            "",
            f"        /// <summary>{prop.description or prop.name}</summary>",
            f"        public {cs_type} {prop_name}",
            "        {",
            f"            get => {getter_body};",
        ]

        if not prop.is_readonly:
            lines.append(
                f'            set => NativeReflection.SetProperty(Native, "{native_prop_name}", value);'
            )

        lines.append("        }")

        return lines
    
    def _generate_method(self, method: ReflectedMethod, cpp_class_name: str = "") -> List[str]:
        """Generate C# method code that delegates to NativeReflection."""
        cs_return = self.type_mapper.map_type(method.return_type)
        method_name = self._escape_identifier(method.name)
        is_void = cs_return == "void"

        params = []
        param_names = []
        seen_param_names: set = set()
        for idx, param in enumerate(method.parameters):
            cs_param_type = self.type_mapper.map_type(param.type_name)
            pname = self._escape_identifier(param.name)
            # If param name was empty or collides, generate a unique one
            if not pname or pname == "_arg" or pname in seen_param_names:
                pname = f"arg{idx}"
            if pname == cs_param_type:
                pname = f"{pname}Value"
            seen_param_names.add(pname)
            prefix = ""
            if param.is_out:
                prefix = "out "
            elif param.is_ref:
                prefix = "ref "
            params.append(f"{prefix}{cs_param_type} {pname}")
            param_names.append(pname)

        param_str = ", ".join(params)
        static_mod = "static " if method.is_static else ""
        native_method_name = method.name  # original C++ name

        # Build the arguments portion: comma-separated param names
        args_portion = ", ".join(param_names)

        lines = [
            "",
            f"        /// <summary>{method.description or method.name}</summary>",
            f"        public {static_mod}{cs_return} {method_name}({param_str})",
            "        {",
        ]

        if method.is_static:
            call = f'NativeReflection.InvokeStaticMethod(NativeTypeName, "{native_method_name}"'
            if args_portion:
                call += f", {args_portion}"
            call += ")"
            if is_void:
                lines.append(f"            {call};")
            else:
                lines.append(f"            var __result = {call};")
                lines.append(f"            return __result is {cs_return} __typed ? __typed : default!;")
        else:
            call = f'NativeReflection.InvokeInstanceMethod(Native, "{native_method_name}"'
            if args_portion:
                call += f", {args_portion}"
            call += ")"
            if is_void:
                lines.append(f"            {call};")
            else:
                lines.append(f"            var __result = {call};")
                lines.append(f"            return __result is {cs_return} __typed ? __typed : default!;")

        lines.append("        }")

        return lines
    
    def _generate_ebus_event(self, event: ReflectedEBusEvent, ebus_name: str) -> List[str]:
        """Generate C# code for an EBus event that delegates to NativeReflection."""
        cs_return = self.type_mapper.map_type(event.return_type)
        # Sanitize event name (may contain spaces like "Add Entity")
        event_name = self._escape_identifier(event.name)
        is_void = cs_return == "void"
        native_event_name = event.name  # original C++ event name

        params = []
        param_names = []
        # For entity-addressed buses, accept an optional entityId parameter
        has_entity_param = not event.is_broadcast
        if has_entity_param:
            params.append("ulong entityId")

        seen_param_names: set = set()
        for idx, param in enumerate(event.parameters):
            cs_param_type = self.type_mapper.map_type(param.type_name)
            pname = self._escape_identifier(param.name)
            # If param name was empty or collides, generate a unique one
            if not pname or pname == "_arg" or pname in seen_param_names:
                pname = f"arg{idx}"
            # Avoid param name colliding with its type
            if pname == cs_param_type:
                pname = f"{pname}Value"
            seen_param_names.add(pname)
            params.append(f"{cs_param_type} {pname}")
            param_names.append(pname)

        param_str = ", ".join(params)
        args_portion = ", ".join(param_names)

        lines = [
            "",
            f"        /// <summary>{event.description or event.name}</summary>",
            f"        public static {cs_return} {event_name}({param_str})",
            "        {",
        ]

        if event.is_broadcast or not has_entity_param:
            # Broadcast to all handlers
            call = f'NativeReflection.BroadcastEBusEvent("{ebus_name}", "{native_event_name}"'
            if args_portion:
                call += f", {args_portion}"
            call += ")"
        else:
            # Send to a specific entity
            call = f'NativeReflection.SendEBusEvent("{ebus_name}", "{native_event_name}", entityId'
            if args_portion:
                call += f", {args_portion}"
            call += ")"

        if is_void:
            lines.append(f"            {call};")
        else:
            lines.append(f"            var __result = {call};")
            lines.append(f"            return __result is {cs_return} __typed ? __typed : default!;")

        lines.append("        }")

        return lines
    
    def _sanitize_namespace(self, name: str) -> str:
        """Sanitize a name for use as a C# namespace component."""
        # Remove invalid characters and make valid identifier
        result = ""
        for char in name:
            if char.isalnum() or char == "_":
                result += char
            elif char in "-. ":
                result += "_"
        
        # Ensure doesn't start with digit
        if result and result[0].isdigit():
            result = "_" + result
        
        return result or "Unknown"
    
    def _escape_identifier(self, name: str) -> str:
        """Sanitize and escape a C# identifier.

        Strips C++ qualifiers (const, &, *, ::), removes spaces/hyphens,
        and prefixes with @ if the result is a C# keyword.
        """
        if not name:
            return "_arg"

        # Strip C++ qualifiers that may leak from reflection data
        s = name.replace("const&", "").replace("const*", "")
        s = s.replace("const ", " ").replace(" const", "")
        s = s.replace("&", "").replace("*", "")

        # Replace :: with _ , strip template args
        import re
        s = re.sub(r'<[^>]*>', '', s)
        s = s.replace("::", "_")

        # Remove spaces, hyphens, brackets
        s = s.replace(" ", "").replace("-", "").replace("[", "").replace("]", "")

        # Keep only valid identifier chars
        s = "".join(ch for ch in s if ch.isalnum() or ch == '_')
        s = s.strip("_") or "_arg"

        # Ensure doesn't start with a digit
        if s[0].isdigit():
            s = '_' + s

        if s.lower() in CSHARP_KEYWORDS:
            return "@" + s
        return s


# ============================================================
# Type Mapping Utilities
# ============================================================

# C# reserved keywords that need escaping with @
CSHARP_KEYWORDS = {
    "abstract", "as", "base", "bool", "break", "byte", "case", "catch",
    "char", "checked", "class", "const", "continue", "decimal", "default",
    "delegate", "do", "double", "else", "enum", "event", "explicit",
    "extern", "false", "finally", "fixed", "float", "for", "foreach",
    "goto", "if", "implicit", "in", "int", "interface", "internal",
    "is", "lock", "long", "namespace", "new", "null", "object",
    "operator", "out", "override", "params", "private", "protected",
    "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
    "sizeof", "stackalloc", "static", "string", "struct", "switch",
    "this", "throw", "true", "try", "typeof", "uint", "ulong",
    "unchecked", "unsafe", "ushort", "using", "virtual", "void",
    "volatile", "while",
}

# Type mappings from O3DE C++ to C#
TYPE_MAPPINGS = {
    "Vector3": "Vector3",
    "Vector2": "Vector2",
    "Vector4": "Vector4",
    "Quaternion": "Quaternion",
    "Transform": "Transform",
    "Color": "Color",
    "EntityId": "ulong",
    "AZ::Vector3": "Vector3",
    "AZ::Vector2": "Vector2",
    "AZ::Vector4": "Vector4",
    "AZ::Quaternion": "Quaternion",
    "AZ::Transform": "Transform",
    "AZ::Color": "Color",
    "AZ::EntityId": "ulong",
    "AZStd::string": "string",
    "string": "string",
    "bool": "bool",
    "int": "int",
    "float": "float",
    "double": "double",
    "int8": "sbyte",
    "int16": "short",
    "int32": "int",
    "int64": "long",
    "uint8": "byte",
    "uint16": "ushort",
    "uint32": "uint",
    "uint64": "ulong",
    "void": "void",
    "AZ::u8": "byte",
    "AZ::u16": "ushort",
    "AZ::u32": "uint",
    "AZ::u64": "ulong",
    "AZ::s8": "sbyte",
    "AZ::s16": "short",
    "AZ::s32": "int",
    "AZ::s64": "long",
}


class TypeMapper:
    """Utility class for mapping C++ types to C# types."""

    # Regex to strip template arguments:  AZStd::vector<EntityId, allocator> -> AZStd::vector
    _TEMPLATE_RE = None  # lazily compiled

    def __init__(self, custom_mappings: Optional[Dict[str, str]] = None):
        import re
        self.mappings = TYPE_MAPPINGS.copy()
        if custom_mappings:
            self.mappings.update(custom_mappings)
        if TypeMapper._TEMPLATE_RE is None:
            TypeMapper._TEMPLATE_RE = re.compile(r'<[^>]*>')

    @staticmethod
    def _strip_cpp_qualifiers(t: str) -> str:
        """Remove all C++ type qualifiers: const, *, &, trailing const."""
        t = t.replace("const&", "").replace("const*", "")
        t = t.replace("const ", " ").replace(" const", "")
        t = t.replace("*", "").replace("&", "")
        return t.strip()

    def map_type(self, cpp_type: str) -> str:
        """Map a C++ type to its C# equivalent."""
        # Defensive: handle dict/non-string types that may come from JSON
        if isinstance(cpp_type, dict):
            cpp_type = cpp_type.get("type_name", cpp_type.get("name", "void"))
        if not isinstance(cpp_type, str):
            cpp_type = str(cpp_type) if cpp_type is not None else "void"

        # Fast path: empty / void
        stripped = cpp_type.strip()
        if not stripped or stripped == "void":
            return "void"

        # 1. Strip all const / ptr / ref qualifiers
        clean = self._strip_cpp_qualifiers(stripped)

        # 2. Direct lookup (handles "AZ::Vector3", "float", etc.)
        if clean in self.mappings:
            return self.mappings[clean]

        # 3. Handle well-known template containers
        #    AZStd::vector<X, allocator>  ->  object  (or List<mapped_X>)
        #    AZStd::basic_string<...>      ->  string
        #    Outcome<void, ...>            ->  object
        if "<" in clean:
            base = clean[:clean.index("<")].strip()
            base_stripped = base.split("::")[-1] if "::" in base else base
            if base in ("AZStd::basic_string", "AZStd::string") or base_stripped == "basic_string":
                return "string"
            if base in ("AZStd::vector", "AZStd::fixed_vector") or base_stripped == "vector":
                return "object"  # ideally List<T> but needs inner mapping
            if base_stripped in ("Outcome", "AZ::Outcome"):
                return "object"
            # Generic template -> object
            return "object"

        # 4. Try stripping namespace prefix:  AZ::Render::Foo -> Foo
        if "::" in clean:
            short = clean.split("::")[-1]
            if short in self.mappings:
                return self.mappings[short]
            # Unknown namespaced type -> use short name if it looks like a valid C# identifier
            if short.isidentifier():
                return short
            return "object"

        # 5. If the remaining name is a valid C# identifier, keep it;
        #    otherwise fall back to "object" to avoid emitting broken C# syntax.
        if clean.isidentifier():
            return clean

        return "object"

    def is_primitive(self, csharp_type: str) -> bool:
        """Check if a C# type is a primitive type."""
        primitives = {"bool", "byte", "sbyte", "short", "ushort", "int", "uint",
                     "long", "ulong", "float", "double", "char", "string"}
        return csharp_type in primitives

    def needs_marshalling(self, cpp_type: str) -> bool:
        """Check if a C++ type needs special marshalling."""
        # Pointers and references need marshalling
        if "*" in cpp_type or "&" in cpp_type:
            return True
        
        # Complex types need marshalling
        clean_type = cpp_type.replace("const ", "").strip()
        return clean_type not in {"bool", "int", "float", "double", "int32", "uint32", 
                                  "int64", "uint64", "byte", "sbyte"}


def escape_identifier(name: str) -> str:
    """Escape a C# identifier if it's a keyword."""
    if name.lower() in CSHARP_KEYWORDS:
        return f"@{name}"
    return name


def sanitize_namespace(namespace: str) -> str:
    """Sanitize a namespace to be valid C#."""
    # Replace :: with .
    namespace = namespace.replace("::", ".")
    
    # Remove invalid characters
    namespace = "".join(c if c.isalnum() or c == "." else "_" for c in namespace)
    
    # Ensure each part is a valid identifier
    parts = namespace.split(".")
    parts = [escape_identifier(part) for part in parts if part]
    
    return ".".join(parts)


# ============================================================
# ClangSharp Tool Invoker
# ============================================================


class ClangSharpInvoker:
    """
    Invokes the ClangSharp-based binding generator tool.

    This class constructs command-line arguments and executes the
    C# ClangSharp tool that performs static analysis of C++ headers.
    """

    def __init__(self, tool_path: Optional[str] = None, engine_path: Optional[str] = None):
        """
        Initialize the ClangSharp invoker.

        Args:
            tool_path: Path to the BindingGenerator executable/project.
                      If None, will search in common locations.
            engine_path: Optional explicit engine root path to pass to the tool.
        """
        self.logger = logging.getLogger("O3DESharp.ClangSharpInvoker")
        self.tool_path = tool_path or self._find_tool()
        self.engine_path = engine_path

    def _find_tool(self) -> Optional[str]:
        """Find the ClangSharp binding generator tool."""
        # Search in common locations relative to this script
        script_dir = Path(__file__).parent
        gem_root = script_dir.parent.parent
        
        # Known .csproj locations (most specific first)
        csproj_candidates = [
            gem_root / "Code" / "Tools" / "BindingGenerator" / "O3DESharp.BindingGenerator" / "O3DESharp.BindingGenerator.csproj",
        ]
        for csproj in csproj_candidates:
            if csproj.exists():
                self.logger.info(f"Using ClangSharp tool source project: {csproj}")
                return str(csproj)

        # Check in CMake binary directory (if built as an executable)
        possible_paths = [
            gem_root / "build" / "bin" / "BindingGenerator",
            Path.cwd() / "build" / "bin" / "BindingGenerator",
        ]
        
        for path in possible_paths:
            if path.exists():
                # If it's a directory, look for a .csproj inside
                if path.is_dir():
                    for csproj in path.rglob("*.csproj"):
                        self.logger.info(f"Found ClangSharp tool project: {csproj}")
                        return str(csproj)
                else:
                    self.logger.info(f"Found ClangSharp tool at: {path}")
                    return str(path)
        
        self.logger.warning("ClangSharp binding generator tool not found in common locations")
        return None

    def generate_bindings(
        self,
        project_path: str,
        config: Optional[BindingGeneratorConfig] = None,
        force_regenerate: bool = False,
    ) -> BindingGeneratorResult:
        """
        Generate C# bindings using the ClangSharp tool.
        
        Args:
            project_path: Path to the O3DE project
            config: Binding generation configuration
            force_regenerate: If True, bypass incremental build cache
            
        Returns:
            BindingGeneratorResult with generation statistics
        """
        if not self.tool_path:
            return BindingGeneratorResult(
                success=False,
                error_message="ClangSharp binding generator tool not found. "
                             "Ensure Code/Tools/BindingGenerator is built."
            )
        
        config = config or BindingGeneratorConfig()
        
        # Build command-line arguments
        args = self._build_arguments(project_path, config, force_regenerate)
        
        # Execute the tool
        self.logger.info(f"Executing ClangSharp binding generator...")
        self.logger.debug(f"Command: {' '.join(args)}")
        
        try:
            result = subprocess.run(
                args,
                capture_output=True,
                text=True,
                cwd=Path(self.tool_path).parent if Path(self.tool_path).is_file() else self.tool_path,
                timeout=1800,  # 30 minute timeout for full project generation
            )
            
            # Parse output
            if result.returncode == 0:
                return self._parse_success_output(result.stdout, config)
            else:
                return BindingGeneratorResult(
                    success=False,
                    error_message=f"ClangSharp tool failed with exit code {result.returncode}\n"
                                 f"STDOUT: {result.stdout}\n"
                                 f"STDERR: {result.stderr}"
                )
                
        except subprocess.TimeoutExpired:
            return BindingGeneratorResult(
                success=False,
                error_message="ClangSharp tool timed out after 30 minutes"
            )
        except Exception as e:
            return BindingGeneratorResult(
                success=False,
                error_message=f"Failed to execute ClangSharp tool: {str(e)}"
            )

    def _build_arguments(
        self,
        project_path: str,
        config: BindingGeneratorConfig,
        force_regenerate: bool,
    ) -> List[str]:
        """Build command-line arguments for the ClangSharp tool."""
        tool_path = Path(self.tool_path)
        
        # Base command - use dotnet run if it's a .csproj, or direct execution if built
        if tool_path.suffix == ".csproj":
            args = ["dotnet", "run", "--project", str(tool_path), "--"]
        elif tool_path.is_dir():
            # Directory with .csproj
            csproj = tool_path / "BindingGenerator.csproj"
            if csproj.exists():
                args = ["dotnet", "run", "--project", str(csproj), "--"]
            else:
                args = ["dotnet", "run", "--"]
        else:
            # Assume it's an executable
            args = [str(tool_path)]
        
        # Add generate command
        args.append("generate")
        
        # Add project path
        args.extend(["--project", str(project_path)])

        # Add engine path if available
        if self.engine_path:
            args.extend(["--engine", str(self.engine_path)])
        
        # Add configuration options
        if config.require_export_attribute:
            args.append("--require-attribute")
        
        if config.incremental_build and not force_regenerate:
            args.append("--incremental")
        
        if force_regenerate:
            args.append("--force")
        
        if config.verbose:
            args.append("--verbose")
        
        if config.include_gems:
            args.extend(["--gems", ",".join(config.include_gems)])

        if config.output_directory:
            args.extend(["--output", config.output_directory])

        return args

    def _parse_success_output(
        self,
        stdout: str,
        config: BindingGeneratorConfig
    ) -> BindingGeneratorResult:
        """Parse the ClangSharp tool output for statistics."""
        result = BindingGeneratorResult(success=True)
        
        # Parse output for statistics (simple regex patterns)
        import re
        
        # Look for patterns like "Generated 42 classes" or "Generated 42 classes in 15 files"
        classes_match = re.search(r"Generated (\d+) classes", stdout)
        if classes_match:
            result.classes_generated = int(classes_match.group(1))

        # Look for "in X files" (part of "Generated N classes in M files") or "Wrote 15 files"
        files_match = re.search(r"in (\d+) files", stdout)
        if not files_match:
            files_match = re.search(r"Wrote (\d+) files", stdout)
        if files_match:
            result.files_written = int(files_match.group(1))
        
        # Look for "Processed gems: Gem1, Gem2"
        gems_match = re.search(r"Processed gems?: (.+)", stdout)
        if gems_match:
            gems_str = gems_match.group(1).strip()
            result.processed_gems = [g.strip() for g in gems_str.split(",")]
        
        # Look for warnings
        for line in stdout.split("\n"):
            if "warning:" in line.lower() or "warn:" in line.lower():
                result.warnings.append(line.strip())
        
        self.logger.info(f"Generated {result.classes_generated} classes in {result.files_written} files")
        if result.processed_gems:
            self.logger.info(f"Processed gems: {', '.join(result.processed_gems)}")
        
        return result

    def check_tool_available(self) -> Tuple[bool, str]:
        """
        Check if the ClangSharp tool is available.
        
        Returns:
            Tuple of (available, message)
        """
        if not self.tool_path:
            return False, "ClangSharp binding generator tool not found"
        
        tool_path = Path(self.tool_path)
        
        # Check if it's a source project (.csproj file or directory containing one)
        if tool_path.suffix == ".csproj" and tool_path.exists():
            csproj_path = tool_path
        elif tool_path.is_dir():
            # Look for any .csproj in the directory tree
            csproj_path = None
            for candidate_name in [
                "O3DESharp.BindingGenerator.csproj",
                "BindingGenerator.csproj",
            ]:
                found = list(tool_path.rglob(candidate_name))
                if found:
                    csproj_path = found[0]
                    break
        else:
            csproj_path = None

        if csproj_path and csproj_path.exists():
            # Check if dotnet is available
            try:
                result = subprocess.run(
                    ["dotnet", "--version"],
                    capture_output=True,
                    text=True,
                    timeout=5
                )
                if result.returncode == 0:
                    dotnet_version = result.stdout.strip()
                    return True, f"ClangSharp tool source available, dotnet version: {dotnet_version}"
                else:
                    return False, "dotnet CLI not found. Install .NET 8.0 SDK or later."
            except Exception as e:
                return False, f"dotnet CLI check failed: {str(e)}"
        
        # Check if it's a built executable
        if tool_path.is_file() and tool_path.exists():
            return True, f"ClangSharp tool executable found at {tool_path}"
        
        return False, "ClangSharp tool path exists but is not executable or project"


# ============================================================
# Deprecated Functions (for backwards compatibility)
# ============================================================


def load_reflection_data_from_json(json_path: str) -> Optional[ReflectionData]:
    """
    Load reflection data from JSON file.
    
    Args:
        json_path: Path to the reflection_data.json file
        
    Returns:
        ReflectionData object or None if loading failed
    """
    try:
        with open(json_path, "r", encoding="utf-8") as f:
            data = json.load(f)
        return ReflectionData.from_json(data)
    except Exception as e:
        logger.error(f"Failed to load reflection data from {json_path}: {e}")
        return None


# ============================================================
# Module-level deprecation warning (disabled - no longer deprecated)
# ============================================================

# The module is now actively maintained as a wrapper around the ClangSharp tool
