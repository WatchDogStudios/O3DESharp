#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Guards the marshal_type contract between the C++ exporter and the C# generator.

The C++ exporter writes a `marshal_type` tag per parameter
(ReflectionDataExporter::MarshalTypeToString). The reflection binding generator
maps that tag to a C# type (ReflectionBindingGenerator.MapMarshalToCSharp).

The mapper's fallback arm is `_ => "object"`. So if the two sides ever disagree
- a renamed tag, a new C++ enum value, a casing change - nothing errors. Every
affected binding silently degrades to `object`: no type safety, no IntelliSense,
callers casting everything, and no signal that it happened. That failure mode is
invisible in both the generator's output and its exit code, which is precisely
why it needs a test rather than review.

Verified 2026-07-20: 19 C++ tags, all mapped. The C# side additionally handles
Vector2/Vector4/Color, which the C++ classifier does not currently produce -
those are a coverage gap in the C++ enum, not a mismatch, so this test does not
require the reverse direction to hold.
"""

import re
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
CPP = REPO / "Code" / "Source" / "Scripting" / "Reflection" / "ReflectionDataExporter.cpp"
CSHARP = (REPO / "Code" / "Tools" / "BindingGenerator" / "O3DESharp.BindingGenerator"
          / "Generation" / "ReflectionBindingGenerator.cs")


def _cpp_marshal_tags() -> set:
    """Every string MarshalTypeToString can return."""
    text = CPP.read_text(encoding="utf-8")
    # Anchor on the definition, not the call site at ~line 442.
    start = text.index("ReflectionDataExporter::MarshalTypeToString(")
    # Stop at the next member-function definition so we don't absorb its returns.
    nxt = text.find("ReflectionDataExporter::", start + 1)
    body = text[start:nxt if nxt != -1 else len(text)]
    return set(re.findall(r'return\s+"([A-Za-z0-9_]+)"', body))


def _csharp_mapped_tags() -> set:
    """Every explicit case label in MapMarshalToCSharp (excludes the `_` fallback)."""
    text = CSHARP.read_text(encoding="utf-8")
    # Anchor on the definition - "MapMarshalToCSharp" also appears at call
    # sites earlier in the file, whose window would not contain the switch.
    start = text.index("private static string MapMarshalToCSharp(")
    body = text[start:start + 4000]
    switch = body.index("switch")
    end = body.index("};", switch)
    return set(re.findall(r'"([A-Za-z0-9_]+)"\s*=>', body[switch:end]))


@pytest.mark.unit
def test_both_sources_are_present():
    assert CPP.is_file(), f"missing {CPP}"
    assert CSHARP.is_file(), f"missing {CSHARP}"


@pytest.mark.unit
def test_extraction_actually_found_tags():
    # A regex that silently matches nothing would make the parity assert vacuous.
    assert len(_cpp_marshal_tags()) >= 15
    assert len(_csharp_mapped_tags()) >= 15


@pytest.mark.unit
def test_every_cpp_marshal_tag_is_mapped_in_csharp():
    cpp = _cpp_marshal_tags()
    csharp = _csharp_mapped_tags()
    unmapped = sorted(cpp - csharp)
    assert not unmapped, (
        f"C++ emits marshal_type tag(s) {unmapped} that MapMarshalToCSharp has no case for. "
        "They will hit the `_ => \"object\"` fallback and every affected binding will "
        "silently lose its type. Add the case(s) to ReflectionBindingGenerator.MapMarshalToCSharp."
    )


@pytest.mark.unit
def test_tags_are_pascal_case_on_both_sides():
    # The mapper's switch is ordinal/case-sensitive. A casing drift on either
    # side is the same silent-`object` failure as a missing case.
    for tag in _cpp_marshal_tags() | _csharp_mapped_tags():
        assert tag[0].isupper(), f"marshal tag {tag!r} is not PascalCase"
