#
# Copyright (c) Contributors to the Open 3D Engine Project.
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Proves the M2 runtime-bundle mechanism: publishing the probe self-contained
for the host RID yields a private CoreCLR + hostfxr (no machine .NET needed).

Marked `slow` + skipped when dotnet is unavailable, so it never blocks the fast
unit run but does exercise the real mechanism on CI (Windows + Linux agents)."""

import shutil
import subprocess
import sys
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PROBE = REPO / "Code" / "Tools" / "RuntimeBundle" / "probe" / "probe.csproj"

# hostfxr + coreclr filenames per host OS (RID inferred from the runner).
_EXPECTED = {
    "win32": ("hostfxr.dll", "coreclr.dll"),
    "linux": ("libhostfxr.so", "libcoreclr.so"),
    "darwin": ("libhostfxr.dylib", "libcoreclr.dylib"),
}


def _host_key():
    if sys.platform.startswith("linux"):
        return "linux"
    return sys.platform


@pytest.mark.slow
def test_probe_publish_yields_private_runtime(tmp_path):
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    if not PROBE.exists():
        pytest.skip("probe project not present")

    out = tmp_path / "out"
    result = subprocess.run(
        ["dotnet", "publish", str(PROBE), "-c", "Release",
         "--self-contained", "true", "-o", str(out)],
        capture_output=True, text=True, timeout=600,
    )
    assert result.returncode == 0, result.stdout + result.stderr

    hostfxr, coreclr = _EXPECTED[_host_key()]
    files = {p.name for p in out.iterdir()}
    assert hostfxr in files, f"{hostfxr} missing from bundle: {sorted(files)[:20]}"
    assert coreclr in files, f"{coreclr} missing from bundle"
    assert "System.Private.CoreLib.dll" in files
