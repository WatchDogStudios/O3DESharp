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
_HEADER_PATH = (
    Path(__file__).parent.parent.parent / "Code" / "Source" / "Tools" / "CSharpEditorToolsBus.h"
)
_EXPECTED_SENTINEL_VALUE = "__O3DESharp_ClearSelection__"


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
        assert sentinel_value == _EXPECTED_SENTINEL_VALUE, (
            "Sentinel value drifted from the expected literal - if this was "
            "intentional, update CSharpEditorToolsBus.h's ScriptPickerClearedSentinel "
            "to match, and update _EXPECTED_SENTINEL_VALUE in this test file"
        )

    def test_python_and_cpp_sentinel_literals_match(self, source_text):
        """
        The Python and C++ sides compare this sentinel via plain string
        equality across the CSharpEditorToolsBus EBus, with no shared build
        step to catch a one-sided typo. A drift here wouldn't cause a build
        or import failure on either side - it would silently make "Clear
        Selection" start behaving like "Cancel" again. This test is the only
        thing that actually catches that.
        """
        py_match = re.search(
            r'^SCRIPT_PICKER_CLEARED_SENTINEL\s*=\s*"([^"]+)"', source_text, re.MULTILINE
        )
        assert py_match is not None, "SCRIPT_PICKER_CLEARED_SENTINEL not found in Python source"
        py_value = py_match.group(1)

        assert _HEADER_PATH.exists(), f"C++ header not found at {_HEADER_PATH}"
        cpp_text = _HEADER_PATH.read_text(encoding="utf-8")
        cpp_match = re.search(
            r'ScriptPickerClearedSentinel\s*=\s*"([^"]+)"', cpp_text
        )
        assert cpp_match is not None, (
            "ScriptPickerClearedSentinel not found in CSharpEditorToolsBus.h"
        )
        cpp_value = cpp_match.group(1)

        assert py_value == cpp_value, (
            f"Sentinel literal mismatch: Python has {py_value!r}, "
            f"C++ CSharpEditorToolsBus.h has {cpp_value!r} - these must be "
            "byte-for-byte identical since they're compared with == across "
            "the EBus boundary"
        )

    def test_add_to_recent_classes_excludes_sentinel(self, source_text):
        """
        The exclusion must live in AddToRecentClasses itself, not only in
        OpenScriptPicker's call site - otherwise a future caller that routes
        through AddToRecentClasses directly (e.g. a refactor sharing code
        with the non-EBus show_script_class_picker() entry point) would
        silently start adding the sentinel to the recent-classes list.
        """
        match = re.search(
            r"def AddToRecentClasses\(self, class_name\):.*?(?=\n    def |\Z)",
            source_text,
            re.DOTALL,
        )
        assert match is not None, "AddToRecentClasses method not found"
        body = match.group(0)

        assert "SCRIPT_PICKER_CLEARED_SENTINEL" in body, (
            "AddToRecentClasses must itself check for and exclude "
            "SCRIPT_PICKER_CLEARED_SENTINEL, not rely solely on callers to "
            "filter it out before calling"
        )

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
