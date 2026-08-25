#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""StaticDispatchGenerator turns reflection_data.json into a compile-time table.

The shipping build has to answer 'is this (bus, event) real, and what shape are
its arguments?' with no managed-side reflection. The table is that answer. It
consumes the EXISTING reflection_data.json - the dump the reflection binding
backend already produces - not SP-1b's separate native_bindings.json, which is
an orthogonal track.

Marked `slow` because it shells out to `dotnet build`.
"""

import json
import re
import shutil
import subprocess
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
CORE_CSPROJ = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "O3DE.Core.csproj"
GENERATED = (
    GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "obj" / "Release" / "net9.0"
    / "generated" / "O3DESharp.SourceGenerators"
    / "O3DESharp.SourceGenerators.StaticDispatchGenerator"
)

FIXTURE = {
    "classes": [],
    "global_methods": [],
    "global_properties": [],
    "ebuses": [
        {
            "name": "TickBus",
            "address_type": {"marshal_type": "Void"},
            "events": [
                {
                    "name": "OnTick",
                    "bus_name": "TickBus",
                    "is_broadcast": True,
                    "return_type": {"marshal_type": "Void"},
                    "parameters": [
                        {"name": "deltaTime", "marshal_type": "Float"},
                        {"name": "timePoint", "marshal_type": "Double"},
                    ],
                }
            ],
        },
        {
            "name": "TransformBus",
            "address_type": {"marshal_type": "EntityId"},
            "events": [
                {
                    "name": "GetWorldTranslation",
                    "bus_name": "TransformBus",
                    "is_broadcast": False,
                    "return_type": {"marshal_type": "Vector3"},
                    "parameters": [],
                }
            ],
        },
    ],
}


def _build(tmp_path):
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    data = tmp_path / "reflection_data.json"
    data.write_text(json.dumps(FIXTURE), encoding="utf-8")
    result = subprocess.run(
        [
            "dotnet", "build", str(CORE_CSPROJ), "-c", "Release", "--nologo", "-t:Rebuild",
            "-p:EmitCompilerGeneratedFiles=true",
            f"-p:O3DESharpReflectionData={data}",
        ],
        capture_output=True, text=True, timeout=900,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    files = list(GENERATED.glob("*.g.cs"))
    assert files, f"StaticDispatchGenerator emitted nothing into {GENERATED}"
    return "\n".join(f.read_text(encoding="utf-8") for f in files)


@pytest.mark.slow
def test_emits_one_entry_per_reflected_event(tmp_path):
    emitted = _build(tmp_path)
    assert '"TickBus\\u0000OnTick"' in emitted or '"TickBus\\0OnTick"' in emitted or \
           ('case "TickBus"' in emitted and '"OnTick"' in emitted)
    assert "TransformBus" in emitted and "GetWorldTranslation" in emitted
    assert "EntryCount => 2" in emitted


@pytest.mark.slow
def test_records_arity_and_broadcast_flag(tmp_path):
    emitted = _build(tmp_path)
    # OnTick takes two parameters and is a broadcast; GetWorldTranslation takes
    # none and is addressed. Both facts are what the runtime routing needs in
    # order to reject a malformed call without reflecting.
    assert re.search(r"OnTick.*?arity\s*=\s*2", emitted, re.DOTALL)
    assert re.search(r"OnTick.*?isBroadcast\s*=\s*true", emitted, re.DOTALL)
    assert re.search(r"GetWorldTranslation.*?arity\s*=\s*0", emitted, re.DOTALL)
    assert re.search(r"GetWorldTranslation.*?isBroadcast\s*=\s*false", emitted, re.DOTALL)


@pytest.mark.slow
def test_lookup_is_a_switch_not_a_dictionary(tmp_path):
    emitted = _build(tmp_path)
    assert "switch (busName)" in emitted, (
        "a generated switch is resolved at compile time; a Dictionary would be "
        "runtime state that has to be built during startup."
    )


@pytest.mark.slow
def test_missing_reflection_data_emits_an_empty_but_valid_table(tmp_path):
    # A fresh clone has no reflection_data.json. The build must still succeed -
    # the table is simply empty and every dispatch falls to the diagnostic.
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    result = subprocess.run(
        [
            "dotnet", "build", str(CORE_CSPROJ), "-c", "Release", "--nologo", "-t:Rebuild",
            "-p:EmitCompilerGeneratedFiles=true",
            f"-p:O3DESharpReflectionData={tmp_path / 'does-not-exist.json'}",
        ],
        capture_output=True, text=True, timeout=900,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    emitted = "\n".join(f.read_text(encoding="utf-8") for f in GENERATED.glob("*.g.cs"))
    assert "EntryCount => 0" in emitted


@pytest.mark.slow
def test_malformed_reflection_data_does_not_break_the_build(tmp_path):
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    bad = tmp_path / "reflection_data.json"
    bad.write_text("{ not json", encoding="utf-8")
    result = subprocess.run(
        [
            "dotnet", "build", str(CORE_CSPROJ), "-c", "Release", "--nologo", "-t:Rebuild",
            f"-p:O3DESharpReflectionData={bad}",
        ],
        capture_output=True, text=True, timeout=900,
    )
    assert result.returncode == 0, (
        "a corrupt reflection dump must degrade to an empty table, not take the "
        "build down: " + result.stdout + result.stderr
    )


@pytest.mark.slow
def test_non_object_array_elements_degrade_to_an_empty_table(tmp_path):
    # Syntactically valid JSON, structurally wrong: an "ebuses" array whose
    # elements aren't objects. JsonElement.TryGetProperty throws
    # InvalidOperationException on a non-object element - this must not
    # escape uncaught (which would make Roslyn drop the whole generated
    # file, silently deleting StaticEBusDispatch from the compilation).
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    data = tmp_path / "reflection_data.json"
    data.write_text(json.dumps({"ebuses": ["not_an_object", 123, None]}), encoding="utf-8")
    result = subprocess.run(
        [
            "dotnet", "build", str(CORE_CSPROJ), "-c", "Release", "--nologo", "-t:Rebuild",
            "-p:EmitCompilerGeneratedFiles=true",
            f"-p:O3DESharpReflectionData={data}",
        ],
        capture_output=True, text=True, timeout=900,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    files = list(GENERATED.glob("*.g.cs"))
    assert files, f"StaticDispatchGenerator emitted nothing into {GENERATED}"
    emitted = "\n".join(f.read_text(encoding="utf-8") for f in files)
    assert "internal static class StaticEBusDispatch" in emitted
    assert "EntryCount => 0" in emitted


@pytest.mark.slow
def test_names_with_illegal_string_literal_characters_are_escaped(tmp_path):
    # A bus/event name containing a raw newline is illegal unescaped inside
    # a non-verbatim C# string literal (CS1010/CS1003). Literal() must
    # escape it rather than splicing the raw character into generated source.
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    fixture = {
        "ebuses": [
            {
                "name": "Weird\nBus",
                "events": [
                    {"name": "Odd\nEvent", "is_broadcast": True, "parameters": []},
                ],
            },
        ],
    }
    data = tmp_path / "reflection_data.json"
    data.write_text(json.dumps(fixture), encoding="utf-8")
    result = subprocess.run(
        [
            "dotnet", "build", str(CORE_CSPROJ), "-c", "Release", "--nologo", "-t:Rebuild",
            "-p:EmitCompilerGeneratedFiles=true",
            f"-p:O3DESharpReflectionData={data}",
        ],
        capture_output=True, text=True, timeout=900,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    files = list(GENERATED.glob("*.g.cs"))
    assert files, f"StaticDispatchGenerator emitted nothing into {GENERATED}"
    emitted = "\n".join(f.read_text(encoding="utf-8") for f in files)
    assert r"Weird\nBus" in emitted
    assert r"Odd\nEvent" in emitted
    # The raw newline must never appear inside the quoted literal - that's
    # exactly what would break the build.
    assert "\"Weird\nBus\"" not in emitted
