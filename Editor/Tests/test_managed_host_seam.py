#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Structural guards for the IManagedHost seam.

None of this C++ can be compiled in the authoring environment (no O3DE engine
SDK), so the properties that matter and are checkable from source are checked
here: the interface has exactly the four methods the design specifies, CoralHost
implements all of them, MakeNativeImports assigns every one of the 47 ABI
fields, and the wrapping refactor did not delete the Coral upload path it is
supposed to preserve.
"""

import re
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
SCRIPTING = GEM_ROOT / "Code" / "Source" / "Scripting"
IMANAGED_HOST = SCRIPTING / "IManagedHost.h"
CORAL_HOST_H = SCRIPTING / "CoralHost.h"
CORAL_HOST_CPP = SCRIPTING / "CoralHost.cpp"
SCRIPT_BINDINGS_CPP = SCRIPTING / "ScriptBindings.cpp"
HOST_ABI_H = SCRIPTING / "HostAbi.h"

REQUIRED_METHODS = ["Initialize", "GetExports", "SupportsHotReload", "Shutdown"]


def _read(path):
    assert path.is_file(), f"{path} is missing."
    return path.read_text(encoding="utf-8")


@pytest.mark.unit
def test_interface_declares_exactly_the_four_designed_methods():
    text = _read(IMANAGED_HOST)
    declared = re.findall(r"virtual\s+[\w:&*<>\s]+?\s(\w+)\s*\([^)]*\)\s*(?:const\s*)?=\s*0\s*;", text)
    assert sorted(declared) == sorted(REQUIRED_METHODS), (
        f"IManagedHost must declare exactly {REQUIRED_METHODS}, found {declared}. "
        "Widening the interface widens what every backend has to implement."
    )


@pytest.mark.unit
def test_coral_host_implements_all_four():
    text = _read(CORAL_HOST_H)
    for method in REQUIRED_METHODS:
        assert re.search(rf"\b{method}\s*\([^)]*\)\s*(?:const\s*)?override\s*;", text), (
            f"CoralHost must override {method}."
        )


@pytest.mark.unit
def test_make_native_imports_assigns_every_abi_field():
    abi_fields = re.findall(r"void\*\s+(\w+)\s*;", _read(HOST_ABI_H))
    native_imports = abi_fields[: abi_fields.index("Component_HasComponent") + 1]

    body = _read(SCRIPT_BINDINGS_CPP)
    m = re.search(r"MakeNativeImports\s*\(\s*\)", body)
    assert m, "ScriptBindings::MakeNativeImports must exist."
    tail = body[m.end():]

    missing = [f for f in native_imports if not re.search(rf"imports\.{f}\s*=", tail)]
    assert not missing, (
        f"MakeNativeImports leaves these ABI fields unassigned: {missing}. "
        "An unassigned field is a null pointer the managed side may call."
    )


@pytest.mark.unit
def test_make_native_imports_stamps_the_version():
    assert re.search(r"imports\.version\s*=\s*Abi::HostAbiVersion\s*;", _read(SCRIPT_BINDINGS_CPP)), (
        "MakeNativeImports must stamp the version, or the managed side cannot "
        "distinguish a populated struct from a zeroed one."
    )


@pytest.mark.unit
def test_the_coral_upload_path_is_preserved():
    # M3 is behaviour-preserving. The struct is descriptive under Coral; the
    # actual transport is still AddInternalCall + UploadInternalCalls.
    text = _read(SCRIPT_BINDINGS_CPP)
    assert "UploadInternalCalls()" in text, (
        "RegisterAll must still upload internal calls - the ABI struct does NOT "
        "replace Coral's transport in M3."
    )
    assert text.count("AddInternalCall") >= 47, (
        "the 47 AddInternalCall registrations must survive the refactor unchanged."
    )


@pytest.mark.unit
def test_coral_host_reports_hot_reload_from_the_config():
    text = _read(CORAL_HOST_CPP)
    assert "SupportsHotReload" in text and "enableHotReload" in text, (
        "CoralHost::SupportsHotReload must report the existing "
        "CoralHostConfig::enableHotReload gate rather than a hard-coded true."
    )


@pytest.mark.unit
def test_new_sources_are_in_the_build_file_list():
    files_cmake = (GEM_ROOT / "Code" / "o3desharp_private_files.cmake").read_text(encoding="utf-8")
    for entry in ("Source/Scripting/IManagedHost.h",
                  "Source/Scripting/CoralHost.h",
                  "Source/Scripting/CoralHost.cpp"):
        assert entry in files_cmake, f"{entry} is missing from o3desharp_private_files.cmake"
