# O3DESharp Linux Parity (M0 + M1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make O3DESharp configure, build, and run its C# scripting workflow on Linux with full editor + runtime parity, by closing the concrete Linux gaps found in the audit (missing CMake stub, MSVC-only managed build, Windows-bound Python editor tooling).

**Architecture:** Small, independent fixes grouped as M0 (unblock a clean Linux configure) then M1 (build parity + portable editor tooling). A new stdlib-only Python helper module (`csharp_platform_utils.py`) centralizes cross-platform `dotnet` resolution, default-app opening, and VS Code launch.json rendering so the fixes are unit-testable without the O3DE editor. CMake gains a non-MSVC managed-build path mirroring the existing binding-generator `dotnet` build mechanism. This is milestone M0+M1 of the combined Linux + dual-mode AOT spec (`docs/superpowers/specs/2026-07-02-linux-aot-support-design.md`); later milestones (self-contained deploy, the frozen ABI seam, NativeAOT, Mono) are out of scope here.

**Tech Stack:** CMake (O3DE gem build), Python 3 (O3DE editor tooling, pytest), C# / .NET 9 (`dotnet` CLI), PySide2/Qt (editor UI — not touched by this plan except via already-portable code).

---

## Environment note (read before starting)

This plan is authored on Windows without an O3DE engine checkout. Tasks split into two verifiability classes:

- **Verifiable here (pytest / `ast.parse` / file checks):** every Python task (Tasks 3–9) and the CMake-parity guard test (Task 1). Run these and confirm green before committing.
- **Verifiable only on Linux with a full O3DE engine (CMake configure/build + editor run):** the CMake managed-build change (Task 10), the Coral fork check (Task 11), and the end-to-end run (Task 14). Each such task states the exact command and expected output to run in that environment, and — where a Windows regression is possible — a Windows re-check. **Do not mark these complete from this machine; mark them "implemented, pending Linux verification" and record the Linux result when available.**

`dotnet`, `python`, and `pytest` are available on this machine for the here-verifiable tasks. Run Python tests with:
```bash
python -m pytest Editor/Tests -q
```

## File structure

**New files:**
- `Code/Platform/Linux/o3desharp_shared_files.cmake` — empty `set(FILES)` stub (M0 blocker).
- `Editor/Scripts/csharp_platform_utils.py` — stdlib-only helpers: `resolve_dotnet()`, `open_in_default_app()`, `render_vscode_launch_json()`. No `azlmbr`/PySide2 imports, so it imports cleanly in tests.
- `Editor/Tests/test_platform_cmake_parity.py` — asserts every referenced `Code/Platform/*` dir has the `o3desharp_shared_files.cmake` stub and PAL traits match `gem.json`.
- `Editor/Tests/test_csharp_platform_utils.py` — unit tests for the new helpers.
- `Editor/Tests/test_dotnet_resolution_wired.py` — asserts no bare `["dotnet", ...]` subprocess lists remain.

**Modified files:**
- `Code/Platform/{Mac,Android,iOS}/PAL_*.cmake` — honest support traits.
- `Code/CMakeLists.txt` — non-MSVC O3DE.Core build path.
- `Editor/Scripts/csharp_binding_generator.py`, `generate_bindings.py`, `csharp_editor_tools.py`, `csharp_project_manager.py`, `csharp_editor_bootstrap.py` — use the new helpers; fix stale strings; Linux Rider paths.
- `.teamcity/settings.kts` — run the Python suite on Linux CI.
- `README.md` — Linux build/run instructions + honest platform table.

---

## M0 — Unblock a clean Linux configure

### Task 1: Add the missing Linux `o3desharp_shared_files.cmake` stub

**Files:**
- Create: `Code/Platform/Linux/o3desharp_shared_files.cmake`
- Test: `Editor/Tests/test_platform_cmake_parity.py`

`Code/CMakeLists.txt:481-482` unconditionally `include()`s `${pal_dir}/o3desharp_shared_files.cmake`. Windows/Mac/Android/iOS each ship an empty stub; **Linux does not**, so a Linux configure fails with a fatal "include could not find requested file" before anything else runs. Every platform's stub is just a header comment plus an empty `set(FILES)`.

- [ ] **Step 1: Write the failing parity test**

Create `Editor/Tests/test_platform_cmake_parity.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Guards that the per-platform CMake file set stays consistent.

Code/CMakeLists.txt includes ${pal_dir}/o3desharp_shared_files.cmake
unconditionally, so every supported platform directory must ship that
stub or a configure on that platform dies with a fatal include error.
"""

import re
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).parent.parent.parent
PLATFORM_DIR = GEM_ROOT / "Code" / "Platform"

# Platforms whose PAL file Code/CMakeLists.txt will include(). Each must
# carry the shared_files stub because the include is unconditional.
PLATFORMS = ["Windows", "Linux", "Mac", "Android", "iOS"]


@pytest.mark.unit
@pytest.mark.parametrize("platform", PLATFORMS)
def test_platform_has_shared_files_stub(platform):
    stub = PLATFORM_DIR / platform / "o3desharp_shared_files.cmake"
    assert stub.is_file(), (
        f"{stub} is missing; Code/CMakeLists.txt include()s it unconditionally, "
        f"so a {platform} configure would fail with a fatal include error."
    )
    text = stub.read_text(encoding="utf-8")
    assert re.search(r"set\s*\(\s*FILES", text), (
        f"{stub} must define a FILES list (even if empty) to match the other platforms."
    )
```

- [ ] **Step 2: Run it to verify Linux fails**

Run: `python -m pytest Editor/Tests/test_platform_cmake_parity.py -q`
Expected: FAIL on `test_platform_has_shared_files_stub[Linux]` ("is missing"); the other four PASS.

- [ ] **Step 3: Create the Linux stub**

Create `Code/Platform/Linux/o3desharp_shared_files.cmake`:

