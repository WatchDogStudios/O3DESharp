#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""
O3DESharp Editor Scripts Package

This package provides Python utilities for generating C# bindings from O3DE's
BehaviorContext reflection data.

Main modules:
    - gem_dependency_resolver: Discovers gems and resolves dependencies
    - csharp_binding_generator: Generates C# wrapper classes from reflection data
    - generate_bindings: Main entry point for binding generation

Usage:
    # From command line
    python -m generate_bindings --reflection-data data.json --project /path/to/project

    # From O3DE Editor Python console
    import csharp_editor_bootstrap
    csharp_editor_bootstrap.open_csharp_project_manager()

    # Using individual modules
    import gem_dependency_resolver
    import csharp_binding_generator
"""

import sys
from pathlib import Path

# Ensure this package's directory is in sys.path for imports
_PACKAGE_DIR = Path(__file__).parent.resolve()
if str(_PACKAGE_DIR) not in sys.path:
    sys.path.insert(0, str(_PACKAGE_DIR))

from .csharp_binding_generator import (
    BindingGeneratorConfig,
    BindingGeneratorResult,
    CSharpBindingGenerator,
    ReflectedClass,
    ReflectedEBus,
    ReflectedEBusEvent,
    ReflectedMethod,
    ReflectedParameter,
    ReflectedProperty,
    ReflectionData,
    load_reflection_data_from_json,
)
from .gem_dependency_resolver import (
    GemDependencyResolver,
    GemDescriptor,
    GemMappingConfig,
    GemResolutionResult,
    discover_engine_gems,
    discover_project_gems,
)
from .generate_bindings import (
    BindingGenerationOrchestrator,
    generate_all_bindings,
    generate_core_bindings,
    generate_gem_bindings,
    get_gem_dependencies,
    list_available_gems,
)

# Editor tools for C# script project management
from . import csharp_project_manager
from . import csharp_editor_tools
from . import csharp_editor_bootstrap

__all__ = [
    # Gem dependency resolver
    "GemDependencyResolver",
    "GemDescriptor",
    "GemResolutionResult",
    "GemMappingConfig",
    "discover_project_gems",
    "discover_engine_gems",
    # C# binding generator
    "CSharpBindingGenerator",
    "BindingGeneratorConfig",
    "BindingGeneratorResult",
    "ReflectionData",
    "ReflectedClass",
    "ReflectedMethod",
    "ReflectedProperty",
    "ReflectedEBus",
    "ReflectedEBusEvent",
    "ReflectedParameter",
    "load_reflection_data_from_json",
    # Main orchestrator
    "BindingGenerationOrchestrator",
    "generate_all_bindings",
    "generate_gem_bindings",
    "generate_core_bindings",
    "list_available_gems",
    "get_gem_dependencies",
    # C# project management
    "csharp_project_manager",
    "csharp_editor_tools",
    "csharp_editor_bootstrap",
]

__version__ = "1.0.0"


# Convenience functions for C# project management
def get_project_manager():
    """Get a CSharpProjectManager instance"""
    return csharp_project_manager.CSharpProjectManager()


def show_project_manager():
    """Show the C# Project Manager window"""
    csharp_editor_bootstrap.open_csharp_project_manager()


def create_project():
    """Show the Create C# Project dialog"""
    csharp_editor_bootstrap.create_csharp_project()


def create_script():
    """Show the Create C# Script dialog"""
    csharp_editor_bootstrap.create_csharp_script()


def browse_scripts():
    """Show the C# Script Browser dialog"""
    return csharp_editor_bootstrap.browse_csharp_scripts()


def build_all():
    """Build all C# projects"""
    csharp_editor_bootstrap.build_csharp_projects()
