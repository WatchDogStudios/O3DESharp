#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""
Regression tests for the "Clear Selection" vs "Cancel" script picker fix.

csharp_editor_tools.py imports PySide2 and azlmbr at module scope, neither of
which is available outside a running O3DE Editor process, so this module
cannot be imported directly in the CI Python environment. These tests instead
parse the module's source text to verify the sentinel-based fix is present
and internally consistent.
"""

import re
from pathlib import Path

import pytest

_MODULE_PATH = Path(__file__).parent.parent / "Scripts" / "csharp_editor_tools.py"


@pytest.fixture(scope="module")
def source_text() -> str:
    return _MODULE_PATH.read_text(encoding="utf-8")


@pytest.mark.unit
class TestScriptPickerClearVsCancelSentinel:
    def test_sentinel_constant_defined_once(self, source_text):
        matches = re.findall(
            r'^SCRIPT_PICKER_CLEARED_SENTINEL\s*=\s*"([^"]+)"',
            source_text,
            re.MULTILINE,
        )
        assert len(matches) == 1, (
            "Expected exactly one SCRIPT_PICKER_CLEARED_SENTINEL definition, "
            f"found {len(matches)}"
        )
        sentinel_value = matches[0]
        assert sentinel_value != "", "Sentinel must not itself be the empty string"

    def test_select_none_uses_sentinel_not_empty_string(self, source_text):
        match = re.search(
            r"def _select_none\(self\):.*?(?=\n    def |\Z)",
            source_text,
            re.DOTALL,
        )
        assert match is not None, "_select_none method not found"
        body = match.group(0)

        assert "SCRIPT_PICKER_CLEARED_SENTINEL" in body, (
            "_select_none must assign the sentinel to self._selected_class "
            "so OpenScriptPicker can distinguish explicit clear from cancel"
        )
        assert 'self._selected_class = ""' not in body, (
            "_select_none must not set _selected_class to a bare empty "
            "string -- that collides with the Cancel return value"
        )

    def test_open_script_picker_cancel_path_returns_empty_string(self, source_text):
        match = re.search(
            r"def OpenScriptPicker\(self, current_class\):.*?(?=\n    def |\Z)",
            source_text,
            re.DOTALL,
        )
        assert match is not None, "OpenScriptPicker method not found"
        body = match.group(0)

        assert re.search(r'return\s+""\s*#\s*Cancelled', body), (
            'OpenScriptPicker must still return "" for the cancelled path'
        )

    def test_open_script_picker_does_not_add_sentinel_to_recent_classes(self, source_text):
        match = re.search(
            r"def OpenScriptPicker\(self, current_class\):.*?(?=\n    def |\Z)",
            source_text,
            re.DOTALL,
        )
        assert match is not None, "OpenScriptPicker method not found"
        body = match.group(0)

        assert "SCRIPT_PICKER_CLEARED_SENTINEL" in body, (
            "OpenScriptPicker must reference the sentinel (to exclude it "
            "from AddToRecentClasses)"
        )
