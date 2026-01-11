#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""
Gem Dependency Resolver for O3DESharp

This module provides utilities for discovering O3DE gems and resolving their dependencies.
It is used by the C# binding generator to organize bindings by gem.

Usage:
    from gem_dependency_resolver import GemDependencyResolver

    resolver = GemDependencyResolver()
    resolver.discover_gems_from_project("/path/to/project")

    # Get all active gems
    gems = resolver.get_active_gems()

    # Get dependencies for a specific gem
    deps = resolver.get_gem_dependencies("PhysX")

    # Get gems in topological order
    ordered = resolver.get_gems_in_dependency_order()
"""

import json
import logging
import os
import sys
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional, Set, Tuple

# Set up logging
logger = logging.getLogger("O3DESharp.GemResolver")


@dataclass
class GemDescriptor:
    """Represents information about a discovered gem."""

    name: str
    display_name: str = ""
    version: str = "1.0.0"
    absolute_path: Path = field(default_factory=Path)
    gem_json_path: Path = field(default_factory=Path)
    dependencies: List[str] = field(default_factory=list)
    dependents: List[str] = field(default_factory=list)
    summary: str = ""
    tags: List[str] = field(default_factory=list)
    module_names: List[str] = field(default_factory=list)
    is_active: bool = False
    is_loaded: bool = False

    def __post_init__(self):
        if not self.display_name:
            self.display_name = self.name


@dataclass
class GemResolutionResult:
    """Result of gem dependency resolution."""

    success: bool = False
    error_message: str = ""
    all_gems: List[GemDescriptor] = field(default_factory=list)
    sorted_gem_names: List[str] = field(default_factory=list)
    active_gem_names: List[str] = field(default_factory=list)
    failed_gems: List[Tuple[str, str]] = field(
        default_factory=list
    )  # (gem_name, error_reason)


@dataclass
class GemMappingConfig:
    """Configuration for class-to-gem mapping heuristics."""

    use_category_attribute: bool = True
    use_name_prefixes: bool = True
    use_module_hints: bool = True
    prefix_mappings: Dict[str, str] = field(default_factory=dict)
    category_mappings: Dict[str, str] = field(default_factory=dict)
    default_gem_name: str = "O3DE.Core"


# Default prefix-to-gem mappings for well-known O3DE components
DEFAULT_PREFIX_MAPPINGS = {
    "AZ": "AzCore",
    "Az": "AzCore",
    "Atom": "Atom",
    "AtomRPI": "Atom_RPI",
    "AtomRHI": "Atom_RHI",
    "AtomUtils": "Atom_Utils",
    "PhysX": "PhysX",
    "Script": "ScriptCanvas",
    "ScriptCanvas": "ScriptCanvas",
    "Multiplayer": "Multiplayer",
    "Network": "Multiplayer",
    "UI": "LyShine",
    "Ly": "LyShine",
    "AWS": "AWSCore",
    "Http": "HttpRequestor",
    "Input": "StartingPointInput",
    "Audio": "AudioSystem",
    "Terrain": "Terrain",
    "Vegetation": "Vegetation",
    "Landscape": "LandscapeCanvas",
    "EMotionFX": "EMotionFX",
    "Animation": "EMotionFX",
    "Debug": "ImGui",
    "ImGui": "ImGui",
    "Camera": "StartingPointCamera",
    "Transform": "AzCore",
    "Entity": "AzCore",
    "Component": "AzCore",
}

# Default category-to-gem mappings
DEFAULT_CATEGORY_MAPPINGS = {
    "Core": "AzCore",
    "Math": "AzCore",
    "Entity": "AzCore",
    "Components": "AzCore",
    "Rendering": "Atom",
    "Render": "Atom",
    "Graphics": "Atom",
    "Material": "Atom",
    "Mesh": "Atom",
    "Physics": "PhysX",
    "Collision": "PhysX",
    "Rigid Body": "PhysX",
    "Script": "ScriptCanvas",
    "Scripting": "ScriptCanvas",
    "Networking": "Multiplayer",
    "Multiplayer": "Multiplayer",
    "UI": "LyShine",
    "Audio": "AudioSystem",
    "Animation": "EMotionFX",
    "Terrain": "Terrain",
    "Vegetation": "Vegetation",
    "Camera": "StartingPointCamera",
    "Input": "StartingPointInput",
    "Debugging": "ImGui",
}


class GemDependencyResolver:
    """
    Discovers gems and resolves their dependencies.

    This class scans for active gems in an O3DE project/engine and builds
    a dependency graph. It provides utilities to:

    - Discover all active gems and their metadata
    - Resolve gem dependencies in topological order
    - Map BehaviorContext classes to their source gems
    - Generate per-gem binding configurations
    """

    def __init__(self):
        self._gems: Dict[str, GemDescriptor] = {}
        self._sorted_gems: List[str] = []
        self._class_mappings: Dict[str, str] = {}
        self._mapping_config = GemMappingConfig()
        self._normalized_name_lookup: Dict[str, str] = {}
        self._graph_built = False

        # Initialize with default mappings
        self._mapping_config.prefix_mappings = dict(DEFAULT_PREFIX_MAPPINGS)
        self._mapping_config.category_mappings = dict(DEFAULT_CATEGORY_MAPPINGS)

    # ============================================================
    # Gem Discovery
    # ============================================================

    def discover_gems_from_project(self, project_path: str) -> GemResolutionResult:
        """
        Discover gems from a project path.

        Args:
            project_path: Path to the project root (containing project.json)

        Returns:
            GemResolutionResult with discovered gems
        """
        self.clear()

        project_path = Path(project_path)
        if not project_path.exists():
            return GemResolutionResult(
                success=False,
                error_message=f"Project path does not exist: {project_path}",
            )

        result = GemResolutionResult(success=True)

        # Read project.json
        project_json_path = project_path / "project.json"
        if not project_json_path.exists():
            return GemResolutionResult(
                success=False,
                error_message=f"project.json not found at: {project_json_path}",
            )

        try:
            with open(project_json_path, "r", encoding="utf-8") as f:
                project_data = json.load(f)
        except json.JSONDecodeError as e:
            return GemResolutionResult(
                success=False, error_message=f"Failed to parse project.json: {e}"
            )

        # Get active gem names from project.json
        gem_names = project_data.get("gem_names", [])
        for gem_entry in gem_names:
            if isinstance(gem_entry, str):
                result.active_gem_names.append(gem_entry)
            elif isinstance(gem_entry, dict):
                name = gem_entry.get("name")
                if name:
                    result.active_gem_names.append(name)

        # Find engine path for engine gems
        engine_path = self._find_engine_path(project_path)

        # Build search paths for gems
        search_paths = [
            project_path / "Gems",
        ]
        if engine_path:
            search_paths.append(engine_path / "Gems")

        # Also check external gem directories from o3de_manifest.json
        external_gem_paths = self._get_external_gem_paths()
        search_paths.extend(external_gem_paths)

        # Discover each active gem
        for gem_name in result.active_gem_names:
            descriptor = self._find_and_parse_gem(gem_name, search_paths)
            if descriptor:
                descriptor.is_active = True
                self.register_gem(descriptor)
            else:
                result.failed_gems.append((gem_name, "gem.json not found"))
                # Still register with basic info
                basic_descriptor = GemDescriptor(
                    name=gem_name, is_active=True, is_loaded=False
                )
                self.register_gem(basic_descriptor)

        # Build dependency graph
        self._build_dependency_graph()
        self._topological_sort()

        result.sorted_gem_names = self._sorted_gems.copy()
        result.all_gems = list(self._gems.values())

        logger.info(
            f"Discovered {len(self._gems)} gems ({len(result.active_gem_names)} active)"
        )

        return result

    def discover_gems_from_engine(self, engine_path: str) -> GemResolutionResult:
        """
        Discover all gems from an engine installation.

        Args:
            engine_path: Path to the O3DE engine root

        Returns:
            GemResolutionResult with discovered gems
        """
        self.clear()

        engine_path = Path(engine_path)
        if not engine_path.exists():
            return GemResolutionResult(
                success=False,
                error_message=f"Engine path does not exist: {engine_path}",
            )

        result = GemResolutionResult(success=True)

        gems_path = engine_path / "Gems"
        if not gems_path.exists():
            return GemResolutionResult(
                success=False, error_message=f"Gems directory not found at: {gems_path}"
            )

        # Recursively find all gem.json files
        for gem_json_path in gems_path.rglob("gem.json"):
            try:
                descriptor = self._parse_gem_json(gem_json_path)
                if descriptor:
                    descriptor.is_active = True  # Consider all engine gems as active
                    self.register_gem(descriptor)
                    result.active_gem_names.append(descriptor.name)
            except Exception as e:
                gem_name = gem_json_path.parent.name
                result.failed_gems.append((gem_name, str(e)))
                logger.warning(f"Failed to parse {gem_json_path}: {e}")

        # Build dependency graph
        self._build_dependency_graph()
        self._topological_sort()

        result.sorted_gem_names = self._sorted_gems.copy()
        result.all_gems = list(self._gems.values())

        logger.info(f"Discovered {len(self._gems)} gems from engine")

        return result

    def register_gem(self, descriptor: GemDescriptor) -> None:
        """
        Manually register a gem.

        Args:
            descriptor: The gem descriptor to register
        """
        self._gems[descriptor.name] = descriptor
        self._normalized_name_lookup[self._normalize_gem_name(descriptor.name)] = (
            descriptor.name
        )
        self._graph_built = False

    def clear(self) -> None:
        """Clear all discovered gem data."""
        self._gems.clear()
        self._sorted_gems.clear()
        self._class_mappings.clear()
        self._normalized_name_lookup.clear()
        self._graph_built = False

    # ============================================================
    # Dependency Resolution
    # ============================================================

    def get_gem_dependencies(
        self, gem_name: str, include_transitive: bool = True
    ) -> List[str]:
        """
        Get all dependencies for a gem.

        Args:
            gem_name: Name of the gem
            include_transitive: Include transitive dependencies

        Returns:
            List of dependency gem names
        """
        result = []
        visited = set()
        self._collect_dependencies(gem_name, visited, result, include_transitive)
        return result

    def get_gem_dependents(
        self, gem_name: str, include_transitive: bool = True
    ) -> List[str]:
        """
        Get all gems that depend on a given gem.

        Args:
            gem_name: Name of the gem
            include_transitive: Include transitive dependents

        Returns:
            List of dependent gem names
        """
        result = []
        visited = set()
        self._collect_dependents(gem_name, visited, result, include_transitive)
        return result

    def get_gems_in_dependency_order(self) -> List[str]:
        """
        Get gems in topological order (dependencies before dependents).

        Returns:
            Sorted list of gem names
        """
        if not self._graph_built:
            self._build_dependency_graph()
            self._topological_sort()
        return self._sorted_gems.copy()

    def depends_on(self, gem_name: str, dependency_name: str) -> bool:
        """
        Check if one gem depends on another.

        Args:
            gem_name: The gem to check
            dependency_name: The potential dependency

        Returns:
            True if gem_name depends on dependency_name
        """
        deps = self.get_gem_dependencies(gem_name, include_transitive=True)
        return dependency_name in deps

    # ============================================================
    # Gem Accessors
    # ============================================================

    def get_gem(self, gem_name: str) -> Optional[GemDescriptor]:
        """
        Get a gem descriptor by name.

        Args:
            gem_name: Name of the gem

        Returns:
            GemDescriptor or None if not found
        """
        if gem_name in self._gems:
            return self._gems[gem_name]

        # Try normalized lookup
        normalized = self._normalize_gem_name(gem_name)
        if normalized in self._normalized_name_lookup:
            real_name = self._normalized_name_lookup[normalized]
            return self._gems.get(real_name)

        return None

    def get_all_gems(self) -> Dict[str, GemDescriptor]:
        """Get all discovered gems."""
        return self._gems.copy()

    def get_active_gems(self) -> List[GemDescriptor]:
        """Get all active gems."""
        return [gem for gem in self._gems.values() if gem.is_active]

    def get_active_gem_names(self) -> List[str]:
        """Get names of all active gems."""
        return [gem.name for gem in self._gems.values() if gem.is_active]

    def has_gem(self, gem_name: str) -> bool:
        """Check if a gem exists."""
        if gem_name in self._gems:
            return True
        normalized = self._normalize_gem_name(gem_name)
        return normalized in self._normalized_name_lookup

    def get_gem_count(self) -> int:
        """Get the number of discovered gems."""
        return len(self._gems)

    # ============================================================
    # Class-to-Gem Mapping
    # ============================================================

    def set_mapping_config(self, config: GemMappingConfig) -> None:
        """Configure the class-to-gem mapping heuristics."""
        self._mapping_config = config

    def get_mapping_config(self) -> GemMappingConfig:
        """Get the current mapping configuration."""
        return self._mapping_config

    def resolve_gem_for_class(self, class_name: str, category: str = "") -> str:
        """
        Resolve which gem a class belongs to.

        Args:
            class_name: Name of the class
            category: Optional category from BehaviorContext

        Returns:
            Name of the owning gem, or default_gem_name if unknown
        """
        # 1. Check explicit mappings first
        if class_name in self._class_mappings:
            return self._class_mappings[class_name]

        # 2. Try category-based mapping
        if self._mapping_config.use_category_attribute and category:
            gem_hint = self._extract_gem_hint_from_category(category)
            if gem_hint and self.has_gem(gem_hint):
                return gem_hint

            # Check configured category mappings
            if category in self._mapping_config.category_mappings:
                return self._mapping_config.category_mappings[category]

        # 3. Try name prefix-based mapping
        if self._mapping_config.use_name_prefixes:
            gem_hint = self._extract_gem_hint_from_class_name(class_name)
            if gem_hint and self.has_gem(gem_hint):
                return gem_hint

            # Check configured prefix mappings
            for prefix, gem_name in self._mapping_config.prefix_mappings.items():
                if class_name.startswith(prefix):
                    return gem_name

        # 4. Return default gem name
        return self._mapping_config.default_gem_name

    def register_class_mapping(self, class_name: str, gem_name: str) -> None:
        """
        Register an explicit class-to-gem mapping.

        Args:
            class_name: Name of the class
            gem_name: Name of the gem
        """
        self._class_mappings[class_name] = gem_name

    def get_class_mappings(self) -> Dict[str, str]:
        """Get all registered class-to-gem mappings."""
        return self._class_mappings.copy()

    def auto_populate_class_mappings(self) -> None:
        """
        Populate class mappings from gem metadata.
        Call this after discovering gems to auto-populate common mappings.
        """
        for gem in self._gems.values():
            for module_name in gem.module_names:
                self._mapping_config.prefix_mappings[module_name] = gem.name

    # ============================================================
    # Binding Generation Helpers
    # ============================================================

    def get_binding_namespace(
        self, gem_name: str, root_namespace: str = "O3DE.Generated"
    ) -> str:
        """
        Get the C# namespace to use for a gem's bindings.

        Args:
            gem_name: Name of the gem
            root_namespace: Base namespace

        Returns:
            Full namespace string (e.g., "O3DE.Generated.Atom")
        """
        clean_name = gem_name.replace("_", ".").replace("-", ".")

        # Remove double dots
        while ".." in clean_name:
            clean_name = clean_name.replace("..", ".")

        return f"{root_namespace}.{clean_name}"

    def get_binding_output_path(
        self, gem_name: str, base_output_dir: str = "Generated"
    ) -> Path:
        """
        Get the output directory for a gem's bindings.

        Args:
            gem_name: Name of the gem
            base_output_dir: Base output directory

        Returns:
            Path to gem-specific output directory
        """
        clean_name = self._get_safe_filename(gem_name)
        return Path(base_output_dir) / clean_name

    def should_generate_bindings(
        self,
        gem_name: str,
        include_list: Optional[List[str]] = None,
        exclude_list: Optional[List[str]] = None,
    ) -> bool:
        """
        Check if bindings should be generated for a gem.

        Args:
            gem_name: Name of the gem
            include_list: Optional list of gems to include (empty/None = all)
            exclude_list: Optional list of gems to exclude

        Returns:
            True if bindings should be generated
        """
        # Check exclusion list first
        if exclude_list and gem_name in exclude_list:
            return False

        # If include list is specified, gem must be in it
        if include_list:
            return gem_name in include_list

        # Default: generate for all active gems
        gem = self.get_gem(gem_name)
        return gem is not None and gem.is_active

    # ============================================================
    # Internal Helpers
    # ============================================================

    def _find_engine_path(self, project_path: Path) -> Optional[Path]:
        """Find the engine path for a project."""
        # Check for engine.json in parent directories
        current = project_path.parent
        for _ in range(5):  # Limit search depth
            engine_json = current / "engine.json"
            if engine_json.exists():
                return current
            current = current.parent
            if current == current.parent:
                break

        # Check environment variable
        engine_path = os.environ.get("O3DE_ENGINE_PATH")
        if engine_path:
            return Path(engine_path)

        return None

    def _get_external_gem_paths(self) -> List[Path]:
        """Get external gem paths from o3de_manifest.json."""
        paths = []

        # Check user home directory for o3de manifest
        home = Path.home()
        manifest_path = home / ".o3de" / "o3de_manifest.json"

        if manifest_path.exists():
            try:
                with open(manifest_path, "r", encoding="utf-8") as f:
                    manifest = json.load(f)

                external_subdirs = manifest.get("external_subdirectories", [])
                for subdir in external_subdirs:
                    path = Path(subdir)
                    if path.exists():
                        paths.append(path)
            except Exception as e:
                logger.warning(f"Failed to read o3de_manifest.json: {e}")

        return paths

    def _find_and_parse_gem(
        self, gem_name: str, search_paths: List[Path]
    ) -> Optional[GemDescriptor]:
        """Find and parse a gem by name in search paths."""
        for search_path in search_paths:
            # Direct match
            gem_path = search_path / gem_name / "gem.json"
            if gem_path.exists():
                return self._parse_gem_json(gem_path)

            # Search subdirectories
            for gem_json in search_path.rglob("gem.json"):
                try:
                    with open(gem_json, "r", encoding="utf-8") as f:
                        data = json.load(f)
                        if data.get("gem_name") == gem_name:
                            return self._parse_gem_json(gem_json)
                except Exception:
                    continue

        return None

    def _parse_gem_json(self, gem_json_path: Path) -> Optional[GemDescriptor]:
        """Parse a gem.json file and create a GemDescriptor."""
        try:
            with open(gem_json_path, "r", encoding="utf-8") as f:
                data = json.load(f)
        except Exception as e:
            logger.warning(f"Failed to parse {gem_json_path}: {e}")
            return None

        descriptor = GemDescriptor(
            name=data.get("gem_name", gem_json_path.parent.name),
            display_name=data.get("display_name", ""),
            version=data.get("version", "1.0.0"),
            absolute_path=gem_json_path.parent,
            gem_json_path=gem_json_path,
            summary=data.get("summary", ""),
            is_loaded=True,
        )

        # Parse dependencies
        dependencies = data.get("dependencies", [])
        for dep in dependencies:
            if isinstance(dep, str):
                descriptor.dependencies.append(dep)
            elif isinstance(dep, dict):
                dep_name = dep.get("name")
                if dep_name:
                    descriptor.dependencies.append(dep_name)

        # Parse tags
        for tag_key in ["user_tags", "canonical_tags"]:
            tags = data.get(tag_key, [])
            for tag in tags:
                if isinstance(tag, str):
                    descriptor.tags.append(tag)

        return descriptor

    def _build_dependency_graph(self) -> None:
        """Build the dependency graph after gem discovery."""
        if self._graph_built:
            return

        # Clear existing dependents lists
        for gem in self._gems.values():
            gem.dependents.clear()

        # Build reverse dependency graph
        for gem in self._gems.values():
            for dep in gem.dependencies:
                if dep in self._gems:
                    self._gems[dep].dependents.append(gem.name)

        self._graph_built = True

    def _topological_sort(self) -> None:
        """Perform topological sort on the dependency graph."""
        self._sorted_gems.clear()

        # Kahn's algorithm
        in_degree = {name: 0 for name in self._gems}

        for gem in self._gems.values():
            for dep in gem.dependencies:
                if dep in self._gems:
                    in_degree[gem.name] += 1

        # Queue of gems with no dependencies
        queue = [name for name, degree in in_degree.items() if degree == 0]

        while queue:
            current = queue.pop(0)
            self._sorted_gems.append(current)

            gem = self._gems.get(current)
            if gem:
                for dependent in gem.dependents:
                    in_degree[dependent] -= 1
                    if in_degree[dependent] == 0:
                        queue.append(dependent)

        # Check for cycles
        if len(self._sorted_gems) != len(self._gems):
            logger.warning(
                f"Cyclic dependencies detected. Sorted {len(self._sorted_gems)} "
                f"of {len(self._gems)} gems."
            )
            # Add remaining gems
            for name in self._gems:
                if name not in self._sorted_gems:
                    self._sorted_gems.append(name)

    def _collect_dependencies(
        self,
        gem_name: str,
        visited: Set[str],
        result: List[str],
        include_transitive: bool,
    ) -> None:
        """Recursively collect dependencies."""
        gem = self._gems.get(gem_name)
        if not gem:
            return

        for dep in gem.dependencies:
            if dep not in visited:
                visited.add(dep)
                result.append(dep)
                if include_transitive:
                    self._collect_dependencies(dep, visited, result, True)

    def _collect_dependents(
        self,
        gem_name: str,
        visited: Set[str],
        result: List[str],
        include_transitive: bool,
    ) -> None:
        """Recursively collect dependents."""
        gem = self._gems.get(gem_name)
        if not gem:
            return

        for dep in gem.dependents:
            if dep not in visited:
                visited.add(dep)
                result.append(dep)
                if include_transitive:
                    self._collect_dependents(dep, visited, result, True)

    def _normalize_gem_name(self, gem_name: str) -> str:
        """Normalize a gem name for case-insensitive matching."""
        return gem_name.lower().replace("_", "").replace("-", "")

    def _extract_gem_hint_from_class_name(self, class_name: str) -> str:
        """Extract gem name hint from a class name."""
        # Check for namespace separator
        if "::" in class_name:
            namespace = class_name.split("::")[0]
            if self.has_gem(namespace):
                return namespace

            normalized = self._normalize_gem_name(namespace)
            if normalized in self._normalized_name_lookup:
                return self._normalized_name_lookup[normalized]

        # Try to match prefixes
        for gem_name in self._gems:
            if class_name.startswith(gem_name):
                return gem_name

        return ""

    def _extract_gem_hint_from_category(self, category: str) -> str:
        """Extract gem name hint from a category string."""
        # Categories often contain gem names like "Atom/Rendering"
        if "/" in category:
            first_part = category.split("/")[0]
            if self.has_gem(first_part):
                return first_part

            normalized = self._normalize_gem_name(first_part)
            if normalized in self._normalized_name_lookup:
                return self._normalized_name_lookup[normalized]

        # Check if entire category is a gem name
        if self.has_gem(category):
            return category

        normalized = self._normalize_gem_name(category)
        if normalized in self._normalized_name_lookup:
            return self._normalized_name_lookup[normalized]

        return ""

    def _get_safe_filename(self, gem_name: str) -> str:
        """Get a safe filename for a gem."""
        result = gem_name
        invalid_chars = [":", "<", ">", "|", "?", "*", "/", "\\", '"']
        for char in invalid_chars:
            result = result.replace(char, "_")
        return result


# ============================================================
# Convenience Functions
# ============================================================


def discover_project_gems(project_path: str) -> GemResolutionResult:
    """
    Convenience function to discover gems from a project.

    Args:
        project_path: Path to the project root

    Returns:
        GemResolutionResult with discovered gems
    """
    resolver = GemDependencyResolver()
    return resolver.discover_gems_from_project(project_path)


def discover_engine_gems(engine_path: str) -> GemResolutionResult:
    """
    Convenience function to discover gems from an engine.

    Args:
        engine_path: Path to the engine root

    Returns:
        GemResolutionResult with discovered gems
    """
    resolver = GemDependencyResolver()
    return resolver.discover_gems_from_engine(engine_path)


def get_gem_dependency_order(gems: Dict[str, GemDescriptor]) -> List[str]:
    """
    Get gems in dependency order from a dictionary of gems.

    Args:
        gems: Dictionary of gem name to GemDescriptor

    Returns:
        List of gem names in dependency order
    """
    resolver = GemDependencyResolver()
    for gem in gems.values():
        resolver.register_gem(gem)
    return resolver.get_gems_in_dependency_order()


# ============================================================
# CLI Interface
# ============================================================


def main():
    """Command-line interface for gem discovery."""
    import argparse

    parser = argparse.ArgumentParser(
        description="Discover O3DE gems and their dependencies"
    )
    parser.add_argument("--project", "-p", help="Path to the project root")
    parser.add_argument("--engine", "-e", help="Path to the engine root")
    parser.add_argument(
        "--list", "-l", action="store_true", help="List all discovered gems"
    )
    parser.add_argument("--deps", help="Show dependencies for a specific gem")
    parser.add_argument("--dependents", help="Show gems that depend on a specific gem")
    parser.add_argument(
        "--order", action="store_true", help="Show gems in dependency order"
    )
    parser.add_argument(
        "--verbose", "-v", action="store_true", help="Enable verbose output"
    )

    args = parser.parse_args()

    if args.verbose:
        logging.basicConfig(level=logging.DEBUG)
    else:
        logging.basicConfig(level=logging.INFO)

    resolver = GemDependencyResolver()

    if args.project:
        result = resolver.discover_gems_from_project(args.project)
    elif args.engine:
        result = resolver.discover_gems_from_engine(args.engine)
    else:
        parser.error("Either --project or --engine must be specified")
        return

    if not result.success:
        print(f"Error: {result.error_message}")
        sys.exit(1)

    if args.list:
        print(f"\nDiscovered {len(result.all_gems)} gems:")
        for gem in sorted(result.all_gems, key=lambda g: g.name):
            active = "[active]" if gem.is_active else ""
            deps = f" -> {', '.join(gem.dependencies)}" if gem.dependencies else ""
            print(f"  {gem.name} {active}{deps}")

    if args.deps:
        deps = resolver.get_gem_dependencies(args.deps)
        print(f"\nDependencies for {args.deps}:")
        for dep in deps:
            print(f"  {dep}")

    if args.dependents:
        dependents = resolver.get_gem_dependents(args.dependents)
        print(f"\nGems that depend on {args.dependents}:")
        for dep in dependents:
            print(f"  {dep}")

    if args.order:
        print("\nGems in dependency order:")
        for i, gem_name in enumerate(resolver.get_gems_in_dependency_order(), 1):
            print(f"  {i}. {gem_name}")

    if result.failed_gems:
        print("\nFailed to load:")
        for name, reason in result.failed_gems:
            print(f"  {name}: {reason}")


if __name__ == "__main__":
    main()
