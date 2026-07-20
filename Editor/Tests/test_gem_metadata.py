#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Guards on gem.json compatibility metadata.

O3DE's compatibility check short-circuits entirely when both compatible_engines
and engine_api_dependencies are empty (see o3de's compatibility.py), which turns
an engine/gem mismatch into a confusing C++ build failure 40 minutes in instead
of an immediate, legible refusal. These guards keep that from silently
regressing to empty.
"""

import json
from pathlib import Path

import pytest

GEM_JSON = Path(__file__).resolve().parents[2] / "gem.json"


@pytest.fixture
def gem():
    return json.loads(GEM_JSON.read_text(encoding="utf-8"))


@pytest.mark.unit
def test_gem_json_is_valid_json(gem):
    assert gem["gem_name"] == "O3DESharp"


@pytest.mark.unit
def test_compatible_engines_is_declared(gem):
    assert gem["compatible_engines"], "compatible_engines must not be empty"
    assert all(isinstance(e, str) and e for e in gem["compatible_engines"])


@pytest.mark.unit
def test_engine_api_dependencies_declared(gem):
    deps = gem["engine_api_dependencies"]
    assert deps, "engine_api_dependencies must not be empty (empty short-circuits the check)"
    # The gem uses AzCore/AzFramework, so the framework API is mandatory.
    assert any(d.startswith("framework") for d in deps), f"framework API not declared: {deps}"


@pytest.mark.unit
def test_repo_uri_is_set(gem):
    assert gem["repo_uri"].startswith("http"), "repo_uri must point at the gem repository"


README = Path(__file__).resolve().parents[2] / "README.md"


@pytest.mark.unit
def test_readme_documents_external_registration():
    text = README.read_text(encoding="utf-8")
    assert "o3de register --gem-path" in text, "README must document the registration workflow"
    assert "never be copied into an engine tree" in text, (
        "README must state that vendoring the gem is a rejected practice"
    )