```cmake
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
#

# Platform specific shared (module) files for Linux.
# No Linux-only C++ sources today; the list is intentionally empty so the
# unconditional include() in Code/CMakeLists.txt succeeds, matching the
# Windows/Mac/Android/iOS stubs.

set(FILES
)
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `python -m pytest Editor/Tests/test_platform_cmake_parity.py -q`
Expected: PASS (all 5 platforms).

- [ ] **Step 5: (Linux, deferred) configure smoke**

On a Linux O3DE engine with this gem enabled:
Run: `cmake --preset linux <engine-configure-args>` (project's standard Linux configure)
Expected: no `include could not find requested file: ".../Platform/Linux/o3desharp_shared_files.cmake"` error. (Record result; do not block the commit on this — the parity test is the here-verifiable guard.)

- [ ] **Step 6: Commit**

```bash
git add Code/Platform/Linux/o3desharp_shared_files.cmake Editor/Tests/test_platform_cmake_parity.py
git commit -m "Linux: add missing o3desharp_shared_files.cmake stub (fatal configure blocker)"
```

### Task 2: Make the platform support matrix honest

**Files:**
- Modify: `Code/Platform/Mac/PAL_mac.cmake:9`, `Code/Platform/Android/PAL_android.cmake:9`, `Code/Platform/iOS/PAL_ios.cmake:9`
- Test: `Editor/Tests/test_platform_cmake_parity.py` (extend)

All five PAL files set `PAL_TRAIT_O3DESHARP_SUPPORTED TRUE`, but `gem.json` declares only `Linux` + `Windows`, and the support gate (`Code/CMakeLists.txt:24-26`) only early-returns when the trait is FALSE — so on Mac/Android/iOS the gem would attempt a full (desktop-only) Coral build that cannot succeed. Make the traits match the true M0/M1 target set (Windows + Linux). Mac re-enables in the desktop-AOT milestone (with testing + a `gem.json` platform add); Android/iOS re-enable in the Mono milestone (M5).

> **Maintainer note for review:** this sets Mac/Android/iOS to FALSE to match the current `gem.json`. If you intend to *claim* Mac desktop now, keep `Mac` TRUE and add "Mac" to `gem.json` platforms instead — adjust this task to your intended target set.

- [ ] **Step 1: Extend the parity test to assert trait honesty**

Add to `Editor/Tests/test_platform_cmake_parity.py`:

```python
import json


def _pal_supported(platform):
    pal = PLATFORM_DIR / platform / f"PAL_{platform.lower()}.cmake"
    text = pal.read_text(encoding="utf-8")
    m = re.search(r"set\s*\(\s*PAL_TRAIT_O3DESHARP_SUPPORTED\s+(TRUE|FALSE)\s*\)", text)
    assert m, f"{pal} must set PAL_TRAIT_O3DESHARP_SUPPORTED explicitly."
    return m.group(1) == "TRUE"


@pytest.mark.unit
def test_pal_support_traits_match_gem_json():
    gem = json.loads((GEM_ROOT / "gem.json").read_text(encoding="utf-8"))
    declared = set(gem.get("platforms", []))
    # gem.json currently declares the honest supported set.
    assert _pal_supported("Windows") is True
    assert _pal_supported("Linux") is True
    assert _pal_supported("Mac") is ("Mac" in declared)
    assert _pal_supported("Android") is ("Android" in declared)
    assert _pal_supported("iOS") is ("iOS" in declared)
```

- [ ] **Step 2: Run it to verify Mac/Android/iOS fail**

Run: `python -m pytest Editor/Tests/test_platform_cmake_parity.py::test_pal_support_traits_match_gem_json -q`
Expected: FAIL (Mac/Android/iOS are TRUE but not in `gem.json.platforms`).

- [ ] **Step 3: Set the three traits to FALSE**

In `Code/Platform/Mac/PAL_mac.cmake`, `Code/Platform/Android/PAL_android.cmake`, and `Code/Platform/iOS/PAL_ios.cmake`, change line 9 from:

```cmake
set(PAL_TRAIT_O3DESHARP_SUPPORTED TRUE)
```

to (Mac variant shown — use the matching platform word in the comment for Android/iOS):

```cmake
# Not yet a claimed target (see gem.json). Coral hosts desktop CoreCLR, so
# Mac desktop is re-enabled in the desktop-AOT milestone once tested; Android/iOS
# come with the Mono milestone. Keeping this FALSE makes the support gate in
# Code/CMakeLists.txt cleanly skip the gem here instead of attempting an
# unsupported build.
set(PAL_TRAIT_O3DESHARP_SUPPORTED FALSE)
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `python -m pytest Editor/Tests/test_platform_cmake_parity.py -q`
Expected: PASS (all cases).

- [ ] **Step 5: Commit**

```bash
git add Code/Platform/Mac/PAL_mac.cmake Code/Platform/Android/PAL_android.cmake Code/Platform/iOS/PAL_ios.cmake Editor/Tests/test_platform_cmake_parity.py
git commit -m "Linux: make PAL support traits honest (Win+Linux only, per gem.json)"
```

---

## M1 — Portable editor tooling (Python; verifiable here)

### Task 3: Create `csharp_platform_utils.resolve_dotnet()`

**Files:**
- Create: `Editor/Scripts/csharp_platform_utils.py`
- Test: `Editor/Tests/test_csharp_platform_utils.py`

The editor Python invokes `dotnet` as a bare PATH name at six sites. On Linux the O3DE Editor is frequently launched without the user's login PATH (and the SDK CMake auto-installs into `<build>/.dotnet` is never on PATH), so `dotnet` isn't found and every editor-triggered build/generate fails with `FileNotFoundError`. Centralize resolution in one stdlib-only, unit-testable helper.

- [ ] **Step 1: Write the failing tests**

Create `Editor/Tests/test_csharp_platform_utils.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Unit tests for csharp_platform_utils (stdlib-only cross-platform helpers)."""

import os
from pathlib import Path

import pytest

import csharp_platform_utils as ppu


@pytest.mark.unit
def test_resolve_dotnet_prefers_env_override(monkeypatch, tmp_path):
    exe = tmp_path / "dotnet"
    exe.write_text("#!/bin/sh\n")
    monkeypatch.setenv("O3DESHARP_DOTNET_EXECUTABLE", str(exe))
    monkeypatch.setattr(ppu.shutil, "which", lambda name: "/usr/bin/dotnet")
    assert ppu.resolve_dotnet() == str(exe)


@pytest.mark.unit
def test_resolve_dotnet_falls_back_to_which(monkeypatch):
    monkeypatch.delenv("O3DESHARP_DOTNET_EXECUTABLE", raising=False)
    monkeypatch.setattr(ppu.shutil, "which", lambda name: "/opt/dotnet/dotnet")
    assert ppu.resolve_dotnet() == "/opt/dotnet/dotnet"


@pytest.mark.unit
def test_resolve_dotnet_uses_dotnet_root(monkeypatch, tmp_path):
    monkeypatch.delenv("O3DESHARP_DOTNET_EXECUTABLE", raising=False)
    monkeypatch.setattr(ppu.shutil, "which", lambda name: None)
    root = tmp_path / "root"
    root.mkdir()
    exe = root / ppu._DOTNET_EXE_NAME
    exe.write_text("x")
    monkeypatch.setenv("DOTNET_ROOT", str(root))
    assert ppu.resolve_dotnet() == str(exe)


@pytest.mark.unit
def test_resolve_dotnet_bare_fallback(monkeypatch):
    monkeypatch.delenv("O3DESHARP_DOTNET_EXECUTABLE", raising=False)
    monkeypatch.delenv("DOTNET_ROOT", raising=False)
    monkeypatch.setattr(ppu.shutil, "which", lambda name: None)
    monkeypatch.setattr(ppu, "_common_dotnet_locations", lambda: [])
    assert ppu.resolve_dotnet() == "dotnet"
```

