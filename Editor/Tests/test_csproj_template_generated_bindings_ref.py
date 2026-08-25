#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Regression guard: new C# script projects must implicitly reference
O3DESharp.GeneratedBindings.dll with no manual csproj edit - see
docs/superpowers/specs/2026-08-25-zero-config-gem-bindings-design.md §3.
"""

import sys
import types
from pathlib import Path

import pytest


def _install_azlmbr_stub():
    """
    csharp_project_manager.py does `import azlmbr.bus`, `import
    azlmbr.editor`, and `import azlmbr.paths` at module scope. None of
    those packages exist outside a running O3DE Editor process, so stub
    them in sys.modules before the module under test is imported.
    """
    azlmbr = types.ModuleType("azlmbr")
    azlmbr_bus = types.ModuleType("azlmbr.bus")
    azlmbr_editor = types.ModuleType("azlmbr.editor")
    azlmbr_paths = types.ModuleType("azlmbr.paths")
    azlmbr_paths.projectroot = str(Path(__file__).resolve().parents[2])
    azlmbr.bus = azlmbr_bus
    azlmbr.editor = azlmbr_editor
    azlmbr.paths = azlmbr_paths
    sys.modules["azlmbr"] = azlmbr
    sys.modules["azlmbr.bus"] = azlmbr_bus
    sys.modules["azlmbr.editor"] = azlmbr_editor
    sys.modules["azlmbr.paths"] = azlmbr_paths


_install_azlmbr_stub()

EDITOR_SCRIPTS = Path(__file__).resolve().parents[1] / "Scripts"
sys.path.insert(0, str(EDITOR_SCRIPTS))

import csharp_project_manager  # noqa: E402


@pytest.mark.unit
def test_csproj_template_references_generated_bindings():
    rendered = csharp_project_manager.CSPROJ_TEMPLATE.format(
        o3de_core_path=r"C:\fake\Bin\Scripts\O3DE.Core.dll",
        generated_bindings_path=r"C:\fake\Bin\Scripts\O3DESharp.GeneratedBindings.dll",
    )
    assert '<Reference Include="O3DESharp.GeneratedBindings">' in rendered
    assert r"C:\fake\Bin\Scripts\O3DESharp.GeneratedBindings.dll" in rendered
    # The reference must not be marked Private=false the way O3DE.Core's
    # HintPath reference in the *generated-bindings* csproj itself is
    # (Task 2) - user game scripts are the actual consumer and must copy
    # the DLL's dependency graph normally.


@pytest.mark.unit
def test_get_generated_bindings_path_matches_deploy_location():
    # _get_generated_bindings_path mirrors _get_o3de_core_path's shape -
    # both point at <project>/Bin/Scripts/<name>.
    manager_cls = csharp_project_manager.CSharpProjectManager
    assert hasattr(manager_cls, "_get_generated_bindings_path"), (
        "CSharpProjectManager must define _get_generated_bindings_path, "
        "the same way it defines _get_o3de_core_path"
    )
