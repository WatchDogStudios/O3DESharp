#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""sync_generated_bindings must be callable with no live Project Manager
dialog instance, so the Editor-startup auto-sync hook (Task 6) can drive
the same generate->build chain the "Generate Bindings" button already
does. See docs/superpowers/specs/2026-08-25-zero-config-gem-bindings-design.md §4.

csharp_editor_tools.py imports PySide2 and azlmbr at module scope (neither is
installed in the CI pytest environment) - stubbed out in sys.modules before
import, same pattern as test_csharp_editor_tools_validation.py.
"""

import sys
import types
from pathlib import Path
from unittest.mock import MagicMock

import pytest

EDITOR_SCRIPTS = Path(__file__).resolve().parents[1] / "Scripts"
sys.path.insert(0, str(EDITOR_SCRIPTS))


def _install_stub_modules():
    """Install minimal fake PySide2 / azlmbr packages so csharp_editor_tools
    imports without the real Qt / O3DE-editor runtime present."""
    if "PySide2" not in sys.modules:
        pyside2 = types.ModuleType("PySide2")

        qtwidgets = types.ModuleType("PySide2.QtWidgets")
        for name in [
            "QDialog", "QVBoxLayout", "QHBoxLayout", "QLabel", "QLineEdit",
            "QPushButton", "QListWidget", "QListWidgetItem", "QComboBox",
            "QCheckBox", "QMessageBox", "QWidget", "QSplitter", "QTextEdit",
            "QGroupBox", "QFormLayout", "QApplication", "QMenu", "QAction",
            "QFileDialog", "QProgressDialog", "QTreeWidget", "QTreeWidgetItem",
            "QAbstractItemView", "QFrame", "QScrollArea", "QSizePolicy",
            "QToolButton", "QStyle", "QTabWidget",
        ]:
            setattr(qtwidgets, name, MagicMock())

        qtcore = types.ModuleType("PySide2.QtCore")
        qtcore.Qt = MagicMock()
        qtcore.Signal = MagicMock(return_value=MagicMock())
        qtcore.QThread = MagicMock()

        qtgui = types.ModuleType("PySide2.QtGui")
        qtgui.QFont = MagicMock()
        qtgui.QIcon = MagicMock()

        pyside2.QtWidgets = qtwidgets
        pyside2.QtCore = qtcore
        pyside2.QtGui = qtgui

        sys.modules["PySide2"] = pyside2
        sys.modules["PySide2.QtWidgets"] = qtwidgets
        sys.modules["PySide2.QtCore"] = qtcore
        sys.modules["PySide2.QtGui"] = qtgui

    if "azlmbr" not in sys.modules:
        azlmbr = types.ModuleType("azlmbr")
        azlmbr_bus = types.ModuleType("azlmbr.bus")
        azlmbr_editor = types.ModuleType("azlmbr.editor")
        azlmbr_paths = types.ModuleType("azlmbr.paths")
        azlmbr.bus = azlmbr_bus
        azlmbr.editor = azlmbr_editor
        azlmbr.paths = azlmbr_paths

        sys.modules["azlmbr"] = azlmbr
        sys.modules["azlmbr.bus"] = azlmbr_bus
        sys.modules["azlmbr.editor"] = azlmbr_editor
        sys.modules["azlmbr.paths"] = azlmbr_paths


_install_stub_modules()


@pytest.mark.unit
def test_sync_generated_bindings_is_a_free_function_not_a_bound_method():
    import csharp_editor_tools

    assert hasattr(csharp_editor_tools, "sync_generated_bindings"), (
        "sync_generated_bindings must exist at module scope in "
        "csharp_editor_tools.py, not as a method on the Project Manager "
        "dialog class - a headless startup hook has no dialog instance to "
        "call it on."
    )


@pytest.mark.unit
def test_sync_generated_bindings_calls_invoker_with_reflection_source():
    import csharp_editor_tools

    invoker = MagicMock()
    config = MagicMock()
    worker = csharp_editor_tools.sync_generated_bindings(
        invoker=invoker,
        project_path="/fake/project",
        config=config,
        output_dir="/fake/project/Generated/CSharp",
        on_log=lambda line, level: None,
        on_finished=lambda result: None,
    )

    # sync_generated_bindings must set config.source before handing it to
    # the worker - same requirement _generate_bindings already enforces
    # (csharp_editor_tools.py:2390) so the editor flow stays on the
    # reflection backend by default.
    assert config.source == "reflection"
    worker.wait(5000)  # let the background thread finish before test teardown