- [ ] **Step 2: Run to verify it fails**

Run: `python -m pytest Editor/Tests/test_csharp_platform_utils.py -q`
Expected: FAIL with `ModuleNotFoundError: No module named 'csharp_platform_utils'`.

- [ ] **Step 3: Create the module with `resolve_dotnet()`**

Create `Editor/Scripts/csharp_platform_utils.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Cross-platform helpers for the O3DESharp editor tooling.

Deliberately stdlib-only (no azlmbr / PySide2) so it imports cleanly in
unit tests and anywhere in the editor. Centralizes the platform-specific
bits that were previously hardcoded for Windows.
"""

import os
import shutil
import sys
from pathlib import Path

_DOTNET_EXE_NAME = "dotnet.exe" if os.name == "nt" else "dotnet"


def _is_dotnet(path):
    """True if path points at an existing file (a usable dotnet executable)."""
    try:
        return Path(path).is_file()
    except (OSError, ValueError):
        return False


def _common_dotnet_locations():
    """Well-known dotnet install locations for the current OS, best first."""
    home = Path.home()
    if os.name == "nt":
        out = []
        for env_var in ("ProgramFiles", "ProgramW6432"):
            root = os.environ.get(env_var)
            if root:
                out.append(Path(root) / "dotnet" / _DOTNET_EXE_NAME)
        local = os.environ.get("LOCALAPPDATA")
        if local:
            out.append(Path(local) / "Microsoft" / "dotnet" / _DOTNET_EXE_NAME)
        out.append(home / ".dotnet" / _DOTNET_EXE_NAME)
        return out
    if sys.platform == "darwin":
        return [
            Path("/usr/local/share/dotnet/dotnet"),
            Path("/opt/homebrew/bin/dotnet"),
            Path("/usr/local/bin/dotnet"),
            home / ".dotnet" / "dotnet",
        ]
    # Linux / other Unix
    return [
        Path("/usr/bin/dotnet"),
        Path("/usr/local/bin/dotnet"),
        Path("/usr/share/dotnet/dotnet"),
        Path("/usr/local/share/dotnet/dotnet"),
        Path("/snap/dotnet-sdk/current/dotnet"),
        home / ".dotnet" / "dotnet",
    ]


def resolve_dotnet():
    """Return a usable `dotnet` executable path as a string.

    Resolution order (first hit wins):
      1. $O3DESHARP_DOTNET_EXECUTABLE (explicit override; CMake can export it)
      2. shutil.which("dotnet")       (on PATH - the common case)
      3. $DOTNET_ROOT/dotnet[.exe]     (standard .NET env var)
      4. Well-known per-OS install locations
      5. Bare "dotnet" (lets the subprocess raise a clear FileNotFoundError)

    On Linux the editor often runs without the user's login PATH, so
    which() alone is insufficient - hence the override + location sweep.
    """
    override = os.environ.get("O3DESHARP_DOTNET_EXECUTABLE")
    if override and _is_dotnet(override):
        return str(override)

    on_path = shutil.which("dotnet")
    if on_path:
        return on_path

    dotnet_root = os.environ.get("DOTNET_ROOT")
    if dotnet_root:
        candidate = Path(dotnet_root) / _DOTNET_EXE_NAME
        if _is_dotnet(candidate):
            return str(candidate)

    for candidate in _common_dotnet_locations():
        if _is_dotnet(candidate):
            return str(candidate)

    return "dotnet"
```

- [ ] **Step 4: Run to verify it passes**

Run: `python -m pytest Editor/Tests/test_csharp_platform_utils.py -q`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add Editor/Scripts/csharp_platform_utils.py Editor/Tests/test_csharp_platform_utils.py
git commit -m "Editor: add csharp_platform_utils.resolve_dotnet() (portable dotnet lookup)"
```

### Task 4: Add `open_in_default_app()` and `render_vscode_launch_json()`

**Files:**
- Modify: `Editor/Scripts/csharp_platform_utils.py`
- Test: `Editor/Tests/test_csharp_platform_utils.py` (extend)

`os.startfile` exists only on Windows (`AttributeError` on Linux). And the VS Code launch.json template hardcodes `build/windows/...GameLauncher.exe`. Both need host-aware logic; put the pure logic here so it's testable.

- [ ] **Step 1: Write the failing tests**

Append to `Editor/Tests/test_csharp_platform_utils.py`:

```python
@pytest.mark.unit
@pytest.mark.parametrize("os_name,platform,expected", [
    ("nt", "win32", None),
    ("posix", "darwin", "open"),
    ("posix", "linux", "xdg-open"),
])
def test_default_opener(os_name, platform, expected):
    assert ppu._default_opener(os_name=os_name, platform=platform) == expected


@pytest.mark.unit
def test_open_in_default_app_uses_opener_on_linux(monkeypatch):
    calls = {}
    monkeypatch.setattr(ppu, "_default_opener", lambda: "xdg-open")

    def fake_popen(argv, **kwargs):
        calls["argv"] = argv
        return object()

    monkeypatch.setattr(ppu.subprocess, "Popen", fake_popen)
    assert ppu.open_in_default_app("/tmp/x.cs") is True
    assert calls["argv"] == ["xdg-open", "/tmp/x.cs"]


@pytest.mark.unit
def test_open_in_default_app_returns_false_on_error(monkeypatch):
    monkeypatch.setattr(ppu, "_default_opener", lambda: "xdg-open")

    def boom(argv, **kwargs):
        raise OSError("no opener")

    monkeypatch.setattr(ppu.subprocess, "Popen", boom)
    assert ppu.open_in_default_app("/tmp/x.cs") is False


@pytest.mark.unit
def test_render_launch_json_linux():
    text = ppu.render_vscode_launch_json(host_platform="linux")
    assert "build/linux/bin/profile/" in text
    assert '"default": "GameLauncher"' in text
    assert ".exe" not in text


@pytest.mark.unit
def test_render_launch_json_windows_is_valid_json():
    import json
    text = ppu.render_vscode_launch_json(host_platform="win32")
    assert "build/windows/bin/profile/" in text
    assert '"default": "GameLauncher.exe"' in text
    json.loads(text)  # must parse
