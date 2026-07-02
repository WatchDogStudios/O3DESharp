#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""
Unit tests for CSharpEditorToolsHandler.ValidateScriptClass.

csharp_editor_tools.py imports PySide2 and azlmbr at module scope (neither is
installed in the CI pytest environment), so both are stubbed out in
sys.modules before import. ValidateScriptClass itself only touches
GetAvailableScriptClasses, string formatting, and dict construction - no Qt
or azlmbr calls - so once the module imports cleanly the method under test
can be exercised directly.
"""

import sys
import types
import pytest
from unittest.mock import MagicMock


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

from csharp_editor_tools import CSharpEditorToolsHandler  # noqa: E402


@pytest.mark.unit
class TestValidateScriptClass:
    """Test suite for CSharpEditorToolsHandler.ValidateScriptClass."""

    @pytest.fixture
    def handler(self, monkeypatch):
        """A CSharpEditorToolsHandler with project-manager construction
        short-circuited, so tests don't depend on a real project layout."""
        monkeypatch.setattr(
            "csharp_editor_tools.get_project_manager",
            lambda: MagicMock(),
        )
        return CSharpEditorToolsHandler()

    def test_empty_class_name_is_valid(self, handler):
        """Empty class name means 'no script' and is reported valid."""
        result = handler.ValidateScriptClass("")

        assert result["isValid"] is True
        assert result["message"] == "No script class specified"
        assert result["filePath"] == ""
        assert result["baseClass"] == ""

    def test_whitespace_only_class_name_is_valid(self, handler):
        """Whitespace-only class name is treated the same as empty."""
        result = handler.ValidateScriptClass("   ")

        assert result["isValid"] is True
        assert result["message"] == "No script class specified"

    def test_missing_namespace_is_invalid(self, handler):
        """A class name with no '.' fails the namespace format check."""
        result = handler.ValidateScriptClass("MyScript")

        assert result["isValid"] is False
        assert "namespace" in result["message"].lower()

    def test_class_found_on_disk_is_valid_with_metadata(self, handler, monkeypatch):
        """A class discovered on disk is valid and returns its real file
        path / base class - this is the case the native-only Coral check
        cannot distinguish from 'not found', because a freshly-created
        script may not be in a loaded assembly yet."""
        monkeypatch.setattr(
            handler,
            "GetAvailableScriptClasses",
            lambda scripts_only=True: [
                {
                    "fullName": "MyGame.PlayerController",
                    "className": "PlayerController",
                    "namespace": "MyGame",
                    "projectName": "MyGame",
                    "filePath": "C:/MyProject/Scripts/PlayerController.cs",
                    "baseClass": "ScriptComponent",
                    "isScriptComponent": True,
                    "isRecent": False,
                }
            ],
        )

        result = handler.ValidateScriptClass("MyGame.PlayerController")

        assert result["isValid"] is True
        assert result["baseClass"] == "ScriptComponent"
        assert result["filePath"] == "C:/MyProject/Scripts/PlayerController.cs"
        assert "PlayerController" not in result["message"]
        assert "ScriptComponent" in result["message"]

    def test_class_not_found_reports_not_compiled_hint(self, handler, monkeypatch):
        """A well-formed name that matches no discovered .cs file (bad path,
        typo, or a script that hasn't been compiled yet) gets the specific
        'not found ... Ensure it's compiled' message, with no stale
        filePath/baseClass."""
        monkeypatch.setattr(
            handler, "GetAvailableScriptClasses", lambda scripts_only=True: []
        )

        result = handler.ValidateScriptClass("MyGame.DoesNotExist")

        assert result["isValid"] is False
        assert "MyGame.DoesNotExist" in result["message"]
        assert "compiled" in result["message"].lower()
        assert result["filePath"] == ""
        assert result["baseClass"] == ""

    def test_exception_in_lookup_is_caught_and_reported(self, handler, monkeypatch):
        """If GetAvailableScriptClasses blows up, ValidateScriptClass must
        not propagate - it should degrade to an isValid=False result."""
        def _raise(scripts_only=True):
            raise RuntimeError("simulated project scan failure")

        monkeypatch.setattr(handler, "GetAvailableScriptClasses", _raise)

        result = handler.ValidateScriptClass("MyGame.PlayerController")

        assert result["isValid"] is False
        assert "simulated project scan failure" in result["message"]
