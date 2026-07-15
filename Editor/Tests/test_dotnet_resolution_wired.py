#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Guards that no editor script invokes `dotnet` as a bare PATH literal.

Every dotnet subprocess must go through csharp_platform_utils.resolve_dotnet()
so it works on a Linux editor launched without the user's login PATH.
"""

import ast
import re
from pathlib import Path

import pytest

SCRIPTS = Path(__file__).parent.parent / "Scripts"
FILES = [
    "csharp_binding_generator.py",
    "generate_bindings.py",
    "csharp_editor_tools.py",
    "csharp_project_manager.py",
    "csharp_editor_bootstrap.py",
]

# A subprocess arg list literally starting with a "dotnet" string.
BARE_DOTNET = re.compile(r"""\[\s*["']dotnet["']""")


@pytest.mark.unit
@pytest.mark.parametrize("name", FILES)
def test_no_bare_dotnet_list_literal(name):
    text = (SCRIPTS / name).read_text(encoding="utf-8")
    hits = BARE_DOTNET.findall(text)
    assert not hits, f"{name} still has {len(hits)} bare ['dotnet', ...] subprocess list(s)"


@pytest.mark.unit
@pytest.mark.parametrize("name", FILES)
def test_references_resolve_dotnet(name):
    text = (SCRIPTS / name).read_text(encoding="utf-8")
    assert "resolve_dotnet" in text, f"{name} must use resolve_dotnet()"


@pytest.mark.unit
@pytest.mark.parametrize("name", FILES)
def test_still_parses(name):
    ast.parse((SCRIPTS / name).read_text(encoding="utf-8"))


@pytest.mark.unit
def test_no_unguarded_startfile_in_editor_tools():
    text = (SCRIPTS / "csharp_editor_tools.py").read_text(encoding="utf-8")
    assert "os.startfile" not in text, (
        "csharp_editor_tools.py must use csharp_platform_utils.open_in_default_app() "
        "instead of os.startfile (Windows-only)."
    )


@pytest.mark.unit
def test_project_manager_uses_launch_renderer():
    text = (SCRIPTS / "csharp_project_manager.py").read_text(encoding="utf-8")
    assert "render_vscode_launch_json" in text, (
        "csharp_project_manager.py must render launch.json via the host-aware helper."
    )
    assert "build/windows/bin/profile" not in text, (
        "The hardcoded Windows launch.json path must be gone."
    )


@pytest.mark.unit
@pytest.mark.parametrize("name", ["csharp_project_manager.py", "csharp_editor_bootstrap.py"])
def test_no_stale_dotnet8_sdk_message(name):
    text = (SCRIPTS / name).read_text(encoding="utf-8")
    assert ".NET 8.0 SDK" not in text, f"{name} has a stale '.NET 8.0 SDK' install message"


# The public helpers a wired script may call. Any of these a script CALLS must
# also be IMPORTED from csharp_platform_utils, or it's a NameError at runtime -
# which the unit suite can't hit directly because these modules need azlmbr /
# PySide2 to import. This static AST check catches that class of bug (it is
# exactly the gap that let a missing `open_in_default_app` import ship).
PLATFORM_UTIL_HELPERS = {"resolve_dotnet", "open_in_default_app", "render_vscode_launch_json"}


def _imported_from_platform_utils(tree):
    names = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.ImportFrom) and node.module == "csharp_platform_utils":
            for alias in node.names:
                names.add(alias.asname or alias.name)
    return names


def _called_helper_names(tree):
    called = set()
    for node in ast.walk(tree):
        if (
            isinstance(node, ast.Call)
            and isinstance(node.func, ast.Name)
            and node.func.id in PLATFORM_UTIL_HELPERS
        ):
            called.add(node.func.id)
    return called


@pytest.mark.unit
@pytest.mark.parametrize("name", FILES)
def test_called_platform_helpers_are_imported(name):
    tree = ast.parse((SCRIPTS / name).read_text(encoding="utf-8"))
    called = _called_helper_names(tree)
    imported = _imported_from_platform_utils(tree)
    missing = called - imported
    assert not missing, (
        f"{name} calls {sorted(missing)} from csharp_platform_utils but does not import "
        f"them (NameError at runtime). Add them to the "
        f"'from csharp_platform_utils import ...' line."
    )
