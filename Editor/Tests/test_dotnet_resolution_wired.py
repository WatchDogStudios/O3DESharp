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
