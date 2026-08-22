#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""The shipping build config must be AOT-clean, and enforced as such.

An IL2xxx/IL3xxx warning in a NativeAOT build is a runtime failure waiting to
happen - reflection over a type the compiler removed, or serialization needing
codegen that is not there. As a warning it accumulates unnoticed; as an error
it cannot.

Deliberately scoped to NativeAot mode only: the editor build is behaviour-
preserving in M3 and must not gain new failure modes.

Marked `slow` because it shells out to `dotnet build`.
"""

import re
import shutil
import subprocess
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
CORE_CSPROJ = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "O3DE.Core.csproj"


def _build(*extra):
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    return subprocess.run(
        ["dotnet", "build", str(CORE_CSPROJ), "-c", "Release", "--nologo", "-t:Rebuild", *extra],
        capture_output=True, text=True, timeout=900,
    )


@pytest.mark.unit
def test_analyzers_are_scoped_to_the_shipping_mode_only():
    text = CORE_CSPROJ.read_text(encoding="utf-8")
    block = re.search(
        r"<PropertyGroup Condition=\"'\$\(O3DESharpHostMode\)'\s*==\s*'NativeAot'\">(.*?)</PropertyGroup>",
        text, re.DOTALL,
    )
    assert block, "the NativeAot PropertyGroup must exist"
    body = block.group(1)
    assert "<IsAotCompatible>true</IsAotCompatible>" in body
    for code in ("IL2026", "IL2072", "IL2075", "IL3050", "IL3051"):
        assert code in body, f"{code} must be promoted to an error in the shipping mode"
    assert "IsAotCompatible" not in text.replace(body, ""), (
        "IsAotCompatible must not be set outside the NativeAot condition; the editor "
        "build is behaviour-preserving in M3."
    )


@pytest.mark.slow
def test_nativeaot_mode_builds_with_zero_il_diagnostics():
    result = _build("-p:O3DESharpHostMode=NativeAot")
    assert result.returncode == 0, result.stdout + result.stderr
    diagnostics = re.findall(r"(?:warning|error) (IL\d+)", result.stdout)
    assert not diagnostics, f"shipping build is not AOT-clean: {sorted(set(diagnostics))}"


@pytest.mark.slow
def test_coral_mode_is_unaffected():
    result = _build()
    assert result.returncode == 0, result.stdout + result.stderr
    assert "IsAotCompatible" not in result.stdout
    # The editor build keeps its pre-existing CS warnings and gains no IL ones.
    assert not re.findall(r"(?:warning|error) (IL\d+)", result.stdout)
