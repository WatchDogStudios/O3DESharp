#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Regression guard for Coral's Linux build wiring in Code/CMakeLists.txt.

Two gaps found while verifying Coral builds cleanly for linux-x64 (WSL/Ubuntu,
clang, CORAL_EXAMPLE=OFF/CORAL_TESTING=OFF matching this file's settings):

1. Coral's own cmake/CMakeLists.txt does an independent
   find_program(DOTNET_EXE NAMES dotnet REQUIRED) rather than accepting our
   resolved DOTNET_EXECUTABLE. o3de_sharp_netverify() can auto-download a
   private dotnet under CMAKE_BINARY_DIR/.dotnet when none is on PATH (the
   common case on a bare Linux CI image); Coral's raw find_program() can't
   see that location and fails configure. Fixed by prepending its directory
   to CMAKE_PROGRAM_PATH before FetchContent_MakeAvailable(Coral).

2. Coral.Native (HostInstance.cpp: dlopen + threads) and our own
   NativeAotHost.cpp (dlopen/dlsym/dlclose) never explicitly link dl/pthread.
   Silently fine on glibc >= 2.34 (folded into libc) but produces unresolved
   symbol errors at final link on older glibc (Ubuntu 20.04, Debian 11).
   Fixed by linking CMAKE_DL_LIBS + Threads::Threads onto Coral.Native
   (PUBLIC, so it propagates transitively into the gem) and onto the
   installer-side IMPORTED o3desharp::Coral.Native target.
"""

from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
CMAKELISTS = GEM_ROOT / "Code" / "CMakeLists.txt"


def _read():
    return CMAKELISTS.read_text(encoding="utf-8")


@pytest.mark.unit
def test_dotnet_program_path_is_forwarded_before_coral_fetch():
    text = _read()
    netverify_call = text.index("o3de_sharp_netverify()")
    program_path_prepend = text.index("list(PREPEND CMAKE_PROGRAM_PATH")
    # The real invocation, not this file's own explanatory comment mentioning it by name.
    fetch_content_make_available = text.index("FetchContent_MakeAvailable(Coral)", program_path_prepend)

    assert netverify_call < program_path_prepend < fetch_content_make_available, (
        "CMAKE_PROGRAM_PATH must be prepended with DOTNET_EXECUTABLE's directory after "
        "o3de_sharp_netverify() resolves it, and before FetchContent_MakeAvailable(Coral) "
        "runs Coral's own find_program(DOTNET_EXE) - otherwise an auto-downloaded, "
        "off-PATH dotnet is invisible to Coral's configure step."
    )


@pytest.mark.unit
def test_dev_side_coral_native_links_dl_and_threads_on_linux():
    text = _read()
    # The dev-side patch loop: "Coral has a different warning level requirements
    # than O3DE" - already patches MSVC warning flags, is the target for the
    # Linux dl/pthread fixup too.
    loop_start = text.index("Coral has a different warning level")
    loop_end = text.index("${gem_name}.API target declares")
    block = text[loop_start:loop_end]

    assert "UNIX AND NOT APPLE" in block, (
        "Missing a Linux branch in the dev-side Coral.Native patch loop."
    )
    assert "CMAKE_DL_LIBS" in block and "Threads::Threads" in block, (
        "Coral.Native must link CMAKE_DL_LIBS + Threads::Threads on Linux - "
        "HostInstance.cpp dlopen()s hostfxr and uses threads, and neither Coral's "
        "premake nor its CMakeLists.txt link them itself."
    )
    assert "PUBLIC" in block, (
        "The dl/pthread link must be PUBLIC so it propagates transitively from "
        "Coral.Native into the gem's own linked target (which also needs dl for "
        "NativeAotHost.cpp's own dlopen/dlsym/dlclose calls)."
    )


@pytest.mark.unit
def test_installer_side_coral_native_links_dl_and_threads_on_linux():
    text = _read()
    install_code_start = text.index("O3DE_SUBDIRECTORY_INSTALL_CODE")
    install_code_block = text[install_code_start:]

    assert "UNIX AND NOT APPLE" in install_code_block, (
        "Installer-exported o3desharp::Coral.Native is missing the same Linux dl/pthread "
        "fixup as the dev-side FetchContent build - an exported build linking it on "
        "older-glibc Linux would hit the same unresolved dlopen/pthread symbols."
    )
    assert "INTERFACE_LINK_LIBRARIES" in install_code_block
    assert "CMAKE_DL_LIBS" in install_code_block and "Threads::Threads" in install_code_block
