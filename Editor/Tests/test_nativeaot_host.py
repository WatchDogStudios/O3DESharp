#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Structural guards for the NativeAOT backend.

Not compile-verifiable here (no O3DE engine SDK), so the properties that
distinguish this backend from CoralHost - and that a careless edit would
silently undo - are checked against the source.
"""

import re
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
SCRIPTING = GEM_ROOT / "Code" / "Source" / "Scripting"
HOST_H = SCRIPTING / "NativeAotHost.h"
HOST_CPP = SCRIPTING / "NativeAotHost.cpp"


def _read(path):
    assert path.is_file(), f"{path} is missing."
    return path.read_text(encoding="utf-8")


@pytest.mark.unit
def test_implements_the_full_interface():
    text = _read(HOST_H)
    for method in ("Initialize", "GetExports", "SupportsHotReload", "Shutdown"):
        assert re.search(rf"\b{method}\s*\([^)]*\)\s*(?:const\s*)?override\s*;", text), method


@pytest.mark.unit
def test_does_not_touch_coral_or_hostfxr():
    # A NativeAOT image has no JIT and no hostfxr consumer; there is nothing
    # for nethost -> hostfxr to attach to. Any reference here means the two
    # hosting models got mixed, which is the one thing the design forbids.
    both = _read(HOST_H) + _read(HOST_CPP)
    banned = re.findall(r"\b(Coral::\w+|hostfxr_\w+|get_hostfxr_path|nethost)\b", both)
    assert not banned, (
        f"NativeAotHost must not reference the CoreCLR hosting stack: {sorted(set(banned))}"
    )


@pytest.mark.unit
def test_resolves_exactly_one_exported_symbol():
    body = _read(HOST_CPP)
    assert "O3DESharp_GetManagedExports" in body, (
        "the whole ABI is exchanged through one exported symbol."
    )
    # The five thunks come back inside the struct; resolving them individually
    # would reintroduce name-based lookup for no reason.
    for thunk in ("O3DESharp_CreateInstance", "O3DESharp_InvokeLifecycle",
                  "O3DESharp_DispatchEBusEvent", "O3DESharp_DestroyInstance"):
        assert thunk not in body, (
            f"{thunk} must arrive inside ManagedExports, not be resolved by name."
        )


@pytest.mark.unit
def test_covers_both_desktop_loaders():
    body = _read(HOST_CPP)
    assert "LoadLibrary" in body and "dlopen" in body, (
        "win-x64 and linux-x64 are both in M4 scope."
    )
    assert "GetProcAddress" in body and "dlsym" in body


@pytest.mark.unit
def test_rejects_an_abi_version_mismatch():
    body = _read(HOST_CPP)
    assert re.search(r"exports\.version\s*!=\s*Abi::HostAbiVersion", body), (
        "a shipping image built against a different ABI version must be refused, "
        "not have its pointers reinterpreted."
    )


@pytest.mark.unit
def test_hot_reload_is_hard_false():
    body = _read(HOST_CPP)
    assert re.search(r"bool\s+NativeAotHost::SupportsHotReload\(\)\s*const\s*\{\s*return\s+false\s*;",
                     body, re.MULTILINE | re.DOTALL), (
        "hot-reload is editor-only by design; there is no AssemblyLoadContext in "
        "a NativeAOT image, so this must be an unconditional false rather than a probe."
    )


@pytest.mark.unit
def test_sources_are_in_the_build_file_list():
    files_cmake = (GEM_ROOT / "Code" / "o3desharp_private_files.cmake").read_text(encoding="utf-8")
    for entry in ("Source/Scripting/NativeAotHost.h", "Source/Scripting/NativeAotHost.cpp"):
        assert entry in files_cmake, f"{entry} is missing from o3desharp_private_files.cmake"
