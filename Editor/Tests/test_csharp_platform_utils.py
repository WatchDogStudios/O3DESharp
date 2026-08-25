#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Unit tests for csharp_platform_utils (stdlib-only cross-platform helpers)."""

import os
from pathlib import Path

import pytest

import csharp_platform_utils as ppu


@pytest.mark.unit
def test_resolve_dotnet_prefers_env_override(monkeypatch, tmp_path):
    exe = tmp_path / "dotnet"
    exe.write_text("#!/bin/sh\n")
    monkeypatch.setenv("O3DESHARP_DOTNET_EXECUTABLE", str(exe))
    monkeypatch.setattr(ppu.shutil, "which", lambda name: "/usr/bin/dotnet")
    assert ppu.resolve_dotnet() == str(exe)


@pytest.mark.unit
def test_resolve_dotnet_falls_back_to_which(monkeypatch):
    monkeypatch.delenv("O3DESHARP_DOTNET_EXECUTABLE", raising=False)
    monkeypatch.setattr(ppu.shutil, "which", lambda name: "/opt/dotnet/dotnet")
    assert ppu.resolve_dotnet() == "/opt/dotnet/dotnet"


@pytest.mark.unit
def test_resolve_dotnet_uses_dotnet_root(monkeypatch, tmp_path):
    monkeypatch.delenv("O3DESHARP_DOTNET_EXECUTABLE", raising=False)
    monkeypatch.setattr(ppu.shutil, "which", lambda name: None)
    root = tmp_path / "root"
    root.mkdir()
    exe = root / ppu._DOTNET_EXE_NAME
    exe.write_text("x")
    monkeypatch.setenv("DOTNET_ROOT", str(root))
    assert ppu.resolve_dotnet() == str(exe)


@pytest.mark.unit
def test_resolve_dotnet_bare_fallback(monkeypatch):
    monkeypatch.delenv("O3DESHARP_DOTNET_EXECUTABLE", raising=False)
    monkeypatch.delenv("DOTNET_ROOT", raising=False)
    monkeypatch.setattr(ppu.shutil, "which", lambda name: None)
    monkeypatch.setattr(ppu, "_common_dotnet_locations", lambda: [])
    assert ppu.resolve_dotnet() == "dotnet"


@pytest.mark.unit
@pytest.mark.parametrize("os_name,platform,expected", [
    ("nt", "win32", None),
    ("posix", "darwin", "open"),
    ("posix", "linux", "xdg-open"),
])
def test_default_opener(os_name, platform, expected):
    assert ppu._default_opener(os_name=os_name, platform=platform) == expected


@pytest.mark.unit
def test_common_dotnet_locations_linux_branch():
    """The Linux dotnet-search branch a real Linux editor hits (other tests
    mock this out). Path equality is separator-agnostic, so this asserts the
    intended search set even when the test itself runs on Windows."""
    locs = ppu._common_dotnet_locations(os_name="posix", platform="linux")
    # Canonical system install dirs are searched...
    assert Path("/usr/share/dotnet/dotnet") in locs
    assert Path("/usr/bin/dotnet") in locs
    assert Path("/usr/local/share/dotnet/dotnet") in locs
    # ...and no Windows Program Files locations leak into the Linux branch.
    assert not any("Program" in str(p) for p in locs)


@pytest.mark.unit
def test_common_dotnet_locations_darwin_branch():
    """The macOS branch, injected the same way."""
    locs = ppu._common_dotnet_locations(os_name="posix", platform="darwin")
    assert Path("/usr/local/share/dotnet/dotnet") in locs
    assert Path("/opt/homebrew/bin/dotnet") in locs
    assert not any("Program" in str(p) for p in locs)


@pytest.mark.unit
def test_open_in_default_app_uses_opener_on_linux(monkeypatch):
    calls = {}
    monkeypatch.setattr(ppu, "_default_opener", lambda: "xdg-open")

    def fake_popen(argv, **kwargs):
        calls["argv"] = argv
        return object()

    monkeypatch.setattr(ppu.subprocess, "Popen", fake_popen)
    assert ppu.open_in_default_app("/tmp/x.cs") is True
    assert calls["argv"] == ["xdg-open", "/tmp/x.cs"]


@pytest.mark.unit
def test_open_in_default_app_returns_false_on_error(monkeypatch):
    monkeypatch.setattr(ppu, "_default_opener", lambda: "xdg-open")

    def boom(argv, **kwargs):
        raise OSError("no opener")

    monkeypatch.setattr(ppu.subprocess, "Popen", boom)
    assert ppu.open_in_default_app("/tmp/x.cs") is False


@pytest.mark.unit
def test_render_launch_json_linux():
    text = ppu.render_vscode_launch_json(host_platform="linux")
    assert "build/linux/bin/profile/" in text
    assert '"default": "GameLauncher"' in text
    assert ".exe" not in text


@pytest.mark.unit
def test_render_launch_json_windows_is_valid_json():
    import json
    text = ppu.render_vscode_launch_json(host_platform="win32")
    assert "build/windows/bin/profile/" in text
    assert '"default": "GameLauncher.exe"' in text
    json.loads(text)  # must parse
