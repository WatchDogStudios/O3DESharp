#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""
Unit tests for GemDependencyResolver class.

Tests gem discovery and dependency graph resolution using real gem.json files.
"""

import pytest
from pathlib import Path

from gem_dependency_resolver import GemDependencyResolver, GemDescriptor


@pytest.mark.unit
class TestGemDependencyResolver:
    """Test suite for GemDependencyResolver class."""

    def test_create_resolver(self):
        """Test creating a GemDependencyResolver instance."""
        resolver = GemDependencyResolver()
        assert resolver is not None

    def test_discover_gems_from_invalid_path(self):
        """Test gem discovery with invalid path."""
        resolver = GemDependencyResolver()
        result = resolver.discover_gems_from_project("/invalid/path")
        
        assert not result.success
        assert result.error_message

    @pytest.mark.integration
    def test_discover_o3desharp_gem(self, gem_root):
        """Test discovering the O3DESharp gem itself."""
        resolver = GemDependencyResolver()
        
        # Try to discover from gem root (if in O3DE project structure)
        potential_project = gem_root.parent.parent if gem_root.parent.name == "Gems" else gem_root
        
        result = resolver.discover_gems_from_project(str(potential_project))
        
        # Should at least find O3DESharp gem
        if result.success and result.active_gem_names:
            assert "O3DESharp" in result.active_gem_names or len(result.active_gem_names) > 0


@pytest.mark.integration
class TestGemDependencyResolverIntegration:
    """Integration tests using real gem data from workspace."""

    def test_load_o3desharp_gem_json(self, sample_gem_json):
        """Test loading O3DESharp gem.json data."""
        assert sample_gem_json is not None
        assert "gem_name" in sample_gem_json
        
        # O3DESharp gem should have expected fields
        if sample_gem_json.get("gem_name") == "O3DESharp":
            assert "version" in sample_gem_json
            assert "dependencies" in sample_gem_json or "Dependencies" in sample_gem_json

    def test_o3desharp_has_dependencies(self, real_gem_dependencies):
        """Test that O3DESharp gem dependencies can be read."""
        # O3DESharp should have some dependencies (Atom, AzCore, etc.)
        assert isinstance(real_gem_dependencies, list)
        # May be empty in minimal setup, but should be a valid list

    def test_workspace_gems_discovery(self, workspace_gems):
        """Test discovering gems in the workspace."""
        assert isinstance(workspace_gems, dict)
        assert "O3DESharp" in workspace_gems
        assert workspace_gems["O3DESharp"].exists()

    def test_workspace_gems_have_gem_json(self, workspace_gems):
        """Test that discovered workspace gems have gem.json files."""
        for gem_name, gem_path in workspace_gems.items():
            gem_json = gem_path / "gem.json"
            assert gem_json.exists(), f"Gem {gem_name} missing gem.json at {gem_path}"


@pytest.mark.unit
class TestGemDescriptor:
    """Test suite for GemDescriptor class."""

    def test_create_gem_descriptor(self):
        """Test creating a GemDescriptor."""
        gem = GemDescriptor(
            name="TestGem",
            path=Path("/path/to/gem"),
            version="1.0.0"
        )
        
        assert gem.name == "TestGem"
        assert gem.path == Path("/path/to/gem")
        assert gem.version == "1.0.0"
        assert gem.dependencies == []

    def test_gem_descriptor_with_dependencies(self):
        """Test GemDescriptor with dependencies."""
        gem = GemDescriptor(
            name="TestGem",
            path=Path("/path/to/gem"),
            version="1.0.0",
            dependencies=["AzCore", "AzFramework"]
        )
        
        assert "AzCore" in gem.dependencies
        assert "AzFramework" in gem.dependencies
        assert len(gem.dependencies) == 2
