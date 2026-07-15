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
