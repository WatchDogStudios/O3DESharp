#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""
Pytest configuration and fixtures for O3DESharp Python tests.
"""

import json
import pytest
import sys
from pathlib import Path
from typing import Dict, List

# Add Editor/Scripts to path for importing modules under test
editor_scripts_dir = Path(__file__).parent.parent / "Scripts"
if str(editor_scripts_dir) not in sys.path:
    sys.path.insert(0, str(editor_scripts_dir))


@pytest.fixture
def gem_root():
    """Get the O3DESharp gem root directory."""
    return Path(__file__).parent.parent.parent


@pytest.fixture
def sample_gem_json(gem_root):
    """Load the actual O3DESharp gem.json for testing."""
    gem_json_path = gem_root / "gem.json"
    if gem_json_path.exists():
        with open(gem_json_path, "r", encoding="utf-8") as f:
            return json.load(f)
    return {}


@pytest.fixture
def sample_cpp_header(tmp_path):
    """Create a sample C++ header file for testing parsing."""
    header_content = """
#pragma once
#include <AzCore/Math/Vector3.h>

namespace O3DE {
    class O3DE_EXPORT_CSHARP TestClass {
    public:
        TestClass();
        ~TestClass();
        
        void SetPosition(const AZ::Vector3& position);
        AZ::Vector3 GetPosition() const;
        
        static TestClass* Create();
        static void Destroy(TestClass* instance);
        
    private:
        AZ::Vector3 m_position;
    };
}
"""
    header_file = tmp_path / "TestClass.h"
    header_file.write_text(header_content, encoding="utf-8")
    return header_file


@pytest.fixture
def sample_binding_config(tmp_path):
    """Create a sample binding_config.json for testing."""
    config = {
        "global": {
            "requireExportAttribute": False,
            "incrementalBuild": True,
            "cSharpNamespace": "O3DE",
            "includePaths": ["Code/Include"]
        },
        "gems": {
            "O3DESharp": {
                "headerPatterns": ["Code/Include/**/*.h"],
                "excludePatterns": ["**/Tests/**", "**/Private/**"]
            }
        }
    }
    config_file = tmp_path / "binding_config.json"
    config_file.write_text(json.dumps(config, indent=2), encoding="utf-8")
    return config_file


@pytest.fixture
def o3desharp_headers(gem_root):
    """Get list of real O3DESharp header files."""
    include_dir = gem_root / "Code" / "Include" / "O3DESharp"
    if include_dir.exists():
        return list(include_dir.rglob("*.h"))
    return []


@pytest.fixture
def mock_clangsharp_output():
    """Mock output from ClangSharp tool for testing parser."""
    return """
Starting ClangSharp binding generation...
Discovered 3 gems
Processing gem: O3DESharp
  Parsing headers...
  Found 15 classes
  Generated 15 classes
  Wrote 18 files
Processed gems: O3DESharp
Generation complete
Generated 15 classes
Wrote 18 files
"""


@pytest.fixture
def workspace_gems(gem_root):
    """
    Discover real gems in the workspace for integration testing.
    Returns a dict of gem_name -> gem_path.
    """
    gems = {}
    
    # Check for O3DESharp gem itself
    gems["O3DESharp"] = gem_root
    
    # Try to find other gems in the workspace (if in O3DE repo structure)
    # Typical O3DE structure: Gems/<GemName>/gem.json
    potential_gems_root = gem_root.parent
    if potential_gems_root.name == "Gems":
        for gem_dir in potential_gems_root.iterdir():
            if gem_dir.is_dir() and gem_dir != gem_root:
                gem_json = gem_dir / "gem.json"
                if gem_json.exists():
                    try:
                        with open(gem_json, "r", encoding="utf-8") as f:
                            gem_data = json.load(f)
                            gem_name = gem_data.get("gem_name", gem_dir.name)
                            gems[gem_name] = gem_dir
                    except Exception:
                        pass
    
    return gems


@pytest.fixture
def real_gem_dependencies(gem_root):
    """
    Load real gem dependencies from O3DESharp gem.json.
    Returns list of dependency gem names.
    """
    gem_json_path = gem_root / "gem.json"
    if gem_json_path.exists():
        with open(gem_json_path, "r", encoding="utf-8") as f:
            gem_data = json.load(f)
            dependencies = gem_data.get("dependencies", [])
            # Extract gem names from dependency objects
            return [dep if isinstance(dep, str) else dep.get("gem_name", "") 
                    for dep in dependencies]
    return []


# Pytest configuration
def pytest_configure(config):
    """Configure pytest with custom markers."""
    config.addinivalue_line(
        "markers", "integration: mark test as integration test (uses real gems)"
    )
    config.addinivalue_line(
        "markers", "unit: mark test as unit test (mocked dependencies)"
    )
    config.addinivalue_line(
        "markers", "slow: mark test as slow running"
    )
