#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""
O3DESharp Editor Scripts Package

This package provides Python utilities for the O3DESharp editor integration.

Main entry points:
    - ClangSharpInvoker (from csharp_binding_generator):
        thin wrapper around `dotnet run` on Code/Tools/BindingGenerator,
        the C# / libclang-based generator. This is the only generator;
        the previous Python BehaviorContext flow has been removed.
    - GemDependencyResolver (from gem_dependency_resolver):
        gem discovery + dependency walking used by the editor UI.
    - csharp_editor_tools / csharp_editor_bootstrap:
        Qt UI and Python entry points registered with the editor menu.

If you're looking for the pre-Phase-12 legacy classes
(CSharpBindingGenerator, ReflectionData/ReflectedClass and friends,
BindingGenerationOrchestrator, load_reflection_data_from_json,
TypeMapper), they have been removed. The C# tool reads C++ headers
directly and no longer needs the reflection_data.json intermediate.
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
    ClangSharpInvoker,
)
from .gem_dependency_resolver import (
    GemDependencyResolver,
    GemDescriptor,
    GemMappingConfig,
    GemResolutionResult,
    discover_engine_gems,
    discover_project_gems,
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
    # C# binding generator (canonical: the ClangSharp invoker shells out
    # to Code/Tools/BindingGenerator).
    "ClangSharpInvoker",
    "BindingGeneratorConfig",
    "BindingGeneratorResult",
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
