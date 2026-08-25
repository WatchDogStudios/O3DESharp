#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Regression guard for WatchDogStudios/O3DESharp#3.

add_custom_target()'s COMMAND lines have no declared outputs of their own.
ly_add_target_files() later asks for the exact staged file paths (not the
target) to deploy them to the launchers, and on a fresh clone - where the
staged files don't exist on disk yet - Ninja has no rule to satisfy that
request and fails configure/build with "no known rule to make it". A
pre-packaged release tree never hits this, because the files already exist
and Ninja needs no rule for something that's already there - which is
exactly why the bug only reproduces from a clone, not from a release zip.

BYPRODUCTS on add_custom_target (present since CMake 3.2) registers those
paths as real outputs and fixes it - the same pattern the O3DE.Core custom
target already used. This test pins that both staging targets keep it.
"""

import re
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
CMAKELISTS = GEM_ROOT / "Code" / "CMakeLists.txt"


def _read():
    return CMAKELISTS.read_text(encoding="utf-8")


def _custom_target_block(text, target_name):
    """Extract one add_custom_target(...) call's full argument text, by
    counting parens from its opening '(' - the block spans multiple
    COMMAND/BYPRODUCTS/DEPENDS lines with no nested parens of their own."""
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
@pytest.mark.parametrize(
    "target_name,staged_files",
    [
        (
            "StageCoral",
            ["Coral.Managed.dll", "Coral.Managed.pdb", "Coral.Managed.runtimeconfig.json", "Coral.Managed.deps.json"],
        ),
        (
            "StageO3DECore",
            ["O3DE.Core.dll", "O3DE.Core.pdb", "O3DE.Core.deps.json"],
        ),
    ],
)
def test_staging_target_declares_byproducts_for_every_deployed_file(target_name, staged_files):
    block = _custom_target_block(_read(), target_name)
    assert "BYPRODUCTS" in block, (
        f"{target_name} lost its BYPRODUCTS keyword - Ninja will fail with 'no known rule to "
        f"make it' for these files the moment they don't already exist on disk (a fresh clone, "
        f"not a pre-packaged release tree). See WatchDogStudios/O3DESharp#3."
    )

    byproducts_start = block.index("BYPRODUCTS")
    # BYPRODUCTS runs until the next keyword (DEPENDS) or end of block.
    rest = block[byproducts_start:]
    next_keyword = re.search(r"\n\s*(DEPENDS)\b", rest)
    byproducts_text = rest[: next_keyword.start()] if next_keyword else rest

    for filename in staged_files:
        assert filename in byproducts_text, (
            f"{target_name}'s BYPRODUCTS list is missing {filename} - it's still copied by a "
            f"COMMAND line but Ninja has no rule for it, reproducing the same failure for this "
            f"one file even though the others are covered."
        )
