#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""HotReloadManager must not exist in a shipping NativeAOT image.

It is built entirely on Assembly.GetType + Activator.CreateInstance + field
reflection - the exact pattern a NativeAOT image cannot see through. Hot-reload
is editor-only by design (there is no AssemblyLoadContext to reload into), so
the file is guarded out rather than annotated: annotating machinery that must
never run in the shipping artifact would be work with no consumer.

Marked `slow` because it shells out to `dotnet build`.
"""

import re
import shutil
import subprocess
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
CORE_CSPROJ = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "O3DE.Core.csproj"
HOT_RELOAD = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "HotReload" / "HotReloadManager.cs"


@pytest.mark.unit
def test_file_is_guarded_by_the_host_mode_symbol():
    text = HOT_RELOAD.read_text(encoding="utf-8")
    assert re.search(r"^#if\s+!O3DE_HOST_NATIVEAOT\s*$", text, re.MULTILINE), (
        "HotReloadManager.cs must open with '#if !O3DE_HOST_NATIVEAOT'."
    )
    assert text.rstrip().endswith("#endif"), (
        "the guard must close at end of file so the whole type is excluded, "
        "not just part of it."
    )


@pytest.mark.unit
def test_file_still_has_no_callers():
    # The guard is only safe while nothing references it. If a caller appears,
    # that caller needs guarding too - and this test is the reminder.
    # Scan the entire repo (Assets/ and Code/ and any other C# sources).
    hits = []
    for path in GEM_ROOT.rglob("*.cs"):
        if path == HOT_RELOAD or any(part in ("obj", "bin", ".git", ".claude", ".worktrees", ".superpowers") for part in path.parts):
            continue
        if "HotReloadManager" in path.read_text(encoding="utf-8", errors="ignore"):
            hits.append(str(path))
    assert not hits, (
        "HotReloadManager gained callers: " + ", ".join(hits) +
        ". Guard them for O3DE_HOST_NATIVEAOT too, or the shipping build will not compile."
    )


@pytest.mark.slow
def test_nativeaot_mode_reports_no_il_warnings_for_the_file():
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    result = subprocess.run(
        [
            "dotnet", "build", str(CORE_CSPROJ), "-c", "Release", "--nologo",
            "-p:O3DESharpHostMode=NativeAot", "-p:IsAotCompatible=true", "-t:Rebuild",
        ],
        capture_output=True, text=True, timeout=900,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    offenders = re.findall(r"HotReloadManager\.cs.*?warning (IL\d+)", result.stdout)
    assert not offenders, (
        f"HotReloadManager.cs still reports {offenders} in NativeAot mode - the guard is not covering it."
    )
