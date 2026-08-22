#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""The closed-world diagnostic must fire at build time, on the exact call site.

Runtime-dynamic BehaviorContext dispatch is out of scope on NativeAOT desktop.
That is a designed restriction, and the whole point of designing it rather than
discovering it is that it is diagnosable: a bus or event name the generator
cannot see at compile time gets a build warning naming the site, and a runtime
hard error if it is reached anyway. Never a silent degrade.

Marked `slow` because it shells out to `dotnet build`.
"""

import re
import shutil
import subprocess
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
SMOKE = GEM_ROOT / "Code" / "Tools" / "SourceGenerators.Tests" / "SourceGenerators.Smoke.csproj"
SAMPLES = GEM_ROOT / "Code" / "Tools" / "SourceGenerators.Tests" / "DynamicDispatchSamples.cs"


def _build():
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    result = subprocess.run(
        ["dotnet", "build", str(SMOKE), "-c", "Release", "--nologo", "-t:Rebuild"],
        capture_output=True, text=True, timeout=900,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    return result.stdout


@pytest.mark.slow
def test_non_constant_bus_or_event_name_warns():
    output = _build()
    warnings = re.findall(r"DynamicDispatchSamples\.cs\((\d+),\d+\): warning O3DESHARP1001", output)
    assert warnings, (
        "O3DESHARP1001 did not fire on the deliberately-dynamic call sites in "
        "DynamicDispatchSamples.cs."
    )


@pytest.mark.slow
def test_constant_call_sites_are_silent():
    output = _build()
    # The sample file marks its constant-name calls with // CLOSED-WORLD.
    text = SAMPLES.read_text(encoding="utf-8").splitlines()
    closed = {i + 1 for i, line in enumerate(text) if "// CLOSED-WORLD" in line}
    warned = {int(n) for n in re.findall(r"DynamicDispatchSamples\.cs\((\d+),\d+\): warning O3DESHARP1001", output)}
    assert not (closed & warned), (
        f"constant-name call sites must NOT warn; these did: {sorted(closed & warned)}. "
        "A false positive here trains people to ignore the diagnostic."
    )


@pytest.mark.slow
def test_every_dynamic_site_is_flagged():
    output = _build()
    text = SAMPLES.read_text(encoding="utf-8").splitlines()
    open_world = {i + 1 for i, line in enumerate(text) if "// OPEN-WORLD" in line}
    warned = {int(n) for n in re.findall(r"DynamicDispatchSamples\.cs\((\d+),\d+\): warning O3DESHARP1001", output)}
    missed = open_world - warned
    assert not missed, (
        f"these dynamic call sites were not flagged: {sorted(missed)}. A missed site "
        "becomes a runtime hard error in a shipped game instead of a build warning."
    )


@pytest.mark.slow
def test_the_message_names_the_api_and_the_reason():
    output = _build()
    line = next((l for l in output.splitlines() if "O3DESHARP1001" in l), None)
    assert line, "expected at least one O3DESHARP1001 line"
    assert "NativeAOT" in line, "the message must say WHICH build config this breaks"
    for token in ("BroadcastEBusEvent", "SendEBusEvent"):
        if token in line:
            break
    else:
        pytest.fail("the message must name the API being called")
