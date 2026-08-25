#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""NativeImportsWiring.Apply is the only thing that ever populates
O3DE.InternalCalls' function pointers under NativeAOT - Coral is the only
other populator (AddInternalCall/UploadInternalCalls), and there is no Coral
in a NativeAOT image.

A missed field stays null forever and the first call through it is an
access violation, not a catchable exception. A wrong CAST TYPE compiles
(delegate* unmanaged<...> casts are unchecked) but corrupts the call's
calling convention/argument shape at the call site - also memory-unsafe,
also with no compile-time signal.

These parse InternalCalls.cs (the real declared type per field) and
NativeImportsWiring.cs (the cast type used per assignment) and assert every
one of the 47 fields is assigned, with the assigned type matching the
declared type exactly - not just "some cast to something".
"""

import re
import shutil
import subprocess
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
INTERNAL_CALLS_CS = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "InternalCalls.cs"
WIRING_CS = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "Interop" / "NativeImportsWiring.cs"
CORE_CSPROJ = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "O3DE.Core.csproj"


def _read(path):
    assert path.is_file(), f"{path} is missing."
    return path.read_text(encoding="utf-8")


def _declared_fields():
    """(name -> delegate* unmanaged<...> type text) from InternalCalls.cs."""
    text = _read(INTERNAL_CALLS_CS)
    pattern = re.compile(r"internal\s+static\s+(delegate\*\s+unmanaged<[^>]*>)\s+(\w+)\s*;")
    return {name: sig for sig, name in pattern.findall(text)}


def _wired_assignments():
    """(name -> cast type text) from NativeImportsWiring.cs's Apply body."""
    text = _read(WIRING_CS)
    pattern = re.compile(
        r"InternalCalls\.(\w+)\s*=\s*\((delegate\*\s+unmanaged<[^>]*>)\)\(void\*\)imports\.\1\s*;"
    )
    return {name: cast for name, cast in pattern.findall(text)}


@pytest.mark.unit
def test_wiring_file_is_guarded_the_same_way_as_hotreloadmanager():
    text = _read(WIRING_CS)
    assert re.search(r"^#if\s+O3DE_HOST_NATIVEAOT\s*$", text, re.MULTILINE), (
        "NativeImportsWiring.cs must open with '#if O3DE_HOST_NATIVEAOT' - it references "
        "Coral.Managed.Interop types that make no sense (and may not even exist) outside a "
        "NativeAOT build, same reasoning as HotReloadManager.cs's Coral-mode-only guard in reverse."
    )
    assert text.rstrip().endswith("#endif") or "#endif // O3DE_HOST_NATIVEAOT" in text


@pytest.mark.unit
def test_every_internal_calls_field_is_wired():
    declared = _declared_fields()
    assert len(declared) == 47, (
        f"InternalCalls.cs declares {len(declared)} fields, not 47 - if this is a deliberate "
        "ABI change, NativeImportsWiring.cs must be updated to match and this constant bumped."
    )

    wired = _wired_assignments()
    missing = sorted(set(declared) - set(wired))
    assert not missing, (
        f"NativeImportsWiring.Apply never assigns: {missing}. A missed field stays a null "
        "unmanaged function pointer forever in a NativeAOT image - the first call through it "
        "is an access violation, not a catchable exception."
    )


@pytest.mark.unit
def test_every_wired_cast_matches_the_declared_type_exactly():
    declared = _declared_fields()
    wired = _wired_assignments()

    def normalize(sig):
        return re.sub(r"\s+", " ", sig).strip()

    mismatches = []
    for name, cast in wired.items():
        if name not in declared:
            continue
        if normalize(cast) != normalize(declared[name]):
            mismatches.append((name, declared[name], cast))

    assert not mismatches, (
        "NativeImportsWiring.Apply casts to the wrong type for these fields (declared vs cast): "
        + "; ".join(f"{n}: '{d}' vs '{c}'" for n, d, c in mismatches)
        + ". An unchecked delegate* unmanaged<...> cast to the wrong signature compiles clean "
        "and corrupts the call's argument shape at the call site with no diagnostic."
    )


@pytest.mark.unit
def test_no_extra_fields_wired_that_do_not_exist_on_internal_calls():
    declared = _declared_fields()
    wired = _wired_assignments()
    extra = sorted(set(wired) - set(declared))
    assert not extra, f"NativeImportsWiring.Apply assigns fields InternalCalls.cs does not declare: {extra}"


@pytest.mark.unit
def test_generator_calls_apply_only_under_the_nativeaot_symbol():
    generator = _read(GEM_ROOT / "Code" / "Tools" / "SourceGenerators" / "HostExportsGenerator.cs")
    assert "NativeImportsWiring.Apply" in generator, (
        "HostExportsGenerator must emit a call to NativeImportsWiring.Apply, or the wiring "
        "code exists but is never invoked."
    )
    # The call must sit inside an #if O3DE_HOST_NATIVEAOT block in the EMITTED text (a string
    # inside AppendLine calls), since ManagedExportsThunks itself is generated identically for
    # both host modes - referencing NativeImportsWiring unconditionally would fail to compile
    # under Coral, where that type does not exist.
    assert re.search(
        r'AppendLine\("#if O3DE_HOST_NATIVEAOT"\).*?'
        r'AppendLine\("\s*NativeImportsWiring\.Apply.*?"\).*?'
        r'AppendLine\("#endif"\)',
        generator,
        re.DOTALL,
    ), "the NativeImportsWiring.Apply call must be emitted between #if O3DE_HOST_NATIVEAOT / #endif."


def _build(*extra):
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    return subprocess.run(
        ["dotnet", "build", str(CORE_CSPROJ), "-c", "Release", "--nologo", "-t:Rebuild", *extra],
        capture_output=True, text=True, timeout=900,
    )


@pytest.mark.slow
def test_nativeaot_mode_compiles_the_wiring_with_zero_errors():
    # The real compiler check: delegate* unmanaged<...> casts are unsafe and unchecked at the
    # C# language level, so "it compiles" does not prove the types are right (that's the two
    # source-level tests above) - but it does prove every field name/type used here is real,
    # Coral.Managed.Interop resolves, and the #if wiring reaches a valid call site.
    result = _build("-p:O3DESharpHostMode=NativeAot")
    assert result.returncode == 0, result.stdout + result.stderr
    assert re.search(r"\berror CS\d+\b", result.stdout, re.IGNORECASE) is None, result.stdout


@pytest.mark.slow
def test_coral_mode_is_unaffected():
    # NativeImportsWiring.cs does not exist as a type under Coral mode (whole file guarded
    # out); the generator's #if-wrapped call must compile to nothing, not a missing-type error.
    result = _build()
    assert result.returncode == 0, result.stdout + result.stderr
    assert "NativeImportsWiring" not in result.stdout
