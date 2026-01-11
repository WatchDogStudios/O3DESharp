#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""
C# Binding Generator for O3DESharp

This module generates C# source files from O3DE BehaviorContext reflection data.
It works in conjunction with the gem_dependency_resolver to organize bindings by gem.

The generator produces:
- Core O3DE bindings (math, entity, transform, etc.)
- Per-gem bindings organized by namespace
- EBus wrapper classes
- Solution and project files

Usage:
    from csharp_binding_generator import CSharpBindingGenerator, BindingGeneratorConfig
    from gem_dependency_resolver import GemDependencyResolver

    # Configure the generator
    config = BindingGeneratorConfig()
    config.output_directory = "Generated/CSharp"
    config.root_namespace = "O3DE.Generated"

    # Create generator and generate bindings
    generator = CSharpBindingGenerator(config)
    result = generator.generate_from_reflection_data(reflection_data, gem_resolver)

    # Write files to disk
    generator.write_files()
"""

import hashlib
import json
import logging
import os
import re
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List, Optional, Set, Tuple

from gem_dependency_resolver import GemDependencyResolver, GemDescriptor

# Set up logging
logger = logging.getLogger("O3DESharp.BindingGenerator")


# ============================================================
# Data Classes for Reflection Data
# ============================================================


@dataclass
class ReflectedParameter:
    """Represents a parameter in a reflected method."""

    name: str
    type_name: str
    type_id: str = ""
    is_pointer: bool = False
    is_reference: bool = False
    is_const: bool = False
    marshal_type: str = "Unknown"


@dataclass
class ReflectedMethod:
    """Represents a reflected method from BehaviorContext."""

    name: str
    class_name: str = ""
    is_static: bool = False
    is_const: bool = False
    return_type: ReflectedParameter = None
    parameters: List[ReflectedParameter] = field(default_factory=list)
    description: str = ""
    category: str = ""
    is_deprecated: bool = False
    deprecation_message: str = ""

    def __post_init__(self):
        if self.return_type is None:
            self.return_type = ReflectedParameter(name="", type_name="void")


@dataclass
class ReflectedProperty:
    """Represents a reflected property from BehaviorContext."""

    name: str
    class_name: str = ""
    value_type: ReflectedParameter = None
    has_getter: bool = True
    has_setter: bool = True
    description: str = ""
    is_deprecated: bool = False

    def __post_init__(self):
        if self.value_type is None:
            self.value_type = ReflectedParameter(name="", type_name="object")


@dataclass
class ReflectedEBusEvent:
    """Represents a reflected EBus event."""

    name: str
    bus_name: str = ""
    return_type: ReflectedParameter = None
    parameters: List[ReflectedParameter] = field(default_factory=list)
    is_broadcast: bool = True

    def __post_init__(self):
        if self.return_type is None:
            self.return_type = ReflectedParameter(name="", type_name="void")


@dataclass
class ReflectedEBus:
    """Represents a reflected EBus from BehaviorContext."""

    name: str
    type_id: str = ""
    address_type: ReflectedParameter = None
    events: List[ReflectedEBusEvent] = field(default_factory=list)
    description: str = ""
    category: str = ""
    source_gem_name: str = ""


@dataclass
class ReflectedClass:
    """Represents a reflected class from BehaviorContext."""

    name: str
    type_id: str = ""
    base_classes: List[str] = field(default_factory=list)
    methods: List[ReflectedMethod] = field(default_factory=list)
    properties: List[ReflectedProperty] = field(default_factory=list)
    constructors: List[ReflectedMethod] = field(default_factory=list)
    description: str = ""
    category: str = ""
    is_deprecated: bool = False
    source_gem_name: str = ""


@dataclass
class ReflectionData:
    """Container for all reflection data."""

    classes: Dict[str, ReflectedClass] = field(default_factory=dict)
    ebuses: Dict[str, ReflectedEBus] = field(default_factory=dict)
    global_methods: List[ReflectedMethod] = field(default_factory=list)
    global_properties: List[ReflectedProperty] = field(default_factory=list)


# ============================================================
# Generator Configuration
# ============================================================


@dataclass
class BindingGeneratorConfig:
    """Configuration for the C# binding generator."""

    # Output configuration
    output_directory: str = "Generated/CSharp"
    root_namespace: str = "O3DE.Generated"
    write_to_disk: bool = True
    overwrite_existing: bool = True

    # Core bindings configuration
    generate_core_bindings: bool = True
    core_namespace: str = "O3DE.Core"
    core_output_subdir: str = "Core"
    core_categories: List[str] = field(
        default_factory=lambda: [
            "Core",
            "Math",
            "Entity",
            "Components",
            "Transform",
            "Debug",
            "Time",
        ]
    )
    core_prefixes: List[str] = field(
        default_factory=lambda: [
            "AZ",
            "Az",
            "Entity",
            "Component",
            "Transform",
            "Vector",
            "Quaternion",
            "Matrix",
        ]
    )

    # Gem bindings configuration
    generate_gem_bindings: bool = True
    separate_gem_directories: bool = True
    generate_per_gem_projects: bool = False
    include_gems: List[str] = field(default_factory=list)
    exclude_gems: List[str] = field(default_factory=list)
    include_gem_dependencies: bool = True

    # Code generation options
    generate_documentation: bool = True
    generate_ebus_wrappers: bool = True
    mark_deprecated_as_obsolete: bool = True
    generate_partial_classes: bool = True
    generate_extension_methods: bool = False
    file_header: str = ""

    # Solution/project generation
    generate_solution: bool = True
    solution_name: str = "O3DE.Generated"
    generate_combined_assembly: bool = True
    combined_assembly_name: str = "O3DE.Generated"

    # Target framework
    target_framework: str = "net8.0"


@dataclass
class GeneratedFile:
    """Represents a generated C# file."""

    relative_path: str
    content: str
    checksum: str = ""

    def __post_init__(self):
        if not self.checksum:
            self.checksum = hashlib.md5(self.content.encode("utf-8")).hexdigest()


@dataclass
class GemBindingStats:
    """Statistics for per-gem binding generation."""

    gem_name: str
    classes_generated: int = 0
    ebuses_generated: int = 0
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
    methods_generated: int = 0
    properties_generated: int = 0
    files_written: int = 0

    # Per-gem statistics
    per_gem_stats: List[GemBindingStats] = field(default_factory=list)
    processed_gems: List[str] = field(default_factory=list)

    # Generated files
    generated_files: List[str] = field(default_factory=list)

    # Warnings
    warnings: List[str] = field(default_factory=list)


