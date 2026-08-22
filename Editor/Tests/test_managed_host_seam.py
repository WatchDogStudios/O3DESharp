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
def test_make_native_imports_assigns_every_abi_field_in_order_to_the_matching_function():
    # HostAbi.h field declaration order IS the source of truth for the ABI.
    abi_fields = re.findall(r"void\*\s+(\w+)\s*;", _read(HOST_ABI_H))
    native_imports = abi_fields[: abi_fields.index("Component_HasComponent") + 1]

    body = _read(SCRIPT_BINDINGS_CPP)
    m = re.search(r"MakeNativeImports\s*\(\s*\)", body)
    assert m, "ScriptBindings::MakeNativeImports must exist."
    # Bound the scan to just this function's body, not the rest of the file.
    end = body.index("return imports;", m.end())
    func_body = body[m.end():end]

    # Ordered (field, function) pairs as they actually appear in the
    # assignment sequence - this is the pairing that would catch a
    # copy-paste swap between two adjacent fields (e.g. Transform_GetForward
    # accidentally assigned &Transform_GetUp), which an unordered
    # presence-only check cannot.
    assignments = re.findall(r"imports\.(\w+)\s*=\s*reinterpret_cast<void\*>\(&(\w+)\)\s*;", func_body)

    assert len(assignments) == len(native_imports), (
        f"MakeNativeImports has {len(assignments)} field assignments, expected "
        f"{len(native_imports)} (one per NativeImports field). An unassigned "
        "field is a null pointer the managed side may call."
    )

    for index, (expected_field, (assigned_field, assigned_fn)) in enumerate(zip(native_imports, assignments)):
        assert assigned_field == expected_field, (
            f"MakeNativeImports assignment #{index} is for '{assigned_field}', but "
            f"HostAbi.h declares field #{index} as '{expected_field}'. Assignment "
            "order must match declaration order exactly."
        )
        assert assigned_fn == expected_field, (
            f"MakeNativeImports assigns imports.{assigned_field} the address of "
            f"'{assigned_fn}', a name mismatch. Every field must be assigned the "
            "address of the identically-named function."
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


SYSTEM_COMPONENT_H = GEM_ROOT / "Code" / "Source" / "Clients" / "O3DESharpSystemComponent.h"
SYSTEM_COMPONENT_CPP = GEM_ROOT / "Code" / "Source" / "Clients" / "O3DESharpSystemComponent.cpp"


@pytest.mark.unit
def test_system_component_owns_a_managed_host():
    header = _read(SYSTEM_COMPONENT_H)
    assert re.search(r"AZStd::unique_ptr<CoralHost>\s+m_managedHost\s*;", header), (
        "O3DESharpSystemComponent must own the CoralHost adapter."
    )
    assert "AZStd::unique_ptr<CoralHostManager> m_coralHostManager;" in header, (
        "the existing manager member must survive - M3 wraps it, it does not replace it."
    )


@pytest.mark.unit
def test_both_interfaces_are_registered_and_unregistered():
    body = _read(SYSTEM_COMPONENT_CPP)
    # The pre-existing registration must not be disturbed.
    assert "CoralHostManagerInterface::Register(m_coralHostManager.get())" in body
    assert "CoralHostManagerInterface::Unregister(m_coralHostManager.get())" in body
    # ...and the new seam registered alongside it.
    assert "ManagedHostInterface::Register(m_managedHost.get())" in body
    assert "ManagedHostInterface::Unregister(m_managedHost.get())" in body


@pytest.mark.unit
def test_the_host_is_initialised_through_the_abi_struct():
    body = _read(SYSTEM_COMPONENT_CPP)
    assert "ScriptBindings::MakeNativeImports()" in body, (
        "the seam must actually be exercised in the editor path, not just declared."
    )
    assert re.search(r"m_managedHost->Initialize\(", body), (
        "InitializeCoralHost must drive the manager THROUGH CoralHost::Initialize, "
        "so the editor exercises the same entry point NativeAotHost will."
    )


@pytest.mark.unit
def test_manager_is_not_initialised_twice():
    body = _read(SYSTEM_COMPONENT_CPP)
    # CoralHost::Initialize calls m_manager.Initialize(config). A surviving
    # direct call here would run CLR bring-up twice and return
    # AlreadyInitialized on the second, which reads as a spurious warning.
    assert "m_coralHostManager->Initialize(config)" not in body, (
        "CoralHost::Initialize already drives the manager; a direct call here "
        "would initialise it twice."
    )
