#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""The M4 exit criteria, as far as they are checkable without a launcher.

A sample game's scripts must build against O3DE.Core, register themselves with
the script-type registry through generated factories, and trip the closed-world
diagnostic on a deliberately-dynamic call. Actually RUNNING them in a desktop
NativeAOT launcher needs an engine build and is the maintainer's step.
"""

import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
EXAMPLES = GEM_ROOT / "Assets" / "Scripts" / "Examples" / "Examples.csproj"
SAMPLE = GEM_ROOT / "Assets" / "Scripts" / "Examples" / "AotSampleComponent.cs"
VS_INSTALLER = r"C:\Program Files (x86)\Microsoft Visual Studio\Installer"


def _build(*extra):
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    return subprocess.run(
        ["dotnet", "build", str(EXAMPLES), "-c", "Release", "--nologo", "-t:Rebuild", *extra],
        capture_output=True, text=True, timeout=900,
    )


@pytest.mark.unit
def test_sample_project_exists_and_references_o3de_core():
    assert EXAMPLES.is_file(), f"{EXAMPLES} is missing."
    text = EXAMPLES.read_text(encoding="utf-8")
    assert "O3DE.Core.csproj" in text
    assert "<TargetFramework>net9.0</TargetFramework>" in text, (
        "must match O3DE.Core's TFM; a mismatch is the drift class a prior audit caught."
    )


@pytest.mark.unit
def test_sample_does_not_emit_its_own_host_exports():
    # ManagedExports lives in O3DE.Core alone. A second copy here would make
    # the type name ambiguous the moment both were referenced.
    assert "O3DESharpEmitHostExports" not in EXAMPLES.read_text(encoding="utf-8")


@pytest.mark.slow
def test_sample_builds_and_the_diagnostic_fires_on_the_dynamic_call():
    result = _build()
    assert result.returncode == 0, result.stdout + result.stderr

    warned = re.findall(r"AotSampleComponent\.cs\((\d+),\d+\): warning O3DESHARP1001", result.stdout)
    assert warned, (
        "the deliberately-dynamic call in AotSampleComponent must trip O3DESHARP1001 - "
        "that is the closed-world diagnostic firing on real game code, not just a fixture."
    )

    text = SAMPLE.read_text(encoding="utf-8").splitlines()
    closed = {i + 1 for i, line in enumerate(text) if "// CLOSED-WORLD" in line}
    assert not (closed & {int(n) for n in warned}), (
        "the constant-name calls in the sample must stay silent."
    )


@pytest.mark.slow
def test_sample_publishes_as_nativeaot(tmp_path):
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    if sys.platform != "win32":
        pytest.skip("win-x64 publish; linux-x64 is verified by the maintainer")

    env = dict(os.environ)
    if Path(VS_INSTALLER).is_dir():
        env["PATH"] = VS_INSTALLER + os.pathsep + env.get("PATH", "")

    out = tmp_path / "aot"
    result = subprocess.run(
        [
            "dotnet", "publish", str(EXAMPLES), "-c", "Release", "-r", "win-x64",
            "-p:PublishAot=true", "-p:NativeLib=Shared",
            "-p:O3DESharpHostMode=NativeAot", "-o", str(out), "--nologo",
        ],
        capture_output=True, text=True, timeout=1800, env=env,
    )
    assert result.returncode == 0, result.stdout + result.stderr

    native = out / "Examples.dll"
    assert native.is_file()
    # Measured 2026-08-24: a Coral (managed) Examples.dll is well under 100 KB;
    # the real NativeAOT image measured ~1.16 MB (1,219,584 bytes) - almost
    # identical to O3DE.Core's own measured NativeAOT size in
    # test_nativeaot_publish.py (~1.19 MB), since most of the bulk is the
    # statically-linked runtime plus O3DE.Core, not Examples' own small amount
    # of code. 500 KB comfortably distinguishes "published the native image"
    # from "copied the IL one", matching the threshold used there.
    assert native.stat().st_size > 500_000, (
        f"Examples.dll is only {native.stat().st_size} bytes - that is the managed "
        "assembly, not a NativeAOT image."
    )
