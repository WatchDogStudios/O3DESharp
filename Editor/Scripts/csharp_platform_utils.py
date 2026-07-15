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
