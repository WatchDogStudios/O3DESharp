#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Guards the O3DESharpHostMode build-mode switch.

Coral (CoreCLR hosting) and NativeAOT are mutually exclusive per build
artifact - a NativeAOT image has no JIT and no hostfxr consumer. The two
artifacts come from one codebase, selected by one MSBuild property. These
tests pin that property's contract so a later edit cannot quietly define
both symbols, neither, or default to the shipping mode.
"""

import re
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
CORE_CSPROJ = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "O3DE.Core.csproj"


def _text():
    return CORE_CSPROJ.read_text(encoding="utf-8")


@pytest.mark.unit
def test_host_mode_defaults_to_coral():
    text = _text()
    m = re.search(
        r"<O3DESharpHostMode\s+Condition=\"'\$\(O3DESharpHostMode\)'\s*==\s*''\">(\w+)</O3DESharpHostMode>",
        text,
    )
    assert m, "O3DE.Core.csproj must default O3DESharpHostMode when it is unset."
    assert m.group(1) == "Coral", (
        "The default MUST be Coral. Defaulting to NativeAot would silently strip "
        "hot-reload out of every editor build."
    )


@pytest.mark.unit
def test_exactly_one_host_symbol_is_defined_per_mode():
    text = _text()
    assert "O3DE_HOST_CORAL" in text and "O3DE_HOST_NATIVEAOT" in text
    coral = re.search(
        r"Condition=\"'\$\(O3DESharpHostMode\)'\s*==\s*'NativeAot'\"[^>]*>\s*"
        r"<DefineConstants>\$\(DefineConstants\);O3DE_HOST_NATIVEAOT</DefineConstants>",
        text,
    )
    other = re.search(
        r"Condition=\"'\$\(O3DESharpHostMode\)'\s*!=\s*'NativeAot'\"[^>]*>\s*"
        r"<DefineConstants>\$\(DefineConstants\);O3DE_HOST_CORAL</DefineConstants>",
        text,
    )
    assert coral, "NativeAot mode must define O3DE_HOST_NATIVEAOT (and only it)."
    assert other, "Every non-NativeAot mode must define O3DE_HOST_CORAL (and only it)."


@pytest.mark.unit
def test_generator_can_see_both_properties():
    text = _text()
    for prop in ("O3DESharpHostMode", "O3DESharpEmitHostExports"):
        assert f'<CompilerVisibleProperty Include="{prop}" />' in text, (
            f"{prop} must be a CompilerVisibleProperty or the source generators "
            f"cannot read it from AnalyzerConfigOptions."
        )


@pytest.mark.unit
def test_only_o3de_core_emits_the_host_exports():
    # ManagedExports is a single well-known type. If every consumer assembly
    # emitted its own copy, the name would resolve ambiguously the moment two
    # of them were referenced together.
    assert "<O3DESharpEmitHostExports>true</O3DESharpEmitHostExports>" in _text()
    for other in (
        GEM_ROOT / "Assets" / "Scripts" / "O3DESharp" / "O3DESharp.csproj",
        GEM_ROOT / "Code" / "Tools" / "SourceGenerators.Tests" / "SourceGenerators.Smoke.csproj",
    ):
        assert "O3DESharpEmitHostExports" not in other.read_text(encoding="utf-8"), (
            f"{other.name} must not opt into emitting host exports; only O3DE.Core does."
        )