# ============================================================
# C# Code Generation
# ============================================================

# C# reserved keywords that need escaping with @
CSHARP_KEYWORDS = {
    "abstract",
    "as",
    "base",
    "bool",
    "break",
    "byte",
    "case",
    "catch",
    "char",
    "checked",
    "class",
    "const",
    "continue",
    "decimal",
    "default",
    "delegate",
    "do",
    "double",
    "else",
    "enum",
    "event",
    "explicit",
    "extern",
    "false",
    "finally",
    "fixed",
    "float",
    "for",
    "foreach",
    "goto",
    "if",
    "implicit",
    "in",
    "int",
    "interface",
    "internal",
    "is",
    "lock",
    "long",
    "namespace",
    "new",
    "null",
    "object",
    "operator",
    "out",
    "override",
    "params",
    "private",
    "protected",
    "public",
    "readonly",
    "ref",
    "return",
    "sbyte",
    "sealed",
    "short",
    "sizeof",
    "stackalloc",
    "static",
    "string",
    "struct",
    "switch",
    "this",
    "throw",
    "true",
    "try",
    "typeof",
    "uint",
    "ulong",
    "unchecked",
    "unsafe",
    "ushort",
    "using",
    "virtual",
    "void",
    "volatile",
    "while",
}

# Type mappings from O3DE to C#
TYPE_MAPPINGS = {
    "Vector3": "Vector3",
    "Quaternion": "Quaternion",
    "Transform": "Transform",
    "EntityId": "EntityId",
    "AZ::Vector3": "Vector3",
    "AZ::Quaternion": "Quaternion",
    "AZ::Transform": "Transform",
    "AZ::EntityId": "EntityId",
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


class CSharpBindingGenerator:
    """
    Generates C# source files from O3DE BehaviorContext reflection data.

    This class takes reflection metadata and generates strongly-typed C# wrapper
    classes organized by gem. The generated code provides:

    - Full IntelliSense support in IDEs
    - Compile-time type checking
    - XML documentation from BehaviorContext attributes
    - Proper C# naming conventions (PascalCase methods, etc.)
    - Nullable reference type annotations
    """

    def __init__(self, config: Optional[BindingGeneratorConfig] = None):
        self.config = config or BindingGeneratorConfig()
        self._generated_files: List[GeneratedFile] = []
        self._gem_stats: Dict[str, GemBindingStats] = {}

    # ============================================================
    # Main Generation Methods
    # ============================================================

    def generate_from_reflection_data(
        self,
        reflection_data: ReflectionData,
        gem_resolver: Optional[GemDependencyResolver] = None,
    ) -> BindingGeneratorResult:
        """
        Generate C# bindings from reflection data.

        Args:
            reflection_data: The reflection data to generate bindings from
            gem_resolver: Optional gem resolver for per-gem organization

        Returns:
            BindingGeneratorResult with statistics
        """
        self.clear()

        logger.info("Starting C# binding generation...")

        result = BindingGeneratorResult(success=True)

        # Resolve gem sources if we have a gem resolver
        if gem_resolver and self.config.generate_gem_bindings:
            self._resolve_gem_sources(reflection_data, gem_resolver)

        # Generate core bindings
        if self.config.generate_core_bindings:
            core_result = self._generate_core_bindings(reflection_data)
            result.classes_generated += core_result.classes_generated
            result.ebuses_generated += core_result.ebuses_generated

        # Generate gem bindings
        if self.config.generate_gem_bindings and gem_resolver:
            gem_result = self._generate_gem_bindings(reflection_data, gem_resolver)
            result.classes_generated += gem_result.classes_generated
            result.ebuses_generated += gem_result.ebuses_generated
            result.per_gem_stats = gem_result.per_gem_stats
            result.processed_gems = gem_result.processed_gems

        # Generate internal calls file
        self._generate_internal_calls_file(reflection_data)

        # Generate solution/project files
        if self.config.generate_solution:
            self._generate_solution_files(gem_resolver)

        result.files_written = len(self._generated_files)
        result.generated_files = [f.relative_path for f in self._generated_files]

        logger.info(
            f"Generated {result.classes_generated} classes, "
            f"{result.ebuses_generated} EBuses, {result.files_written} files"
        )

        return result

    def generate_for_gem(
        self,
        reflection_data: ReflectionData,
        gem_resolver: GemDependencyResolver,
        gem_name: str,
    ) -> BindingGeneratorResult:
        """
        Generate bindings for a specific gem and its dependencies.

        Args:
            reflection_data: The reflection data
            gem_resolver: Gem resolver for dependency information
            gem_name: Name of the gem to generate bindings for

        Returns:
            BindingGeneratorResult with statistics
        """
        self.clear()

        logger.info(f"Generating bindings for gem: {gem_name}")

        result = BindingGeneratorResult(success=True)

        # Resolve gem sources
        self._resolve_gem_sources(reflection_data, gem_resolver)

        # Get gems to process (including dependencies)
        gems_to_process = []
        if self.config.include_gem_dependencies:
            gems_to_process = gem_resolver.get_gem_dependencies(gem_name, True)
        gems_to_process.append(gem_name)

        # Process each gem
        for process_gem_name in gems_to_process:
            stats = self._process_gem(process_gem_name, reflection_data, gem_resolver)
            result.classes_generated += stats.classes_generated
            result.ebuses_generated += stats.ebuses_generated
            result.per_gem_stats.append(stats)
            result.processed_gems.append(process_gem_name)

        result.files_written = len(self._generated_files)
        result.generated_files = [f.relative_path for f in self._generated_files]

        return result

    def write_files(self) -> int:
        """
        Write all generated files to disk.

        Returns:
            Number of files written
        """
        if not self.config.write_to_disk:
            return 0

        output_dir = Path(self.config.output_directory)
        output_dir.mkdir(parents=True, exist_ok=True)

        files_written = 0
        for generated_file in self._generated_files:
            file_path = output_dir / generated_file.relative_path
            file_path.parent.mkdir(parents=True, exist_ok=True)

            # Check if we should overwrite
            if file_path.exists() and not self.config.overwrite_existing:
                # Check if content is different
                existing_content = file_path.read_text(encoding="utf-8")
                existing_checksum = hashlib.md5(
                    existing_content.encode("utf-8")
                ).hexdigest()
                if existing_checksum == generated_file.checksum:
                    continue

            file_path.write_text(generated_file.content, encoding="utf-8")
            files_written += 1
            logger.debug(f"Wrote: {file_path}")

        logger.info(f"Wrote {files_written} files to {output_dir}")
        return files_written

    def clear(self) -> None:
        """Clear all generated content."""
        self._generated_files.clear()
        self._gem_stats.clear()

    def get_generated_files(self) -> List[GeneratedFile]:
        """Get all generated files."""
        return self._generated_files.copy()

    # ============================================================
    # Core Bindings Generation
    # ============================================================

    def _generate_core_bindings(
        self, reflection_data: ReflectionData
    ) -> BindingGeneratorResult:
        """Generate core O3DE bindings."""
        result = BindingGeneratorResult(success=True)

        # Collect core classes
        core_classes = []
        for cls in reflection_data.classes.values():
            if self._is_core_class(cls):
                core_classes.append(cls)
                result.classes_generated += 1

        # Collect core EBuses
        core_ebuses = []
        for ebus in reflection_data.ebuses.values():
            if self._is_core_ebus(ebus):
                core_ebuses.append(ebus)
                result.ebuses_generated += 1

        # Group by category
        classes_by_category = self._group_by_category(core_classes)
        ebuses_by_category = self._group_by_category(core_ebuses)

        # Generate files for each category
        for category, classes in classes_by_category.items():
            self._generate_classes_file(
                self.config.core_output_subdir, category, classes, is_core=True
            )

        if self.config.generate_ebus_wrappers:
            for category, ebuses in ebuses_by_category.items():
                self._generate_ebus_file(
                    self.config.core_output_subdir, category, ebuses, is_core=True
                )

        # Generate core project file
        if (
            self.config.generate_per_gem_projects
            or self.config.generate_combined_assembly
        ):
            self._generate_project_file(self.config.core_output_subdir, "O3DE.Core", [])

        return result

    def _is_core_class(self, cls: ReflectedClass) -> bool:
        """Check if a class belongs to core bindings."""
        # Check category
        if cls.category in self.config.core_categories:
            return True

        # Check name prefix
        for prefix in self.config.core_prefixes:
            if cls.name.startswith(prefix):
                return True

        # Check if no gem source assigned (default to core)
        if not cls.source_gem_name or cls.source_gem_name == "O3DE.Core":
            return True

        return False

    def _is_core_ebus(self, ebus: ReflectedEBus) -> bool:
        """Check if an EBus belongs to core bindings."""
        if ebus.category in self.config.core_categories:
            return True

        for prefix in self.config.core_prefixes:
            if ebus.name.startswith(prefix):
                return True

        if not ebus.source_gem_name or ebus.source_gem_name == "O3DE.Core":
            return True

        return False

    # ============================================================
    # Gem Bindings Generation
    # ============================================================

    def _generate_gem_bindings(
        self, reflection_data: ReflectionData, gem_resolver: GemDependencyResolver
    ) -> BindingGeneratorResult:
        """Generate per-gem bindings."""
        result = BindingGeneratorResult(success=True)

        # Get gems to process
        gems_to_process = self._get_gems_to_process(gem_resolver)

        # Process each gem
        for gem_name in gems_to_process:
            if not self._should_process_gem(gem_name):
                continue

            stats = self._process_gem(gem_name, reflection_data, gem_resolver)
            result.classes_generated += stats.classes_generated
            result.ebuses_generated += stats.ebuses_generated
            result.per_gem_stats.append(stats)
            result.processed_gems.append(gem_name)

        return result

    def _process_gem(
        self,
        gem_name: str,
        reflection_data: ReflectionData,
        gem_resolver: GemDependencyResolver,
    ) -> GemBindingStats:
        """Process a single gem's classes and EBuses."""
        stats = GemBindingStats(gem_name=gem_name)

        logger.debug(f"Processing gem: {gem_name}")

        # Get classes for this gem
        gem_classes = [
            cls
            for cls in reflection_data.classes.values()
            if cls.source_gem_name == gem_name and not self._is_core_class(cls)
        ]

        # Get EBuses for this gem
        gem_ebuses = [
            ebus
            for ebus in reflection_data.ebuses.values()
            if ebus.source_gem_name == gem_name and not self._is_core_ebus(ebus)
        ]

        if not gem_classes and not gem_ebuses:
            return stats

        # Group by category
        classes_by_category = self._group_by_category(gem_classes)
        ebuses_by_category = self._group_by_category(gem_ebuses)

        # Determine output directory
        if self.config.separate_gem_directories:
            output_subdir = self._get_safe_filename(gem_name)
        else:
            output_subdir = ""

        files_before = len(self._generated_files)

        # Generate files for each category
        for category, classes in classes_by_category.items():
            self._generate_classes_file(
                output_subdir, category, classes, gem_name=gem_name
            )
            stats.classes_generated += len(classes)

        if self.config.generate_ebus_wrappers:
            for category, ebuses in ebuses_by_category.items():
                self._generate_ebus_file(
                    output_subdir, category, ebuses, gem_name=gem_name
                )
                stats.ebuses_generated += len(ebuses)

        # Generate gem project file
        if self.config.generate_per_gem_projects:
            gem_descriptor = gem_resolver.get_gem(gem_name)
            dependencies = gem_resolver.get_gem_dependencies(gem_name, False)
            self._generate_project_file(output_subdir, gem_name, dependencies)
            self._generate_assembly_info(output_subdir, gem_name, gem_descriptor)

        stats.files_generated = len(self._generated_files) - files_before
        stats.generated_files = [
            f.relative_path for f in self._generated_files[files_before:]
        ]

        self._gem_stats[gem_name] = stats
        return stats

    def _resolve_gem_sources(
        self, reflection_data: ReflectionData, gem_resolver: GemDependencyResolver
    ) -> None:
        """Resolve gem sources for all reflected types."""
        logger.debug("Resolving gem sources for reflected types...")

        for cls in reflection_data.classes.values():
            if not cls.source_gem_name:
                cls.source_gem_name = gem_resolver.resolve_gem_for_class(
                    cls.name, cls.category
                )

        for ebus in reflection_data.ebuses.values():
            if not ebus.source_gem_name:
                ebus.source_gem_name = gem_resolver.resolve_gem_for_class(
                    ebus.name, ebus.category
                )

    # ============================================================
    # File Generation
    # ============================================================

    def _generate_classes_file(
        self,
        output_subdir: str,
        category: str,
        classes: List[ReflectedClass],
        is_core: bool = False,
        gem_name: str = "",
    ) -> None:
        """Generate a classes file for a category."""
        if not classes:
            return

        content = []

        # File header
        content.append(self._generate_file_header())
        content.append("")

        # Using statements
        content.append(self._generate_usings())
        content.append("")

        # Namespace
        if is_core:
            namespace = self.config.core_namespace
        else:
            namespace = self._get_gem_namespace(gem_name)

        if category and category != "Core":
            namespace += f".{self._to_pascal_case(category)}"

        content.append(f"namespace {namespace}")
        content.append("{")

        # Sort classes by name
        sorted_classes = sorted(classes, key=lambda c: c.name)

        # Generate each class
        for i, cls in enumerate(sorted_classes):
            content.append(self._generate_class(cls, indent=1))
            if i < len(sorted_classes) - 1:
                content.append("")

        content.append("}")

        # Create file entry
        filename = self._get_category_filename(category) + ".cs"
        if output_subdir:
            relative_path = f"{output_subdir}/{filename}"
        else:
            relative_path = filename

        self._generated_files.append(
            GeneratedFile(relative_path=relative_path, content="\n".join(content))
        )

    def _generate_ebus_file(
        self,
        output_subdir: str,
        category: str,
        ebuses: List[ReflectedEBus],
        is_core: bool = False,
        gem_name: str = "",
    ) -> None:
        """Generate an EBus wrappers file for a category."""
        if not ebuses:
            return

        content = []

        # File header
        content.append(self._generate_file_header())
        content.append("")

        # Using statements
        content.append(self._generate_usings())
        content.append("")

        # Namespace
        if is_core:
            namespace = self.config.core_namespace
        else:
            namespace = self._get_gem_namespace(gem_name)

        if category and category != "Core":
            namespace += f".{self._to_pascal_case(category)}"

        content.append(f"namespace {namespace}.EBus")
        content.append("{")

        # Sort EBuses by name
        sorted_ebuses = sorted(ebuses, key=lambda e: e.name)

        # Generate each EBus
        for i, ebus in enumerate(sorted_ebuses):
            content.append(self._generate_ebus(ebus, indent=1))
            if i < len(sorted_ebuses) - 1:
                content.append("")

        content.append("}")

        # Create file entry
        filename = self._get_category_filename(category) + ".EBus.cs"
        if output_subdir:
            relative_path = f"{output_subdir}/{filename}"
        else:
            relative_path = filename

        self._generated_files.append(
            GeneratedFile(relative_path=relative_path, content="\n".join(content))
        )

    def _generate_internal_calls_file(self, reflection_data: ReflectionData) -> None:
        """Generate the internal calls file for native method declarations."""
        content = []

        content.append(self._generate_file_header())
        content.append("")
        content.append("using System;")
        content.append("using System.Runtime.CompilerServices;")
        content.append("using System.Runtime.InteropServices;")
        content.append("")
        content.append(f"namespace {self.config.root_namespace}.Internal")
        content.append("{")
        content.append("    /// <summary>")
        content.append("    /// Internal calls to native O3DE methods.")
        content.append(
            "    /// These are registered by the O3DESharp native module via Coral."
        )
        content.append("    /// </summary>")
        content.append("    internal static class NativeMethods")
        content.append("    {")

        # Generate internal call declarations for each class method
        for cls in sorted(reflection_data.classes.values(), key=lambda c: c.name):
            for method in cls.methods:
                internal_call = self._generate_internal_call_signature(method, cls)
                content.append(f"        {internal_call}")

        content.append("    }")
        content.append("}")

        self._generated_files.append(
            GeneratedFile(relative_path="InternalCalls.cs", content="\n".join(content))
        )

    def _generate_project_file(
        self, output_subdir: str, project_name: str, dependencies: List[str]
    ) -> None:
        """Generate a .csproj file."""
        content = []

        content.append('<Project Sdk="Microsoft.NET.Sdk">')
        content.append("")
        content.append("  <PropertyGroup>")
        content.append(
            f"    <TargetFramework>{self.config.target_framework}</TargetFramework>"
        )
        content.append("    <ImplicitUsings>enable</ImplicitUsings>")
        content.append("    <Nullable>enable</Nullable>")
        content.append(
            f"    <AssemblyName>{self._get_safe_filename(project_name)}</AssemblyName>"
        )
        content.append(
            f"    <RootNamespace>{self._get_gem_namespace(project_name)}</RootNamespace>"
        )
        content.append("  </PropertyGroup>")
        content.append("")

        # Add project references for dependencies
        if dependencies:
            content.append("  <ItemGroup>")
            for dep in dependencies:
                dep_filename = self._get_safe_filename(dep)
                content.append(
                    f'    <ProjectReference Include="../{dep_filename}/{dep_filename}.csproj" />'
                )
            content.append("  </ItemGroup>")
            content.append("")

        # Reference to core assembly
        content.append("  <ItemGroup>")
        content.append('    <Reference Include="O3DE.Sharp.Core">')
        content.append(
            "      <HintPath>$(O3DESharpCorePath)/O3DE.Sharp.Core.dll</HintPath>"
        )
        content.append("    </Reference>")
        content.append("  </ItemGroup>")
        content.append("")
        content.append("</Project>")

        filename = f"{self._get_safe_filename(project_name)}.csproj"
        if output_subdir:
            relative_path = f"{output_subdir}/{filename}"
        else:
            relative_path = filename

        self._generated_files.append(
            GeneratedFile(relative_path=relative_path, content="\n".join(content))
        )

    def _generate_assembly_info(
        self,
        output_subdir: str,
        gem_name: str,
        gem_descriptor: Optional[GemDescriptor],
    ) -> None:
        """Generate AssemblyInfo.cs for a gem."""
        content = []

        content.append(self._generate_file_header())
        content.append("")
        content.append("using System.Reflection;")
        content.append("using System.Runtime.CompilerServices;")
        content.append("using System.Runtime.InteropServices;")
        content.append("")

        display_name = gem_descriptor.display_name if gem_descriptor else gem_name
        content.append(f'[assembly: AssemblyTitle("{display_name}")]')

        if gem_descriptor and gem_descriptor.summary:
            content.append(
                f'[assembly: AssemblyDescription("{gem_descriptor.summary}")]'
            )

        version = gem_descriptor.version if gem_descriptor else "1.0.0"
        content.append(f'[assembly: AssemblyVersion("{version}")]')
        content.append(f'[assembly: AssemblyFileVersion("{version}")]')
        content.append("[assembly: ComVisible(false)]")

        if output_subdir:
            relative_path = f"{output_subdir}/AssemblyInfo.cs"
        else:
            relative_path = "AssemblyInfo.cs"

        self._generated_files.append(
            GeneratedFile(relative_path=relative_path, content="\n".join(content))
        )

    def _generate_solution_files(
        self, gem_resolver: Optional[GemDependencyResolver]
    ) -> None:
        """Generate Visual Studio solution file."""
        content = []

        content.append("")
        content.append("Microsoft Visual Studio Solution File, Format Version 12.00")
        content.append("# Visual Studio Version 17")
        content.append("VisualStudioVersion = 17.0.31903.59")
        content.append("MinimumVisualStudioVersion = 10.0.40219.1")

        projects = []

        # Add core project
        if self.config.generate_core_bindings:
            core_guid = self._generate_guid("O3DE.Core")
            core_path = f"{self.config.core_output_subdir}/O3DE.Core.csproj"
            projects.append(("O3DE.Core", core_path, core_guid))

        # Add gem projects
        for gem_name in self._gem_stats.keys():
            gem_guid = self._generate_guid(gem_name)
            gem_filename = self._get_safe_filename(gem_name)
            gem_path = f"{gem_filename}/{gem_filename}.csproj"
            projects.append((gem_name, gem_path, gem_guid))

        # Write project entries
        for name, path, guid in projects:
            content.append(
                f'Project("{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}") = "{name}", "{path}", "{{{guid}}}"'
            )
            content.append("EndProject")

        content.append("Global")
        content.append(
            "    GlobalSection(SolutionConfigurationPlatforms) = preSolution"
        )
        content.append("        Debug|Any CPU = Debug|Any CPU")
        content.append("        Release|Any CPU = Release|Any CPU")
        content.append("    EndGlobalSection")
        content.append(
            "    GlobalSection(ProjectConfigurationPlatforms) = postSolution"
        )

        for _, _, guid in projects:
            content.append(
                f"        {{{guid}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU"
            )
            content.append(f"        {{{guid}}}.Debug|Any CPU.Build.0 = Debug|Any CPU")
            content.append(
                f"        {{{guid}}}.Release|Any CPU.ActiveCfg = Release|Any CPU"
            )
            content.append(
                f"        {{{guid}}}.Release|Any CPU.Build.0 = Release|Any CPU"
            )

        content.append("    EndGlobalSection")
        content.append("EndGlobal")

        self._generated_files.append(
            GeneratedFile(
                relative_path=f"{self.config.solution_name}.sln",
                content="\n".join(content),
            )
        )

    # ============================================================
    # Class/Method/Property Generation
    # ============================================================

    def _generate_class(self, cls: ReflectedClass, indent: int = 0) -> str:
        """Generate C# code for a class."""
        lines = []
        ind = self._indent(indent)

        # XML documentation
        if self.config.generate_documentation and cls.description:
            lines.append(f"{ind}/// <summary>")
            lines.append(f"{ind}/// {self._escape_xml(cls.description)}")
            lines.append(f"{ind}/// </summary>")

        # Obsolete attribute
        if self.config.mark_deprecated_as_obsolete and cls.is_deprecated:
            lines.append(f'{ind}[Obsolete("This class is deprecated.")]')

        # Class declaration
        partial = "partial " if self.config.generate_partial_classes else ""
        class_name = self._make_safe_name(cls.name)

        # Base class
        base_clause = ""
        if cls.base_classes:
            base_name = self._get_csharp_type_name(cls.base_classes[0])
            base_clause = f" : {base_name}"

        lines.append(f"{ind}public {partial}class {class_name}{base_clause}")
        lines.append(f"{ind}{{")

        # Native handle field
        lines.append(f"{ind}    private IntPtr _nativeHandle;")
        lines.append("")

        # Constructors
        for ctor in cls.constructors:
            lines.append(self._generate_constructor(ctor, cls, indent + 1))
            lines.append("")

        # Properties
        for prop in cls.properties:
            lines.append(self._generate_property(prop, cls, indent + 1))
            lines.append("")

        # Methods
        for method in cls.methods:
            lines.append(self._generate_method(method, cls, indent + 1))
            lines.append("")

        # Remove trailing empty line
        if lines and lines[-1] == "":
            lines.pop()

        lines.append(f"{ind}}}")

        return "\n".join(lines)

    def _generate_method(
        self, method: ReflectedMethod, cls: Optional[ReflectedClass], indent: int = 0
    ) -> str:
        """Generate C# code for a method."""
        lines = []
        ind = self._indent(indent)

        # XML documentation
        if self.config.generate_documentation:
            lines.append(f"{ind}/// <summary>")
            desc = method.description if method.description else f"{method.name} method"
            lines.append(f"{ind}/// {self._escape_xml(desc)}")
            lines.append(f"{ind}/// </summary>")

            for param in method.parameters:
                lines.append(
                    f'{ind}/// <param name="{self._to_camel_case(param.name)}">'
                    f"Parameter of type {param.type_name}</param>"
                )

            if method.return_type.type_name != "void":
                lines.append(
                    f"{ind}/// <returns>{method.return_type.type_name}</returns>"
                )

        # Obsolete attribute
        if self.config.mark_deprecated_as_obsolete and method.is_deprecated:
            msg = method.deprecation_message or "This method is deprecated."
            lines.append(f'{ind}[Obsolete("{msg}")]')

        # Method signature
        static = "static " if method.is_static else ""
        return_type = self._get_csharp_type_name(method.return_type.type_name)
        method_name = self._to_pascal_case(method.name)

        # Parameters
        params = []
        for param in method.parameters:
            param_type = self._get_csharp_type_name(param.type_name)
            param_name = self._make_safe_name(self._to_camel_case(param.name))
            params.append(f"{param_type} {param_name}")

        param_list = ", ".join(params)

        lines.append(f"{ind}public {static}{return_type} {method_name}({param_list})")
        lines.append(f"{ind}{{")

        # Method body - call native method
        class_name = cls.name if cls else "Global"
        internal_call = f"NativeMethods.{class_name}_{method.name}"

        args = []
        if not method.is_static and cls:
            args.append("_nativeHandle")
        for param in method.parameters:
            args.append(self._make_safe_name(self._to_camel_case(param.name)))

        args_str = ", ".join(args)

        if return_type == "void":
            lines.append(f"{ind}    {internal_call}({args_str});")
        else:
            lines.append(f"{ind}    return {internal_call}({args_str});")

        lines.append(f"{ind}}}")

        return "\n".join(lines)

    def _generate_property(
        self, prop: ReflectedProperty, cls: Optional[ReflectedClass], indent: int = 0
    ) -> str:
        """Generate C# code for a property."""
        lines = []
        ind = self._indent(indent)

        # XML documentation
        if self.config.generate_documentation and prop.description:
            lines.append(f"{ind}/// <summary>")
            lines.append(f"{ind}/// {self._escape_xml(prop.description)}")
            lines.append(f"{ind}/// </summary>")

        # Obsolete attribute
        if self.config.mark_deprecated_as_obsolete and prop.is_deprecated:
            lines.append(f'{ind}[Obsolete("This property is deprecated.")]')

        prop_type = self._get_csharp_type_name(prop.value_type.type_name)
        prop_name = self._to_pascal_case(prop.name)
        class_name = cls.name if cls else "Global"

        lines.append(f"{ind}public {prop_type} {prop_name}")
        lines.append(f"{ind}{{")

        if prop.has_getter:
            lines.append(
                f"{ind}    get => NativeMethods.{class_name}_Get{prop.name}(_nativeHandle);"
            )

        if prop.has_setter:
            lines.append(
                f"{ind}    set => NativeMethods.{class_name}_Set{prop.name}(_nativeHandle, value);"
            )

        lines.append(f"{ind}}}")

        return "\n".join(lines)

    def _generate_constructor(
        self, ctor: ReflectedMethod, cls: ReflectedClass, indent: int = 0
    ) -> str:
        """Generate C# code for a constructor."""
        lines = []
        ind = self._indent(indent)

        class_name = self._make_safe_name(cls.name)

        # Parameters
        params = []
        for param in ctor.parameters:
            param_type = self._get_csharp_type_name(param.type_name)
            param_name = self._make_safe_name(self._to_camel_case(param.name))
            params.append(f"{param_type} {param_name}")

        param_list = ", ".join(params)

        lines.append(f"{ind}public {class_name}({param_list})")
        lines.append(f"{ind}{{")

        # Constructor body
        args = [
            self._make_safe_name(self._to_camel_case(p.name)) for p in ctor.parameters
        ]
        args_str = ", ".join(args)
        lines.append(
            f"{ind}    _nativeHandle = NativeMethods.{cls.name}_Create({args_str});"
        )

        lines.append(f"{ind}}}")

        return "\n".join(lines)

    def _generate_ebus(self, ebus: ReflectedEBus, indent: int = 0) -> str:
        """Generate C# code for an EBus wrapper."""
        lines = []
        ind = self._indent(indent)

        # XML documentation
        if self.config.generate_documentation and ebus.description:
            lines.append(f"{ind}/// <summary>")
            lines.append(f"{ind}/// {self._escape_xml(ebus.description)}")
            lines.append(f"{ind}/// </summary>")

        ebus_name = self._make_safe_name(ebus.name)
        lines.append(f"{ind}public static class {ebus_name}")
        lines.append(f"{ind}{{")

        # Generate event methods
        for event in ebus.events:
            lines.append(self._generate_ebus_event(event, ebus, indent + 1))
            lines.append("")

        # Remove trailing empty line
        if lines and lines[-1] == "":
            lines.pop()

        lines.append(f"{ind}}}")

        return "\n".join(lines)

    def _generate_ebus_event(
        self, event: ReflectedEBusEvent, ebus: ReflectedEBus, indent: int = 0
    ) -> str:
        """Generate C# code for an EBus event method."""
        lines = []
        ind = self._indent(indent)

        return_type = self._get_csharp_type_name(event.return_type.type_name)
        method_name = self._to_pascal_case(event.name)

        # Parameters
        params = []
        if not event.is_broadcast and ebus.address_type:
            addr_type = self._get_csharp_type_name(ebus.address_type.type_name)
            params.append(f"{addr_type} address")

        for param in event.parameters:
            param_type = self._get_csharp_type_name(param.type_name)
            param_name = self._make_safe_name(self._to_camel_case(param.name))
            params.append(f"{param_type} {param_name}")

        param_list = ", ".join(params)

        # Method signature
        if event.is_broadcast:
            lines.append(
                f"{ind}public static {return_type} Broadcast{method_name}({param_list})"
            )
        else:
            lines.append(
                f"{ind}public static {return_type} Event{method_name}({param_list})"
            )

        lines.append(f"{ind}{{")

        # Method body
        args = []
        if not event.is_broadcast:
            args.append("address")
        for param in event.parameters:
            args.append(self._make_safe_name(self._to_camel_case(param.name)))

        args_str = ", ".join(args)
        internal_method = f"NativeMethods.{ebus.name}_{event.name}"

        if return_type == "void":
            lines.append(f"{ind}    {internal_method}({args_str});")
        else:
            lines.append(f"{ind}    return {internal_method}({args_str});")

        lines.append(f"{ind}}}")

        return "\n".join(lines)

    def _generate_internal_call_signature(
        self, method: ReflectedMethod, cls: Optional[ReflectedClass]
    ) -> str:
        """Generate the internal call declaration for a method."""
        return_type = self._get_csharp_type_name(method.return_type.type_name)
        class_name = cls.name if cls else "Global"
        method_name = f"{class_name}_{method.name}"

        params = []
        if not method.is_static and cls:
            params.append("IntPtr instance")

        for param in method.parameters:
            param_type = self._get_csharp_type_name(param.type_name)
            param_name = self._make_safe_name(self._to_camel_case(param.name))
            params.append(f"{param_type} {param_name}")

        param_list = ", ".join(params)

        return f"[MethodImpl(MethodImplOptions.InternalCall)] internal static extern {return_type} {method_name}({param_list});"

    # ============================================================
    # Helper Methods
    # ============================================================

    def _generate_file_header(self) -> str:
        """Generate file header comment."""
        if self.config.file_header:
            return self.config.file_header

        now = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        return f"""//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by O3DESharp Binding Generator.
//     Generated: {now}
//
//     Changes to this file may be overwritten when regenerated.
// </auto-generated>
//------------------------------------------------------------------------------"""

    def _generate_usings(self) -> str:
        """Generate using statements."""
        return """using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using O3DE.Core;"""

    def _indent(self, level: int) -> str:
        """Generate indentation string."""
        return "    " * level

    def _get_csharp_type_name(self, type_name: str) -> str:
        """Convert an O3DE type name to C# type name."""
        if type_name in TYPE_MAPPINGS:
            return TYPE_MAPPINGS[type_name]

        # Handle pointers and references
        clean_name = type_name.replace("*", "").replace("&", "").strip()
        if clean_name in TYPE_MAPPINGS:
            return TYPE_MAPPINGS[clean_name]

        # Remove AZ:: and AZStd:: prefixes
        if clean_name.startswith("AZ::"):
            clean_name = clean_name[4:]
        elif clean_name.startswith("AZStd::"):
            clean_name = clean_name[7:]

        if clean_name in TYPE_MAPPINGS:
            return TYPE_MAPPINGS[clean_name]

        # Default: return the cleaned name
        return self._make_safe_name(clean_name)

    def _make_safe_name(self, name: str) -> str:
        """Make a name safe for C# (escape keywords)."""
        if name.lower() in CSHARP_KEYWORDS:
            return f"@{name}"
        return name

    def _to_pascal_case(self, name: str) -> str:
        """Convert a name to PascalCase."""
        if not name:
            return name

        # Handle snake_case
        if "_" in name:
            parts = name.split("_")
            return "".join(part.capitalize() for part in parts if part)

        # Handle camelCase
        return name[0].upper() + name[1:]

    def _to_camel_case(self, name: str) -> str:
        """Convert a name to camelCase."""
        pascal = self._to_pascal_case(name)
        if not pascal:
            return pascal
        return pascal[0].lower() + pascal[1:]

    def _escape_xml(self, text: str) -> str:
        """Escape text for XML documentation."""
        return (
            text.replace("&", "&amp;")
            .replace("<", "&lt;")
            .replace(">", "&gt;")
            .replace('"', "&quot;")
        )

    def _get_gem_namespace(self, gem_name: str) -> str:
        """Get the C# namespace for a gem."""
        clean_name = gem_name.replace("_", ".").replace("-", ".")
        while ".." in clean_name:
            clean_name = clean_name.replace("..", ".")
        return f"{self.config.root_namespace}.{clean_name}"

    def _get_safe_filename(self, name: str) -> str:
        """Get a safe filename from a name."""
        invalid_chars = [":", "<", ">", "|", "?", "*", "/", "\\", '"']
        result = name
        for char in invalid_chars:
            result = result.replace(char, "_")
        return result

    def _get_category_filename(self, category: str) -> str:
        """Get a filename for a category."""
        if not category:
            return "Core"
        return self._to_pascal_case(self._get_safe_filename(category))

    def _group_by_category(self, items: List[Any]) -> Dict[str, List[Any]]:
        """Group items by their category."""
        result: Dict[str, List[Any]] = {}
        for item in items:
            category = getattr(item, "category", "") or "Core"
            if category not in result:
                result[category] = []
            result[category].append(item)
        return result

    def _get_gems_to_process(self, gem_resolver: GemDependencyResolver) -> List[str]:
        """Get the list of gems to process."""
        if self.config.include_gems:
            gems = self.config.include_gems.copy()
        else:
            gems = gem_resolver.get_active_gem_names()

        # Include dependencies if configured
        if self.config.include_gem_dependencies:
            all_gems = set()
            ordered_gems = []

            for gem_name in gems:
                deps = gem_resolver.get_gem_dependencies(gem_name, True)
                for dep in deps:
                    if dep not in all_gems:
                        all_gems.add(dep)
                        ordered_gems.append(dep)

                if gem_name not in all_gems:
                    all_gems.add(gem_name)
                    ordered_gems.append(gem_name)

            return ordered_gems

        return gems

    def _should_process_gem(self, gem_name: str) -> bool:
        """Check if a gem should be processed."""
        if self.config.exclude_gems and gem_name in self.config.exclude_gems:
            return False

        if self.config.include_gems:
            return gem_name in self.config.include_gems

        return True

    def _generate_guid(self, name: str) -> str:
        """Generate a deterministic GUID from a name."""
        hash_bytes = hashlib.md5(name.encode("utf-8")).digest()
        guid = f"{hash_bytes[0]:02X}{hash_bytes[1]:02X}{hash_bytes[2]:02X}{hash_bytes[3]:02X}-"
        guid += f"{hash_bytes[4]:02X}{hash_bytes[5]:02X}-"
        guid += f"{hash_bytes[6]:02X}{hash_bytes[7]:02X}-"
        guid += f"{hash_bytes[8]:02X}{hash_bytes[9]:02X}-"
        guid += "".join(f"{b:02X}" for b in hash_bytes[10:16])
        return guid


# ============================================================
# Reflection Data Loading
# ============================================================


def load_reflection_data_from_json(json_path: str) -> ReflectionData:
    """
    Load reflection data from a JSON file.

    The JSON file should be exported from the C++ BehaviorContextReflector.

    Args:
        json_path: Path to the JSON file

    Returns:
        ReflectionData object
    """
    with open(json_path, "r", encoding="utf-8") as f:
        data = json.load(f)

    reflection_data = ReflectionData()

    # Load classes
    for class_data in data.get("classes", []):
        cls = _parse_class_from_json(class_data)
        reflection_data.classes[cls.name] = cls

    # Load EBuses
    for ebus_data in data.get("ebuses", []):
        ebus = _parse_ebus_from_json(ebus_data)
        reflection_data.ebuses[ebus.name] = ebus

    # Load global methods
    for method_data in data.get("global_methods", []):
        method = _parse_method_from_json(method_data)
        reflection_data.global_methods.append(method)

    # Load global properties
    for prop_data in data.get("global_properties", []):
        prop = _parse_property_from_json(prop_data)
        reflection_data.global_properties.append(prop)

    return reflection_data


def _parse_class_from_json(data: Dict[str, Any]) -> ReflectedClass:
    """Parse a ReflectedClass from JSON data."""
    cls = ReflectedClass(
        name=data.get("name", ""),
        type_id=data.get("type_id", ""),
        base_classes=data.get("base_classes", []),
        description=data.get("description", ""),
        category=data.get("category", ""),
        is_deprecated=data.get("is_deprecated", False),
        source_gem_name=data.get("source_gem_name", ""),
    )

    for method_data in data.get("methods", []):
        cls.methods.append(_parse_method_from_json(method_data))

    for prop_data in data.get("properties", []):
        cls.properties.append(_parse_property_from_json(prop_data))

    for ctor_data in data.get("constructors", []):
        cls.constructors.append(_parse_method_from_json(ctor_data))

    return cls


def _parse_ebus_from_json(data: Dict[str, Any]) -> ReflectedEBus:
    """Parse a ReflectedEBus from JSON data."""
    ebus = ReflectedEBus(
        name=data.get("name", ""),
        type_id=data.get("type_id", ""),
        description=data.get("description", ""),
        category=data.get("category", ""),
        source_gem_name=data.get("source_gem_name", ""),
    )

    if "address_type" in data:
        ebus.address_type = _parse_parameter_from_json(data["address_type"])

    for event_data in data.get("events", []):
        event = ReflectedEBusEvent(
            name=event_data.get("name", ""),
            bus_name=ebus.name,
            is_broadcast=event_data.get("is_broadcast", True),
        )

        if "return_type" in event_data:
            event.return_type = _parse_parameter_from_json(event_data["return_type"])

        for param_data in event_data.get("parameters", []):
            event.parameters.append(_parse_parameter_from_json(param_data))

        ebus.events.append(event)

    return ebus


def _parse_method_from_json(data: Dict[str, Any]) -> ReflectedMethod:
    """Parse a ReflectedMethod from JSON data."""
    method = ReflectedMethod(
        name=data.get("name", ""),
        class_name=data.get("class_name", ""),
        is_static=data.get("is_static", False),
        is_const=data.get("is_const", False),
        description=data.get("description", ""),
        category=data.get("category", ""),
        is_deprecated=data.get("is_deprecated", False),
        deprecation_message=data.get("deprecation_message", ""),
    )

    if "return_type" in data:
        method.return_type = _parse_parameter_from_json(data["return_type"])

    for param_data in data.get("parameters", []):
        method.parameters.append(_parse_parameter_from_json(param_data))

    return method


def _parse_property_from_json(data: Dict[str, Any]) -> ReflectedProperty:
    """Parse a ReflectedProperty from JSON data."""
    prop = ReflectedProperty(
        name=data.get("name", ""),
        class_name=data.get("class_name", ""),
        has_getter=data.get("has_getter", True),
        has_setter=data.get("has_setter", True),
        description=data.get("description", ""),
        is_deprecated=data.get("is_deprecated", False),
    )

    if "value_type" in data:
        prop.value_type = _parse_parameter_from_json(data["value_type"])

    return prop


def _parse_parameter_from_json(data: Dict[str, Any]) -> ReflectedParameter:
    """Parse a ReflectedParameter from JSON data."""
    return ReflectedParameter(
        name=data.get("name", ""),
        type_name=data.get("type_name", "object"),
        type_id=data.get("type_id", ""),
        is_pointer=data.get("is_pointer", False),
        is_reference=data.get("is_reference", False),
        is_const=data.get("is_const", False),
        marshal_type=data.get("marshal_type", "Unknown"),
    )


# ============================================================
# CLI Interface
# ============================================================


def main():
    """Command-line interface for binding generation."""
    import argparse

    parser = argparse.ArgumentParser(
        description="Generate C# bindings from O3DE BehaviorContext reflection data"
    )
    parser.add_argument(
        "--reflection-data",
        "-r",
        required=True,
        help="Path to reflection data JSON file",
    )
    parser.add_argument(
        "--output", "-o", default="Generated/CSharp", help="Output directory"
    )
    parser.add_argument(
        "--project", "-p", help="Path to O3DE project (for gem discovery)"
    )
    parser.add_argument(
        "--namespace", "-n", default="O3DE.Generated", help="Root namespace"
    )
    parser.add_argument("--gem", "-g", help="Generate bindings for a specific gem only")
    parser.add_argument(
        "--no-core", action="store_true", help="Skip core bindings generation"
    )
    parser.add_argument(
        "--no-gems", action="store_true", help="Skip gem bindings generation"
    )
    parser.add_argument(
        "--per-gem-projects",
        action="store_true",
        help="Generate separate .csproj per gem",
    )
    parser.add_argument(
        "--verbose", "-v", action="store_true", help="Enable verbose output"
    )

    args = parser.parse_args()

    if args.verbose:
        logging.basicConfig(level=logging.DEBUG)
    else:
        logging.basicConfig(level=logging.INFO)

    # Load reflection data
    logger.info(f"Loading reflection data from: {args.reflection_data}")
    reflection_data = load_reflection_data_from_json(args.reflection_data)

    logger.info(
        f"Loaded {len(reflection_data.classes)} classes, "
        f"{len(reflection_data.ebuses)} EBuses"
    )

    # Configure generator
    config = BindingGeneratorConfig()
    config.output_directory = args.output
    config.root_namespace = args.namespace
    config.generate_core_bindings = not args.no_core
    config.generate_gem_bindings = not args.no_gems
    config.generate_per_gem_projects = args.per_gem_projects

    # Create gem resolver if project specified
    gem_resolver = None
    if args.project and not args.no_gems:
        gem_resolver = GemDependencyResolver()
        gem_result = gem_resolver.discover_gems_from_project(args.project)
        if not gem_result.success:
            logger.error(f"Failed to discover gems: {gem_result.error_message}")
            return 1
        logger.info(f"Discovered {len(gem_result.active_gem_names)} active gems")

    # Create generator
    generator = CSharpBindingGenerator(config)

    # Generate bindings
    if args.gem:
        if not gem_resolver:
            logger.error("--project is required when using --gem")
            return 1
        result = generator.generate_for_gem(reflection_data, gem_resolver, args.gem)
    else:
        result = generator.generate_from_reflection_data(reflection_data, gem_resolver)

    if not result.success:
        logger.error(f"Generation failed: {result.error_message}")
        return 1

    # Write files
    files_written = generator.write_files()

    logger.info(
        f"Successfully generated {result.classes_generated} classes, "
        f"{result.ebuses_generated} EBuses in {files_written} files"
    )

    if result.processed_gems:
        logger.info(f"Processed gems: {', '.join(result.processed_gems)}")

    return 0


if __name__ == "__main__":
    import sys

    sys.exit(main())
