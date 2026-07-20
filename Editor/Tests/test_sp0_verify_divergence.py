#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Unit tests for the SP-0 divergence verification tool.

The tool decides whether deleting the vendored gem copy is lossless, so its
classifier is the gate on an irreversible action. These tests pin the two
behaviours that matter: CRLF/LF differences must NOT register as divergence
(they would drown the real signal), and any unexpected vendored-only file must
trip the gate.
"""

import importlib.util
import sys
from pathlib import Path

import pytest

_TOOL = Path(__file__).resolve().parents[2] / "Tools" / "sp0_verify_divergence.py"
_spec = importlib.util.spec_from_file_location("sp0_verify_divergence", _TOOL)
sp0 = importlib.util.module_from_spec(_spec)
sys.modules["sp0_verify_divergence"] = sp0
_spec.loader.exec_module(sp0)


def _write(root: Path, rel: str, text: str, newline: str = "\n"):
    p = root / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_bytes(text.replace("\n", newline).encode("utf-8"))


@pytest.mark.unit
def test_normalize_collapses_crlf():
    assert sp0.normalize(b"a\r\nb\r\n") == b"a\nb\n"


@pytest.mark.unit
def test_crlf_only_difference_counts_as_identical(tmp_path):
    vend, repo = tmp_path / "vend", tmp_path / "repo"
    _write(vend, "Code/a.cpp", "int main() {}\n", newline="\r\n")
    _write(repo, "Code/a.cpp", "int main() {}\n", newline="\n")

    result = sp0.classify(vend, repo)

    assert result["identical"] == ["Code/a.cpp"]
    assert result["differs"] == []
    assert result["only_in_vendored"] == []


@pytest.mark.unit
def test_real_content_difference_is_reported(tmp_path):
    vend, repo = tmp_path / "vend", tmp_path / "repo"
    _write(vend, "Code/a.cpp", "old\n")
    _write(repo, "Code/a.cpp", "new\n")

    result = sp0.classify(vend, repo)

    assert result["differs"] == ["Code/a.cpp"]
    assert result["identical"] == []


@pytest.mark.unit
def test_vendored_only_file_is_reported(tmp_path):
    vend, repo = tmp_path / "vend", tmp_path / "repo"
    _write(vend, "Code/ghost.cpp", "x\n")
    repo.mkdir(parents=True, exist_ok=True)

    result = sp0.classify(vend, repo)

    assert result["only_in_vendored"] == ["Code/ghost.cpp"]


@pytest.mark.unit
def test_build_output_directories_are_ignored(tmp_path):
    vend, repo = tmp_path / "vend", tmp_path / "repo"
    _write(vend, "bin/junk.dll", "x\n")
    _write(vend, "Code/obj/junk.o", "x\n")
    _write(vend, ".git/config", "x\n")
    repo.mkdir(parents=True, exist_ok=True)

    result = sp0.classify(vend, repo)

    assert result["only_in_vendored"] == []


@pytest.mark.unit
def test_gate_passes_for_allowlisted_rescued_files():
    result = {"identical": [], "differs": [], "only_in_vendored": sorted(sp0.ALLOWED_ONLY_IN_VENDORED)}
    assert sp0.gate(result) == []


@pytest.mark.unit
def test_gate_fails_for_unexpected_vendored_only_file():
    result = {"identical": [], "differs": [], "only_in_vendored": ["Code/Surprise.cpp"]}
    assert sp0.gate(result) == ["Code/Surprise.cpp"]


@pytest.mark.unit
def test_manifest_states_the_verdict():
    passing = sp0.render_manifest({"identical": ["a"], "differs": [], "only_in_vendored": []}, [])
    assert "DELETION APPROVED" in passing

    failing = sp0.render_manifest({"identical": [], "differs": [], "only_in_vendored": ["x"]}, ["x"])
    assert "DELETION BLOCKED" in failing
    assert "x" in failing
