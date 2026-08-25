#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Editor-startup auto-sync must fire silently (log-only, no modal popup)
and must not raise if reflection_data.json doesn't exist yet (fresh
checkout, Editor never launched before). See
docs/superpowers/specs/2026-08-25-zero-config-gem-bindings-design.md §4.

csharp_editor_bootstrap.py imports azlmbr at module scope (not installed in
the CI pytest environment) - stubbed out in sys.modules before import, same
pattern as test_sync_generated_bindings.py.
"""

import sys
import types
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest

EDITOR_SCRIPTS = Path(__file__).resolve().parents[1] / "Scripts"
sys.path.insert(0, str(EDITOR_SCRIPTS))


def _install_stub_modules():
    """Install a minimal fake azlmbr package so csharp_editor_bootstrap
    imports without the real O3DE-editor runtime present."""
    if "azlmbr" not in sys.modules:
        azlmbr = types.ModuleType("azlmbr")
        azlmbr_bus = types.ModuleType("azlmbr.bus")
        azlmbr_editor = types.ModuleType("azlmbr.editor")
        azlmbr_paths = types.ModuleType("azlmbr.paths")
        azlmbr_paths.projectroot = "/fake/project"
        azlmbr_legacy = types.ModuleType("azlmbr.legacy")
        azlmbr_legacy_general = types.ModuleType("azlmbr.legacy.general")
        azlmbr_legacy_general.log = MagicMock()

        azlmbr.bus = azlmbr_bus
        azlmbr.editor = azlmbr_editor
        azlmbr.paths = azlmbr_paths
        azlmbr.legacy = azlmbr_legacy
        azlmbr_legacy.general = azlmbr_legacy_general

        sys.modules["azlmbr"] = azlmbr
        sys.modules["azlmbr.bus"] = azlmbr_bus
        sys.modules["azlmbr.editor"] = azlmbr_editor
        sys.modules["azlmbr.paths"] = azlmbr_paths
        sys.modules["azlmbr.legacy"] = azlmbr_legacy
        sys.modules["azlmbr.legacy.general"] = azlmbr_legacy_general


_install_stub_modules()


@pytest.mark.unit
def test_auto_sync_generated_bindings_exists():
    import csharp_editor_bootstrap

    assert hasattr(csharp_editor_bootstrap, "auto_sync_generated_bindings")


@pytest.mark.unit
def test_auto_sync_generated_bindings_never_shows_a_dialog():
    """
    A QMessageBox popup on every Editor startup (as opposed to only when
    the user explicitly clicks "Generate Bindings") would be a major UX
    regression - the source of this call must be sync_generated_bindings
    with log-only callbacks, never the dialog-driven
    _on_sync_generated_bindings_finished handler.
    """
    import csharp_editor_bootstrap
    import inspect

    source = inspect.getsource(csharp_editor_bootstrap.auto_sync_generated_bindings)
    assert "QMessageBox" not in source, (
        "auto_sync_generated_bindings must not show a QMessageBox - it "
        "runs unattended on every Editor startup, not on a user-initiated click."
    )


@pytest.mark.unit
def test_auto_sync_skips_when_reflection_data_missing():
    """Fresh checkout: nothing has exported reflection_data.json yet, so
    there is nothing to generate from - skip instead of firing a doomed
    generate (and don't raise)."""
    import csharp_editor_bootstrap

    tools = MagicMock()
    with patch.object(csharp_editor_bootstrap, "_import_csharp_editor_tools", return_value=tools):
        # azlmbr.paths.projectroot is the stub's "/fake/project"
        csharp_editor_bootstrap.auto_sync_generated_bindings()

    tools.sync_generated_bindings.assert_not_called()


def _prepare_project(tmp_path, stamp_offset):
    """reflection_data.json + a .last_synced stamp offset_seconds newer
    (positive) or older (negative) than it."""
    import os

    generated = tmp_path / "Generated"
    (generated / "CSharp").mkdir(parents=True)
    reflection_data = generated / "reflection_data.json"
    reflection_data.write_text("{}", encoding="utf-8")
    stamp = generated / "CSharp" / ".last_synced"
    stamp.write_text("", encoding="utf-8")
    mtime = reflection_data.stat().st_mtime + stamp_offset
    os.utime(stamp, (mtime, mtime))
    return stamp


def _run_auto_sync_against(tmp_path):
    import azlmbr.paths as paths
    import csharp_editor_bootstrap

    tools = MagicMock()
    with patch.object(paths, "projectroot", str(tmp_path)), \
            patch.object(csharp_editor_bootstrap, "_import_csharp_editor_tools", return_value=tools):
        csharp_editor_bootstrap.auto_sync_generated_bindings()
    return tools


@pytest.mark.unit
def test_auto_sync_skips_when_stamp_is_newer_than_reflection_data(tmp_path):
    """Nothing changed since the last successful sync - don't pay for a
    full regenerate + dotnet build + hot-reload on every Editor launch."""
    _prepare_project(tmp_path, stamp_offset=60)
    _run_auto_sync_against(tmp_path).sync_generated_bindings.assert_not_called()


@pytest.mark.unit
def test_auto_sync_runs_when_reflection_data_is_newer_than_stamp(tmp_path):
    _prepare_project(tmp_path, stamp_offset=-60)
    _run_auto_sync_against(tmp_path).sync_generated_bindings.assert_called_once()


@pytest.mark.unit
def test_auto_sync_generated_bindings_is_non_fatal_on_exception():
    import csharp_editor_bootstrap

    with patch.object(csharp_editor_bootstrap, "_import_csharp_editor_tools", side_effect=RuntimeError("boom")):
        # Must not raise - this runs inside initialize_ebus_handler()'s
        # startup path, and an uncaught exception there would break Editor
        # startup for an experimental, opt-in-by-default convenience feature.
        csharp_editor_bootstrap.auto_sync_generated_bindings()
