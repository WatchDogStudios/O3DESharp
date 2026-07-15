#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Guards that the per-platform CMake file set stays consistent.

Code/CMakeLists.txt includes ${pal_dir}/o3desharp_shared_files.cmake
unconditionally, so every supported platform directory must ship that
stub or a configure on that platform dies with a fatal include error.
"""

import json
import re
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).parent.parent.parent
PLATFORM_DIR = GEM_ROOT / "Code" / "Platform"

# Platforms whose PAL file Code/CMakeLists.txt will include(). Each must
# carry the shared_files stub because the include is unconditional.
PLATFORMS = ["Windows", "Linux", "Mac", "Android", "iOS"]


@pytest.mark.unit
@pytest.mark.parametrize("platform", PLATFORMS)
def test_platform_has_shared_files_stub(platform):
    stub = PLATFORM_DIR / platform / "o3desharp_shared_files.cmake"
    assert stub.is_file(), (
        f"{stub} is missing; Code/CMakeLists.txt include()s it unconditionally, "
        f"so a {platform} configure would fail with a fatal include error."
    )
    text = stub.read_text(encoding="utf-8")
    assert re.search(r"set\s*\(\s*FILES", text), (
        f"{stub} must define a FILES list (even if empty) to match the other platforms."
    )


def _pal_supported(platform):
    pal = PLATFORM_DIR / platform / f"PAL_{platform.lower()}.cmake"
    text = pal.read_text(encoding="utf-8")
    m = re.search(r"set\s*\(\s*PAL_TRAIT_O3DESHARP_SUPPORTED\s+(TRUE|FALSE)\s*\)", text)
    assert m, f"{pal} must set PAL_TRAIT_O3DESHARP_SUPPORTED explicitly."
    return m.group(1) == "TRUE"


@pytest.mark.unit
def test_pal_support_traits_match_gem_json():
    gem = json.loads((GEM_ROOT / "gem.json").read_text(encoding="utf-8"))
    declared = set(gem.get("platforms", []))
    # gem.json currently declares the honest supported set.
    assert _pal_supported("Windows") is True
    assert _pal_supported("Linux") is True
    assert _pal_supported("Mac") is ("Mac" in declared)
    assert _pal_supported("Android") is ("Android" in declared)
    assert _pal_supported("iOS") is ("iOS" in declared)
