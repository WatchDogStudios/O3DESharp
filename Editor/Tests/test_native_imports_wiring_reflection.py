#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Regression guard: NativeImportsWiring.Apply must wire every
ReflectionInternalCalls field (the 20 added to the ABI in HostAbiVersion 2),
not just the original 47 O3DE.InternalCalls fields. See
docs/superpowers/specs/2026-08-25-nativeaot-reflection-dispatch-design.md.
"""

import re
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
WIRING_CS = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "Interop" / "NativeImportsWiring.cs"
REFLECTION_CS = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "Reflection" / "NativeReflection.cs"


def _reflection_fields():
    text = REFLECTION_CS.read_text(encoding="utf-8")
    return re.findall(r"internal\s+static\s+delegate\*\s+unmanaged<[^>]*>\s+(\w+)\s*;", text)


@pytest.mark.unit
def test_every_reflection_internal_calls_field_is_wired():
    fields = _reflection_fields()
    assert len(fields) == 20, f"expected 20 ReflectionInternalCalls fields, found {len(fields)}"

    wiring = WIRING_CS.read_text(encoding="utf-8")
    for field in fields:
        assert f"ReflectionInternalCalls.{field} = " in wiring, (
            f"NativeImportsWiring.Apply does not assign ReflectionInternalCalls.{field} - "
            "under NativeAOT this stays a null function pointer forever."
        )


@pytest.mark.unit
def test_reflection_wiring_reads_from_the_matching_imports_field():
    wiring = WIRING_CS.read_text(encoding="utf-8")
    for field in _reflection_fields():
        assert f"(void*)imports.{field};" in wiring, (
            f"ReflectionInternalCalls.{field} must be cast from imports.{field}, "
            "the matching NativeImports field - not some other field."
        )