```

- [ ] **Step 2: Run to verify failure**

Run: `python -m pytest Editor/Tests/test_csharp_platform_utils.py -q`
Expected: FAIL (`AttributeError: module ... has no attribute '_default_opener'` / `render_vscode_launch_json`).

- [ ] **Step 3: Implement the two helpers**

Append to `Editor/Scripts/csharp_platform_utils.py`:

```python
import subprocess


def _default_opener(os_name=None, platform=None):
    """Return the argv[0] opener for the current OS, or None for Windows.

    None is the sentinel meaning "use os.startfile" (Windows only).
    """
    os_name = os.name if os_name is None else os_name
    platform = sys.platform if platform is None else platform
    if os_name == "nt":
        return None
    return "open" if platform == "darwin" else "xdg-open"


def open_in_default_app(path):
    """Open `path` in the OS default application. Returns True on best-effort
    success, False on failure. Never raises."""
    target = str(path)
    try:
        opener = _default_opener()
        if opener is None:
            os.startfile(target)  # noqa: S606 - Windows-only, intended
            return True
        subprocess.Popen([opener, target], close_fds=True)
        return True
    except Exception:
        return False


# O3DE build tree folder per host, e.g. build/windows, build/linux, build/mac.
_BUILD_DIR_BY_PLATFORM = {"win32": "windows", "linux": "linux", "darwin": "mac"}


def render_vscode_launch_json(host_platform=None):
    """Render the .vscode/launch.json body with host-appropriate launcher
    path + default name. VS Code launch.json cannot detect the host at F5
    time, so we bake the right values in at generation time."""
    platform = sys.platform if host_platform is None else host_platform
    # sys.platform is "linux"/"linux2"; normalize.
    if platform.startswith("linux"):
        platform = "linux"
    build_dir = _BUILD_DIR_BY_PLATFORM.get(platform, "linux")
    launcher_default = "GameLauncher.exe" if platform == "win32" else "GameLauncher"
    return f'''\
{{
    "version": "0.2.0",
    "configurations": [
        {{
            "name": "O3DESharp: Attach to Editor",
            "type": "coreclr",
            "request": "attach",
            "processName": "Editor"
        }},
        {{
            "name": "O3DESharp: Attach to GameLauncher",
            "type": "coreclr",
            "request": "attach",
            "processName": "GameLauncher"
        }},
        {{
            "name": "O3DESharp: Launch GameLauncher (profile)",
            "type": "coreclr",
            "request": "launch",
            "preLaunchTask": "",
            "program": "${{workspaceFolder}}/../../../../build/{build_dir}/bin/profile/${{input:launcherExeName}}",
            "args": [],
            "cwd": "${{workspaceFolder}}/../../../..",
            "console": "internalConsole",
            "stopAtEntry": false,
            "justMyCode": true
        }}
    ],
    "inputs": [
        {{
            "id": "launcherExeName",
            "type": "promptString",
            "description": "Game launcher name (e.g. NewProject.GameLauncher). Set once and VS Code remembers it for the workspace.",
            "default": "{launcher_default}"
        }}
    ]
}}
'''
```

- [ ] **Step 4: Run to verify pass**

Run: `python -m pytest Editor/Tests/test_csharp_platform_utils.py -q`
Expected: PASS (all tests, ~11).

- [ ] **Step 5: Commit**

```bash
git add Editor/Scripts/csharp_platform_utils.py Editor/Tests/test_csharp_platform_utils.py
git commit -m "Editor: add open_in_default_app() and render_vscode_launch_json() helpers"
```

### Task 5: Wire `resolve_dotnet()` into every dotnet subprocess call site

**Files:**
- Modify: `Editor/Scripts/csharp_binding_generator.py:461-482, 604`
- Modify: `Editor/Scripts/generate_bindings.py:601`
- Modify: `Editor/Scripts/csharp_editor_tools.py:1291`
- Modify: `Editor/Scripts/csharp_project_manager.py:1444`
- Modify: `Editor/Scripts/csharp_editor_bootstrap.py:1295`
- Test: `Editor/Tests/test_dotnet_resolution_wired.py`

Replace bare `"dotnet"` in subprocess argument lists with `resolve_dotnet()`. Add a module-level `from csharp_platform_utils import resolve_dotnet` to each file.

- [ ] **Step 1: Write the failing guard test**

Create `Editor/Tests/test_dotnet_resolution_wired.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Guards that no editor script invokes `dotnet` as a bare PATH literal.

