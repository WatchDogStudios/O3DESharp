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
