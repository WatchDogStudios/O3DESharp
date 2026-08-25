#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Regression guard for the BuildGeneratedBindings CMake target
(zero-config gem bindings, sub-project 1 - see
docs/superpowers/specs/2026-08-25-zero-config-gem-bindings-design.md).

Same BYPRODUCTS lesson as StageCoral/StageO3DECore
(WatchDogStudios/O3DESharp#3): add_custom_target()'s COMMAND lines have no
declared outputs of their own, so the staged DLL/PDB/deps.json need
BYPRODUCTS or a fresh-clone Ninja build fails with "no known rule to make
it" at the point ly_add_target_files() asks for them by exact path.
"""

from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
CMAKELISTS = GEM_ROOT / "Code" / "CMakeLists.txt"


def _read():
    return CMAKELISTS.read_text(encoding="utf-8")


def _custom_target_block(text, target_name):
    marker = f"add_custom_target(${{gem_name}}.{target_name}"
    start = text.index(marker)
    open_paren = text.index("(", start)
    depth = 0
    for i in range(open_paren, len(text)):
        if text[i] == "(":
            depth += 1
        elif text[i] == ")":
            depth -= 1
            if depth == 0:
                return text[open_paren + 1 : i]
    raise AssertionError(f"unterminated add_custom_target({marker}...)")


@pytest.mark.unit
def test_build_generated_bindings_target_exists_and_depends_on_generate():
    block = _custom_target_block(_read(), "BuildGeneratedBindings")
    assert "GenerateBindings" in block, (
        "BuildGeneratedBindings must DEPENDS on ${gem_name}.GenerateBindings "
        "so the consolidated csproj exists before dotnet build runs against it."
    )


@pytest.mark.unit
def test_build_generated_bindings_target_declares_byproducts():
    block = _custom_target_block(_read(), "BuildGeneratedBindings")
    assert "BYPRODUCTS" in block, (
        "Without BYPRODUCTS, Ninja has no rule for the staged DLL/PDB/deps.json "
        "on a fresh clone - see WatchDogStudios/O3DESharp#3 for the exact failure mode."
    )
    byproducts_start = block.index("BYPRODUCTS")
    byproducts_text = block[byproducts_start:]
    for filename in ("O3DESharp.GeneratedBindings.dll", "O3DESharp.GeneratedBindings.pdb", "O3DESharp.GeneratedBindings.deps.json"):
        assert filename in byproducts_text, f"BYPRODUCTS is missing {filename}"


@pytest.mark.unit
def test_build_generated_bindings_deployed_to_bin_scripts():
    text = _read()
    assert "GENERATED_BINDINGS_STAGING_DIR" in text
    ly_add_target_files_calls = text.count("ly_add_target_files")
    assert ly_add_target_files_calls >= 3, (
        "Expected at least 3 ly_add_target_files calls (Coral, O3DE.Core, "
        "and the new GeneratedBindings) - found fewer, deploy block may be missing."
    )
