#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Golden contract test for the frozen C++ <-> managed ABI.

NativeImports and ManagedExports are declared three times: in C#
(Assets/Scripts/O3DE.Core/Interop/HostAbi.cs), in C++
(Code/Source/Scripting/HostAbi.h), and implicitly by O3DE.InternalCalls,
whose field order NativeImports v1 mirrors. Nothing in either toolchain
fails when they drift - a field inserted on one side just reinterprets
every pointer after it, which is memory corruption at runtime.

This test parses all three and asserts they agree in name AND order.
"""

import re
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
INTERNAL_CALLS_CS = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "InternalCalls.cs"
HOST_ABI_CS = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "Interop" / "HostAbi.cs"
HOST_ABI_H = GEM_ROOT / "Code" / "Source" / "Scripting" / "HostAbi.h"

MANAGED_EXPORT_ORDER = [
    "CreateInstance",
    "InvokeLifecycle",
    "DispatchEBusEvent",
    "DestroyInstance",
    "HotReloadSwap",
]


def _read(path):
    assert path.is_file(), f"{path} is missing; the ABI is declared in three places and all three must exist."
    return path.read_text(encoding="utf-8")


def _extract_block(text, header_pattern, source_name):
    """Return the text between the first '{' after header_pattern and its
    matching '}'. Brace-counting rather than a regex because the structs
    contain no nested braces but do contain doc-comments with braces."""
    m = re.search(header_pattern, text)
    assert m, f"could not find {header_pattern!r} in {source_name}"
    start = text.index("{", m.end())
    depth = 0
    for i in range(start, len(text)):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return text[start + 1 : i]
    raise AssertionError(f"unterminated struct body for {header_pattern!r} in {source_name}")


def _csharp_internal_call_fields():
    text = _read(INTERNAL_CALLS_CS)
    return re.findall(r"internal\s+static\s+delegate\*\s+unmanaged<[^>]*>\s+(\w+)\s*;", text)


def _csharp_struct_fields(struct_name):
    body = _extract_block(_read(HOST_ABI_CS), rf"struct\s+{struct_name}\b", str(HOST_ABI_CS))
    return re.findall(r"public\s+IntPtr\s+(\w+)\s*;", body)


def _cpp_struct_fields(struct_name):
    body = _extract_block(_read(HOST_ABI_H), rf"struct\s+{struct_name}\b", str(HOST_ABI_H))
    return re.findall(r"void\*\s+(\w+)\s*;", body)


@pytest.mark.unit
def test_native_imports_mirrors_internal_calls_exactly():
    internal_calls = _csharp_internal_call_fields()
    assert len(internal_calls) == 47, (
        f"O3DE.InternalCalls declares {len(internal_calls)} function pointers, not 47. "
        "Adding one is an ABI change: mirror it in BOTH HostAbi.cs and HostAbi.h and "
        "bump HostAbi.Version / HostAbiVersion together."
    )
    assert _csharp_struct_fields("NativeImports") == internal_calls, (
        "NativeImports (C#) must mirror O3DE.InternalCalls field-for-field, in order."
    )


@pytest.mark.unit
def test_native_imports_cpp_matches_csharp():
    assert _cpp_struct_fields("NativeImports") == _csharp_struct_fields("NativeImports"), (
        "Code/Source/Scripting/HostAbi.h and Interop/HostAbi.cs disagree on NativeImports. "
        "Field ORDER is the ABI - a mismatch reinterprets every pointer after the first "
        "divergence, and neither compiler can see it."
    )


@pytest.mark.unit
def test_managed_exports_is_the_frozen_five_in_both_languages():
    assert _csharp_struct_fields("ManagedExports") == MANAGED_EXPORT_ORDER
    assert _cpp_struct_fields("ManagedExports") == MANAGED_EXPORT_ORDER


@pytest.mark.unit
def test_version_constants_agree():
    cs = re.search(r"public\s+const\s+uint\s+Version\s*=\s*(\d+)\s*;", _read(HOST_ABI_CS))
    cpp = re.search(r"HostAbiVersion\s*=\s*(\d+)\s*;", _read(HOST_ABI_H))
    assert cs, "HostAbi.cs must declare 'public const uint Version = N;'"
    assert cpp, "HostAbi.h must declare 'HostAbiVersion = N;'"
    assert cs.group(1) == cpp.group(1), (
        f"ABI version mismatch: C# says {cs.group(1)}, C++ says {cpp.group(1)}. "
        "Host, editor build and shipping build must agree on one number."
    )


@pytest.mark.unit
def test_cpp_header_is_in_the_build_file_list():
    files_cmake = GEM_ROOT / "Code" / "o3desharp_private_files.cmake"
    assert "Source/Scripting/HostAbi.h" in files_cmake.read_text(encoding="utf-8"), (
        "HostAbi.h must be listed in o3desharp_private_files.cmake or it is invisible "
        "to the IDE and to any generated-file tooling that walks that list."
    )
