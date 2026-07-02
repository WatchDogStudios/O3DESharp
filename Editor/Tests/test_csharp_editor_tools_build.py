#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""
Unit tests for CSharpProjectManager.build_project()'s timeout handling.

These exercise only the pure-Python timeout/error-shape behavior added to
build_project() (Task 11, v1.2.0). The QThread wiring that moves
build_project() off the Qt UI thread lives in csharp_editor_tools.py, which
imports PySide2 and azlmbr.bus/azlmbr.editor at module scope - neither is
installed in this pytest environment, so that wiring is covered by a manual
verification checklist instead (see the v1.2.0 plan).
"""

import subprocess
import sys
import types
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest


def _install_azlmbr_stub():
    """
    csharp_project_manager.py does `import azlmbr.bus`, `import
    azlmbr.editor`, and `import azlmbr.paths` at module scope. None of
    those packages exist outside a running O3DE Editor process, so stub
    them in sys.modules before the module under test is imported.
    """
    azlmbr = types.ModuleType("azlmbr")
    azlmbr_bus = types.ModuleType("azlmbr.bus")
    azlmbr_editor = types.ModuleType("azlmbr.editor")
    azlmbr_paths = types.ModuleType("azlmbr.paths")
    azlmbr_paths.projectroot = str(Path(__file__).resolve().parents[2])
    azlmbr.bus = azlmbr_bus
    azlmbr.editor = azlmbr_editor
    azlmbr.paths = azlmbr_paths
    sys.modules["azlmbr"] = azlmbr
    sys.modules["azlmbr.bus"] = azlmbr_bus
    sys.modules["azlmbr.editor"] = azlmbr_editor
    sys.modules["azlmbr.paths"] = azlmbr_paths


_install_azlmbr_stub()

from csharp_project_manager import CSharpProjectManager  # noqa: E402


@pytest.mark.unit
class TestBuildProjectTimeout:
    """Test suite for build_project()'s subprocess timeout (Task 11)."""

    def _make_manager(self, tmp_path):
        manager = CSharpProjectManager.__new__(CSharpProjectManager)
        manager.project_path = tmp_path
        manager.gem_path = tmp_path
        manager.scripts_path = tmp_path
        return manager

    def test_build_times_out_returns_failure_dict(self, tmp_path):
        """A dotnet build that exceeds the timeout must return a failure
        dict, not raise subprocess.TimeoutExpired up to the caller."""
        project_dir = tmp_path / "MyGame"
        project_dir.mkdir()
        (project_dir / "MyGame.csproj").write_text("<Project />", encoding="utf-8")

        manager = self._make_manager(tmp_path)

        with patch.object(manager, "ensure_runtime_deployed",
                           return_value={"success": True, "message": "ok"}), \
             patch("csharp_project_manager._set_build_in_progress"), \
             patch("csharp_project_manager.subprocess.run",
                   side_effect=subprocess.TimeoutExpired(
                       cmd=["dotnet", "build"], timeout=300,
                       output="partial stdout", stderr="partial stderr")):
            result = manager.build_project(project_dir, "Release")

        assert result["success"] is False
        assert "timed out" in result["message"].lower()
        assert result["output_path"] is None
        assert "partial stdout" in result["build_output"]
        assert "partial stderr" in result["build_output"]

    def test_build_passes_timeout_to_subprocess_run(self, tmp_path):
        """subprocess.run must be called with timeout=300 so a hung
        dotnet build cannot block build_project() forever."""
        project_dir = tmp_path / "MyGame"
        project_dir.mkdir()
        (project_dir / "MyGame.csproj").write_text("<Project />", encoding="utf-8")

        manager = self._make_manager(tmp_path)

        mock_result = MagicMock()
        mock_result.returncode = 1
        mock_result.stdout = ""
        mock_result.stderr = "error"

        with patch.object(manager, "ensure_runtime_deployed",
                           return_value={"success": True, "message": "ok"}), \
             patch("csharp_project_manager._set_build_in_progress"), \
             patch("csharp_project_manager.subprocess.run",
                   return_value=mock_result) as mock_run:
            manager.build_project(project_dir, "Release")

        _, kwargs = mock_run.call_args
        assert kwargs.get("timeout") == 300

    def test_build_in_progress_flag_cleared_on_timeout(self, tmp_path):
        """_set_build_in_progress(False) must still run (via `finally`)
        even when the build times out, so the file-watcher coalescing
        flag never gets stuck on."""
        project_dir = tmp_path / "MyGame"
        project_dir.mkdir()
        (project_dir / "MyGame.csproj").write_text("<Project />", encoding="utf-8")

        manager = self._make_manager(tmp_path)

        with patch.object(manager, "ensure_runtime_deployed",
                           return_value={"success": True, "message": "ok"}), \
             patch("csharp_project_manager._set_build_in_progress") as mock_flag, \
             patch("csharp_project_manager.subprocess.run",
                   side_effect=subprocess.TimeoutExpired(
                       cmd=["dotnet", "build"], timeout=300)):
            manager.build_project(project_dir, "Release")

        mock_flag.assert_any_call(True)
        mock_flag.assert_any_call(False)