Every dotnet subprocess must go through csharp_platform_utils.resolve_dotnet()
so it works on a Linux editor launched without the user's login PATH.
"""

import ast
import re
from pathlib import Path

import pytest

SCRIPTS = Path(__file__).parent.parent / "Scripts"
FILES = [
    "csharp_binding_generator.py",
    "generate_bindings.py",
    "csharp_editor_tools.py",
    "csharp_project_manager.py",
    "csharp_editor_bootstrap.py",
]

# A subprocess arg list literally starting with a "dotnet" string.
BARE_DOTNET = re.compile(r"""\[\s*["']dotnet["']""")


@pytest.mark.unit
@pytest.mark.parametrize("name", FILES)
def test_no_bare_dotnet_list_literal(name):
    text = (SCRIPTS / name).read_text(encoding="utf-8")
    hits = BARE_DOTNET.findall(text)
    assert not hits, f"{name} still has {len(hits)} bare ['dotnet', ...] subprocess list(s)"


@pytest.mark.unit
@pytest.mark.parametrize("name", FILES)
def test_references_resolve_dotnet(name):
    text = (SCRIPTS / name).read_text(encoding="utf-8")
    assert "resolve_dotnet" in text, f"{name} must use resolve_dotnet()"


@pytest.mark.unit
@pytest.mark.parametrize("name", FILES)
def test_still_parses(name):
    ast.parse((SCRIPTS / name).read_text(encoding="utf-8"))
```

- [ ] **Step 2: Run to verify it fails**

Run: `python -m pytest Editor/Tests/test_dotnet_resolution_wired.py -q`
Expected: FAIL — `test_no_bare_dotnet_list_literal` and `test_references_resolve_dotnet` fail for all five files.

- [ ] **Step 3: Edit `csharp_binding_generator.py`**

Add near the top imports:
```python
from csharp_platform_utils import resolve_dotnet
```
Replace the block at lines 465-482:
```python
        if tool_path.suffix == ".dll":
            # Prebuilt DLL path - fastest startup, no MSBuild involvement
            args = ["dotnet", str(tool_path)]
        elif tool_path.suffix == ".csproj":
            # Csproj fallback - dotnet run with --no-build first attempt
            # would be ideal but requires a prior build that we can't
            # guarantee here. Plain dotnet run is what we have.
            args = ["dotnet", "run", "--project", str(tool_path), "--"]
        elif tool_path.is_dir():
            # Directory - look for a DLL first, then fall back to csproj
            dll = tool_path / "O3DESharp.BindingGenerator.dll"
            csproj = tool_path / "O3DESharp.BindingGenerator.csproj"
            if dll.exists():
                args = ["dotnet", str(dll)]
            elif csproj.exists():
                args = ["dotnet", "run", "--project", str(csproj), "--"]
            else:
                args = ["dotnet", "run", "--"]
        else:
            # Assume it's an executable
            args = [str(tool_path)]
```
with:
```python
        _dn = resolve_dotnet()
        if tool_path.suffix == ".dll":
            # Prebuilt DLL path - fastest startup, no MSBuild involvement
            args = [_dn, str(tool_path)]
        elif tool_path.suffix == ".csproj":
            # Csproj fallback - dotnet run with --no-build first attempt
            # would be ideal but requires a prior build that we can't
            # guarantee here. Plain dotnet run is what we have.
            args = [_dn, "run", "--project", str(tool_path), "--"]
        elif tool_path.is_dir():
            # Directory - look for a DLL first, then fall back to csproj
            dll = tool_path / "O3DESharp.BindingGenerator.dll"
            csproj = tool_path / "O3DESharp.BindingGenerator.csproj"
            if dll.exists():
                args = [_dn, str(dll)]
            elif csproj.exists():
                args = [_dn, "run", "--project", str(csproj), "--"]
            else:
                args = [_dn, "run", "--"]
        else:
            # Assume it's an executable
            args = [str(tool_path)]
```
Replace line 604 `["dotnet", "--version"],` with `[resolve_dotnet(), "--version"],`.

- [ ] **Step 4: Edit the remaining four files**

`generate_bindings.py` — add the import; replace line 601:
```python
                        ["dotnet", "build", str(csproj_path), "-c", "Release", "--nologo"],
```
with:
```python
                        [resolve_dotnet(), "build", str(csproj_path), "-c", "Release", "--nologo"],
```

`csharp_editor_tools.py` — add the import; replace line 1291:
```python
                    ["dotnet", "build", csproj, "-c", "Debug", "--nologo"],
```
with:
```python
                    [resolve_dotnet(), "build", csproj, "-c", "Debug", "--nologo"],
```

`csharp_project_manager.py` — add the import; replace line 1444:
```python
                    ["dotnet", "build", str(csproj_path), "-c", configuration],
```
with:
```python
                    [resolve_dotnet(), "build", str(csproj_path), "-c", configuration],
```

`csharp_editor_bootstrap.py` — add the import; replace line 1295:
```python
                    ["dotnet", "test", str(csharp_tests_dir / "BindingGenerator.Tests.csproj"), "--verbosity", "normal"],
```
with:
```python
                    [resolve_dotnet(), "test", str(csharp_tests_dir / "BindingGenerator.Tests.csproj"), "--verbosity", "normal"],
```

> Note: the `net8.0` *fallback path* strings in `csharp_project_manager.py` (lines 602, 606, 860-861, 865-866) are deliberate backward-compat and must **not** be changed here.

- [ ] **Step 5: Run to verify pass**

Run: `python -m pytest Editor/Tests/test_dotnet_resolution_wired.py -q`
Expected: PASS (15 cases).

- [ ] **Step 6: Commit**

```bash
git add Editor/Scripts/csharp_binding_generator.py Editor/Scripts/generate_bindings.py Editor/Scripts/csharp_editor_tools.py Editor/Scripts/csharp_project_manager.py Editor/Scripts/csharp_editor_bootstrap.py Editor/Tests/test_dotnet_resolution_wired.py
git commit -m "Editor: route all dotnet invocations through resolve_dotnet() for Linux"
```

### Task 6: Wire `open_in_default_app()` into the two `os.startfile` sites

**Files:**
- Modify: `Editor/Scripts/csharp_editor_tools.py:553-561, 1018-1026`
- Test: `Editor/Tests/test_dotnet_resolution_wired.py` (extend with a startfile guard)

- [ ] **Step 1: Add the failing guard**

Append to `Editor/Tests/test_dotnet_resolution_wired.py`:

```python
@pytest.mark.unit
def test_no_unguarded_startfile_in_editor_tools():
    text = (SCRIPTS / "csharp_editor_tools.py").read_text(encoding="utf-8")
    assert "os.startfile" not in text, (
        "csharp_editor_tools.py must use csharp_platform_utils.open_in_default_app() "
        "instead of os.startfile (Windows-only)."
    )
```

- [ ] **Step 2: Run to verify failure**

Run: `python -m pytest Editor/Tests/test_dotnet_resolution_wired.py::test_no_unguarded_startfile_in_editor_tools -q`
Expected: FAIL ("os.startfile" present).

- [ ] **Step 3: Replace both sites**

Add to the top imports of `csharp_editor_tools.py`:
```python
from csharp_platform_utils import open_in_default_app
```
Replace lines 553-561:
```python
        import subprocess as _sp
        try:
            _sp.Popen(["code", script_path])
        except Exception:
            try:
                import os
                os.startfile(script_path)
            except Exception as e:
                QMessageBox.warning(self, "Error", f"Could not open script: {e}")
```
with:
```python
        import subprocess as _sp
        try:
            _sp.Popen(["code", script_path])
        except Exception:
            if not open_in_default_app(script_path):
                QMessageBox.warning(self, "Error", f"Could not open script: {script_path}")
```
Replace lines 1018-1026:
```python
            import subprocess as _sp
            try:
                _sp.Popen(["code", data["path"]])
            except Exception:
                try:
                    import os
                    os.startfile(data["path"])
                except Exception as e:
                    QMessageBox.warning(self, "Error", f"Could not open file: {e}")
```
with:
```python
            import subprocess as _sp
            try:
                _sp.Popen(["code", data["path"]])
            except Exception:
                if not open_in_default_app(data["path"]):
                    QMessageBox.warning(self, "Error", f"Could not open file: {data['path']}")
```

- [ ] **Step 4: Run to verify pass**

Run: `python -m pytest Editor/Tests/test_dotnet_resolution_wired.py -q`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Editor/Scripts/csharp_editor_tools.py Editor/Tests/test_dotnet_resolution_wired.py
git commit -m "Editor: use open_in_default_app() instead of Windows-only os.startfile"
```

### Task 7: Linux-shape the generated VS Code launch.json

**Files:**
- Modify: `Editor/Scripts/csharp_project_manager.py:286-324` (remove the constant), `:1303` (call the renderer)
- Test: covered by `test_csharp_platform_utils.py` (Task 4) + a wiring assertion here

- [ ] **Step 1: Add the failing wiring test**

Append to `Editor/Tests/test_dotnet_resolution_wired.py`:

```python
@pytest.mark.unit
def test_project_manager_uses_launch_renderer():
    text = (SCRIPTS / "csharp_project_manager.py").read_text(encoding="utf-8")
    assert "render_vscode_launch_json" in text, (
        "csharp_project_manager.py must render launch.json via the host-aware helper."
    )
    assert "build/windows/bin/profile" not in text, (
        "The hardcoded Windows launch.json path must be gone."
    )
```

- [ ] **Step 2: Run to verify failure**

Run: `python -m pytest Editor/Tests/test_dotnet_resolution_wired.py::test_project_manager_uses_launch_renderer -q`
Expected: FAIL.

- [ ] **Step 3: Replace the constant with the renderer call**

Add to the top imports of `csharp_project_manager.py`:
```python
from csharp_platform_utils import render_vscode_launch_json, resolve_dotnet
```
(If Task 5 already added `resolve_dotnet` to this file's import, extend that line instead of duplicating.)

Delete the `VSCODE_LAUNCH_JSON_TEMPLATE = '''\ ... '''` constant (lines 286-324, the triple-quoted block ending at line 324).

Replace line 1303:
```python
                    launch_path.write_text(VSCODE_LAUNCH_JSON_TEMPLATE)
```
with:
```python
                    launch_path.write_text(render_vscode_launch_json())
```

- [ ] **Step 4: Run to verify pass**

Run: `python -m pytest Editor/Tests -q -k "launch or platform_utils"`
Expected: PASS. Then full syntax check: `python -c "import ast; ast.parse(open('Editor/Scripts/csharp_project_manager.py', encoding='utf-8').read())"` → no output (parses).

- [ ] **Step 5: Commit**

```bash
git add Editor/Scripts/csharp_project_manager.py Editor/Tests/test_dotnet_resolution_wired.py
git commit -m "Editor: render launch.json host-aware (build/linux on Linux, no .exe)"
```

### Task 8: Fix stale ".NET 8.0" SDK install strings

**Files:**
- Modify: `Editor/Scripts/csharp_project_manager.py:1518`
- Modify: `Editor/Scripts/csharp_editor_bootstrap.py:1308`
- Test: `Editor/Tests/test_dotnet_resolution_wired.py` (extend)

The toolchain is net9.0, but two user-facing errors still say "install .NET 8.0 SDK", misdirecting a Linux user to the wrong SDK.

- [ ] **Step 1: Add the failing guard**

Append to `Editor/Tests/test_dotnet_resolution_wired.py`:

```python
@pytest.mark.unit
@pytest.mark.parametrize("name", ["csharp_project_manager.py", "csharp_editor_bootstrap.py"])
def test_no_stale_dotnet8_sdk_message(name):
    text = (SCRIPTS / name).read_text(encoding="utf-8")
    assert ".NET 8.0 SDK" not in text, f"{name} has a stale '.NET 8.0 SDK' install message"
```

- [ ] **Step 2: Run to verify failure**

Run: `python -m pytest Editor/Tests/test_dotnet_resolution_wired.py -q -k stale_dotnet8`
Expected: FAIL for both files.

- [ ] **Step 3: Fix the strings**

`csharp_project_manager.py` line 1518:
```python
                "message": "dotnet CLI not found. Please install .NET 8.0 SDK.",
```
→
```python
                "message": "dotnet CLI not found. Please install .NET 9.0 SDK.",
```

`csharp_editor_bootstrap.py` line 1308:
```python
                general.log("⚠ dotnet not found - install .NET 8.0 SDK")
```
→
```python
                general.log("⚠ dotnet not found - install .NET 9.0 SDK")
```

- [ ] **Step 4: Run to verify pass**

Run: `python -m pytest Editor/Tests/test_dotnet_resolution_wired.py -q -k stale_dotnet8`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Editor/Scripts/csharp_project_manager.py Editor/Scripts/csharp_editor_bootstrap.py Editor/Tests/test_dotnet_resolution_wired.py
git commit -m "Editor: correct stale '.NET 8.0 SDK' install prompts to 9.0"
```

### Task 9: Add Linux/macOS JetBrains Toolbox paths for Rider discovery

**Files:**
- Modify: `Editor/Scripts/csharp_editor_bootstrap.py:596-599`

`_find_rider_executable()` already sets `exe_name = "rider" if not Windows`, but its `toolbox_candidates` list only has Windows paths, so on Linux the Toolbox-installed Rider is never found. Add the Linux and macOS Toolbox roots (the existing `rglob(exe_name)` at line 605 then finds the `rider` launcher). This path is best-effort; managed-attach degrades to a logged manual-attach fallback if Rider isn't found, so no test is required — a syntax check suffices.

- [ ] **Step 1: Add the platform Toolbox dirs**

Replace lines 596-599:
```python
    toolbox_candidates = [
        home / "AppData" / "Local" / "Programs" / "Rider" / "bin" / exe_name,
        home / "AppData" / "Local" / "JetBrains" / "Toolbox" / "apps",
    ]
```
with:
```python
    toolbox_candidates = [
        # Windows
        home / "AppData" / "Local" / "Programs" / "Rider" / "bin" / exe_name,
        home / "AppData" / "Local" / "JetBrains" / "Toolbox" / "apps",
        # Linux (Toolbox default)
        home / ".local" / "share" / "JetBrains" / "Toolbox" / "apps",
        # macOS (Toolbox default)
        home / "Library" / "Application Support" / "JetBrains" / "Toolbox" / "apps",
    ]
```

- [ ] **Step 2: Verify it parses**

Run: `python -c "import ast; ast.parse(open('Editor/Scripts/csharp_editor_bootstrap.py', encoding='utf-8').read())"`
Expected: no output (parses cleanly).

- [ ] **Step 3: Commit**

```bash
git add Editor/Scripts/csharp_editor_bootstrap.py
git commit -m "Editor: discover Toolbox-installed Rider on Linux/macOS"
```

---

## M1 — CMake managed build (verify on Linux)

### Task 10: Build O3DE.Core via `dotnet` on non-MSVC generators

**Files:**
- Modify: `Code/CMakeLists.txt:218-263` (StageO3DECore) and `:840-873` (the `if(MSVC)` block)

On MSVC, `include_external_msproject(O3DE.Core ...)` makes Visual Studio build the managed API. On Linux (Ninja/Make) that block is skipped and `O3DE.Core.dll` is never built by CMake — `StageO3DECore` only *copies* a pre-existing DLL and is itself gated on the DLL existing at configure time, so a fresh Linux checkout stages nothing. Add a non-MSVC build target (same `dotnet` mechanism used for the binding generator at `:290-301`) and make staging depend on it. **This is CMake and cannot be unit-tested here; verify on Linux and re-verify Windows.**

- [ ] **Step 1: Add the non-MSVC build target**

At the end of the `if(MSVC) ... endif()` block (after line 873), add an `else()` branch by changing line 840/873. Replace:
```cmake
if(MSVC)
    # Get the gem root for proper folder organization
    get_property(gem_root GLOBAL PROPERTY "@GEMROOT:${gem_name}@")
```
with:
```cmake
if(MSVC)
    # Get the gem root for proper folder organization
    get_property(gem_root_for_core_build GLOBAL PROPERTY "@GEMROOT:${gem_name}@")
    set(gem_root ${gem_root_for_core_build})
```
and replace the closing `endif()` at line 873 with:
```cmake
    message(STATUS "O3DESharp: C# projects integrated into Visual Studio solution")
else()
    # Non-MSVC generators (Ninja/Make on Linux/macOS) don't consume
    # include_external_msproject, so build O3DE.Core with the dotnet CLI
    # directly - same mechanism as the binding generator above. The output
    # lands where StageO3DECore expects it (bin/Release/net9.0).
    get_property(gem_root_for_core_build GLOBAL PROPERTY "@GEMROOT:${gem_name}@")
    set(O3DE_CORE_CSPROJ "${gem_root_for_core_build}/Assets/Scripts/O3DE.Core/O3DE.Core.csproj")
    if(NOT TARGET O3DE.Core AND EXISTS "${O3DE_CORE_CSPROJ}")
        set(O3DE_CORE_DOTNET_CONFIG "Release")
        add_custom_target(O3DE.Core ALL
            COMMENT "O3DESharp: Building O3DE.Core (dotnet build -c ${O3DE_CORE_DOTNET_CONFIG})"
            COMMAND ${DOTNET_EXECUTABLE} build "${O3DE_CORE_CSPROJ}"
                -c ${O3DE_CORE_DOTNET_CONFIG}
                --nologo
            BYPRODUCTS "${O3DE_CORE_SCRIPTS_DIR}/bin/${O3DE_CORE_DOTNET_CONFIG}/net9.0/O3DE.Core.dll"
            VERBATIM
        )
        if(TARGET Coral.Managed)
            add_dependencies(O3DE.Core Coral.Managed)
        endif()
        message(STATUS "O3DESharp: O3DE.Core will be built via dotnet (non-MSVC generator)")
    endif()
endif()
```

> `O3DE_CORE_SCRIPTS_DIR` is already defined at line 215, in the same file scope, so it is in scope here.

- [ ] **Step 2: Make StageO3DECore always exist and depend on the build**

The current staging block (lines 227-263) is wrapped in `if(EXISTS "${O3DE_CORE_BUILD_OUTPUT}/O3DE.Core.dll") ... else() message(...) endif()`. On a fresh Linux checkout the DLL doesn't exist at configure time, so the target is never created. Restructure so the target always exists and orders after the build.

Replace line 227:
```cmake
if(EXISTS "${O3DE_CORE_BUILD_OUTPUT}/O3DE.Core.dll")
```
with:
```cmake
# Always define the staging target; the O3DE.Core build target (MSVC msproject
# or the non-MSVC dotnet custom target) produces the DLL before staging runs.
if(TRUE)
```
and, so a missing PDB in Release doesn't fail the copy, make the PDB/deps copies tolerant — replace lines 239-244:
```cmake
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${O3DE_CORE_BUILD_OUTPUT}/O3DE.Core.pdb"
            "${O3DE_CORE_STAGING_DIR}/O3DE.Core.pdb"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${O3DE_CORE_BUILD_OUTPUT}/O3DE.Core.deps.json"
            "${O3DE_CORE_STAGING_DIR}/O3DE.Core.deps.json"
```
with:
```cmake
        # PDB/deps.json may or may not be emitted depending on config; copy
        # them only if present so a Release build without a PDB doesn't fail.
        COMMAND ${CMAKE_COMMAND} -DSRC=${O3DE_CORE_BUILD_OUTPUT}/O3DE.Core.pdb -DDST=${O3DE_CORE_STAGING_DIR}/O3DE.Core.pdb -P "${CMAKE_CURRENT_LIST_DIR}/o3desharp_copy_if_exists.cmake"
        COMMAND ${CMAKE_COMMAND} -DSRC=${O3DE_CORE_BUILD_OUTPUT}/O3DE.Core.deps.json -DDST=${O3DE_CORE_STAGING_DIR}/O3DE.Core.deps.json -P "${CMAKE_CURRENT_LIST_DIR}/o3desharp_copy_if_exists.cmake"
```

- [ ] **Step 3: Add the copy-if-exists helper script**

Create `Code/o3desharp_copy_if_exists.cmake`:
```cmake
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
# Copies SRC to DST only if SRC exists. Used for optional build artifacts
# (PDB, deps.json) so a missing file never fails the staging build step.

if(EXISTS "${SRC}")
    execute_process(COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${SRC}" "${DST}")
endif()
```

- [ ] **Step 4: (Windows) re-verify no regression**

On the existing Windows setup:
Run: the project's normal Windows configure + build of the `O3DESharp` + `O3DESharp.StageO3DECore` targets.
Expected: `O3DE.Core` still builds through the VS msproject path; `StageO3DECore` copies `O3DE.Core.dll` (+ PDB/deps.json when present) to `bin/O3DE.Core`. No new configure warnings about `O3DE.Core`.

- [ ] **Step 5: (Linux, deferred) verify the managed build + stage**

On a Linux O3DE engine with this gem:
Run: `cmake --build <build> --target O3DE.Core` then `cmake --build <build> --target O3DESharp.StageO3DECore`
Expected: `O3DE.Core.dll` appears at `Assets/Scripts/O3DE.Core/bin/Release/net9.0/O3DE.Core.dll` and is copied to `bin/O3DE.Core/O3DE.Core.dll`; no "Build O3DE.Core C# project first" message.

- [ ] **Step 6: Commit**

```bash
git add Code/CMakeLists.txt Code/o3desharp_copy_if_exists.cmake
git commit -m "Linux: build O3DE.Core via dotnet on non-MSVC generators; robust staging"
```

---

## M1 — Cross-repo, CI, docs, end-to-end

### Task 11: Verify (and, if needed, patch) Coral fork Linux desktop hosting

**Files:** none in this repo — this is a cross-repo dependency in `github.com/WatchDogStudios/Coral` (fetched by `Code/CMakeLists.txt:108-138`). Owned by the maintainer.

Coral is "desktop-only by design"; Linux desktop uses the same cross-platform `nethost → hostfxr → libcoreclr.so` chain as Windows. This task confirms the fork actually resolves and boots the CLR on Linux; if it doesn't, the fix lands in Coral, not here.

- [ ] **Step 1: (Linux, deferred) build + run the gem, watch Coral init**

On a Linux O3DE engine with this gem, launch the Editor and check the log for the Coral init path.
Expected: `[Coral]`-prefixed init logs and a successful `CoralHostManager::Initialize` (no `DotNetNotFound` from `CoralHostManager.cpp:186-188`). A test script's `OnCreate`/`OnTick` fires.

- [ ] **Step 2: If Coral fails to find the runtime on Linux**

Diagnose in the Coral fork: confirm it uses `nethost`/`hostfxr` (not a Windows-only `LoadLibrary` of `hostfxr.dll`) and that `libnethost`/`libhostfxr.so` are discoverable. Patch the fork's Linux hosting; re-point `O3DESHARP_CORAL_SOURCE_DIR` at the patched tree for local testing (`Code/CMakeLists.txt:53`). Record the outcome; this task is **not** complete until a script runs on Linux.

- [ ] **Step 3: Record the result**

Note in the PR / plan whether Coral needed changes and link the Coral commit if so. No commit in this repo unless the fetched ref/pin changes.

### Task 12: Run the Python editor test suite on Linux CI

**Files:**
- Modify: `.teamcity/settings.kts`

A Linux build type already runs the xUnit suites (added in 1.2.0). Add a step that runs `Editor/Tests` under pytest on Linux so the new portability guards protect against regressions.

- [ ] **Step 1: Add a pytest step to the Linux build type**

In `.teamcity/settings.kts`, inside the Linux build type's `steps { }` block (the one whose commands use `$HOME/.dotnet/dotnet` / `set -e`, alongside the existing "Run O3DE.Core xUnit tests" step), add:
```kotlin
        script {
            name = "Run O3DESharp Python editor tests"
            scriptContent = """
                set -e
                python3 -m pip install --user --break-system-packages pytest
                python3 -m pytest Editor/Tests -q
            """.trimIndent()
        }
```

- [ ] **Step 2: Verify the DSL is well-formed**

If the TeamCity Kotlin DSL tooling is available:
Run: the repo's standard `.teamcity` maven/gradle validate (as used when the CI was migrated).
Expected: settings compile. Otherwise, structurally diff against the existing sibling `script { }` blocks to confirm identical shape. (Record verification method.)

- [ ] **Step 3: Commit**

```bash
git add .teamcity/settings.kts
git commit -m "CI: run O3DESharp Python editor tests on Linux"
```

### Task 13: Document Linux build + run

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Add a Linux section and correct the platform table**

In `README.md`, under the build/prerequisites area, add a short "Building on Linux" subsection:
```markdown
### Building on Linux

O3DESharp supports Linux (editor + runtime) with the same C# authoring, build,
hot-reload, and run workflow as Windows.

Prerequisites:
- .NET 9.0 SDK. If it isn't on your `PATH`, either add it, set `DOTNET_ROOT`,
  or set `O3DESHARP_DOTNET_EXECUTABLE` to the `dotnet` binary — the editor
  tooling checks all three (plus common install locations) before giving up.
- CMake will auto-install a .NET 9 SDK into `<build>/.dotnet` if none is found
  (see `Code/o3desharp_netverify.cmake`); export `DOTNET_ROOT=<build>/.dotnet`
  so the editor picks up that copy.

The managed `O3DE.Core` API builds via the `dotnet` CLI on Ninja/Make
generators (no Visual Studio required).
```
Then, wherever the README lists supported platforms, ensure it reads **Windows and Linux** (not Mac/consoles yet), matching `gem.json` and the PAL traits from Task 2.

- [ ] **Step 2: Verify Markdown renders**

Run: `python -c "import pathlib; assert 'Building on Linux' in pathlib.Path('README.md').read_text(encoding='utf-8')"`
Expected: no assertion error.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "Docs: add Linux build/run instructions; correct supported-platform list"
```

### Task 14: End-to-end Linux parity verification

**Files:** none — a manual verification checklist gated on Task 11.

- [ ] **Step 1: (Linux, deferred) full workflow smoke**

On a Linux O3DE engine with the gem, verify each parity criterion:
1. Configure succeeds (Task 1).
2. `cmake --build` builds the gem and `O3DE.Core.dll` via dotnet (Task 10).
3. Launch the Editor; open the C# Project Manager (`open_csharp_project_manager()` from the Python console).
4. Create a C# script project; **Build** it from the manager (uses `resolve_dotnet()` — Task 5) with no `FileNotFoundError`.
5. Enter Game Mode; the script's `OnCreate`/`OnTick` runs (Coral hosting — Task 11).
6. Edit the script, rebuild; hot-reload swaps the assembly and the change takes effect.
7. "Open in editor" on a script opens it (uses `open_in_default_app()` — Task 6).

Expected: all seven pass. Record any failures against the owning task.

- [ ] **Step 2: Full here-verifiable suite green**

Run: `python -m pytest Editor/Tests -q`
Expected: PASS (all M0/M1 Python guards + helper tests).

- [ ] **Step 3: Final commit / PR**

```bash
git add -A
git commit -m "Linux parity (M0+M1): end-to-end verification notes"
```

---

## Self-review notes (author)

- **Spec coverage (M0/M1 rows of the spec):** missing Linux stub → Task 1; trait drift → Task 2; non-MSVC O3DE.Core build → Task 10; dotnet resolution → Tasks 3/5; os.startfile → Tasks 4/6; launch.json → Tasks 4/7; stale ".NET 8.0" → Task 8; Rider Linux path → Task 9; Coral::String routing → unchanged by this plan (no new marshaling added; called out as a constraint for later ABI work, not M1); Coral Linux hosting → Task 11; Linux CI → Task 12; docs → Task 13; end-to-end exit criterion → Task 14. Deferred (per spec non-goals): clang-backend Linux, self-contained deploy, ABI/AOT.
- **Placeholders:** none — every code/test/command step carries concrete content.
- **Type/name consistency:** `resolve_dotnet`, `open_in_default_app`, `render_vscode_launch_json`, `_default_opener`, `_DOTNET_EXE_NAME`, `_common_dotnet_locations` are defined in Tasks 3-4 and referenced consistently in Tasks 5-7 and the tests.
