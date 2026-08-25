#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Verifies what HostExportsGenerator actually emits into O3DE.Core.

The generator is the ABI adapter: it turns ManagedExportsImpl's plain methods
into [UnmanagedCallersOnly] thunks and packs their addresses into a
ManagedExports struct. Nothing else in the build fails if it emits the wrong
thing - the editor just silently loses its exports - so the emitted text is
asserted directly.

Marked `slow` because it shells out to `dotnet build`.
"""

import shutil
import subprocess
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
CORE_CSPROJ = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "O3DE.Core.csproj"
GENERATED = (
    GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "obj" / "Release" / "net9.0"
    / "generated" / "O3DESharp.SourceGenerators"
    / "O3DESharp.SourceGenerators.HostExportsGenerator"
)

EXPECTED_THUNKS = [
    "O3DESharp_CreateInstance",
    "O3DESharp_InvokeLifecycle",
    "O3DESharp_DispatchEBusEvent",
    "O3DESharp_DestroyInstance",
    "O3DESharp_HotReloadSwap",
]


def _build(host_mode):
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    result = subprocess.run(
        [
            "dotnet", "build", str(CORE_CSPROJ), "-c", "Release", "--nologo",
            "-p:EmitCompilerGeneratedFiles=true",
            f"-p:O3DESharpHostMode={host_mode}",
        ],
        capture_output=True, text=True, timeout=900,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    files = list(GENERATED.glob("*.g.cs"))
    assert files, f"HostExportsGenerator emitted nothing into {GENERATED}"
    return "\n".join(f.read_text(encoding="utf-8") for f in files)


@pytest.mark.slow
def test_coral_mode_emits_all_five_thunks_and_the_exports_getter():
    emitted = _build("Coral")
    for thunk in EXPECTED_THUNKS:
        assert f"[UnmanagedCallersOnly(EntryPoint = \"{thunk}\")]" in emitted, thunk
    assert "O3DESharp_GetManagedExports" in emitted
    assert "HostMode = \"Coral\"" in emitted


@pytest.mark.slow
def test_exports_struct_is_filled_in_the_frozen_field_order():
    emitted = _build("Coral")
    order = [
        "exports->CreateInstance",
        "exports->InvokeLifecycle",
        "exports->DispatchEBusEvent",
        "exports->DestroyInstance",
        "exports->HotReloadSwap",
    ]
    positions = [emitted.index(field) for field in order]
    assert positions == sorted(positions), (
        "ManagedExports must be filled in the frozen field order; the C++ side "
        "reads it as a struct, not by name."
    )


@pytest.mark.slow
def test_version_is_stamped_on_both_structs_before_use():
    emitted = _build("Coral")
    assert "exports->Version = HostAbi.Version" in emitted, (
        "GetManagedExports must stamp the version, or a host cannot tell a "
        "populated struct from a zeroed one."
    )
    assert "imports->Version != HostAbi.Version" in emitted, (
        "GetManagedExports must REJECT an imports struct whose version it does "
        "not recognise rather than reinterpreting its pointers."
    )


@pytest.mark.slow
def test_nativeaot_mode_emits_the_same_thunks():
    emitted = _build("NativeAot")
    for thunk in EXPECTED_THUNKS:
        assert f"[UnmanagedCallersOnly(EntryPoint = \"{thunk}\")]" in emitted, thunk
    assert "HostMode = \"NativeAot\"" in emitted
