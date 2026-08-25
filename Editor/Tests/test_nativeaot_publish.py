#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Proves the M4 publish mechanism: O3DE.Core compiles to a native shared
library exporting the ABI entry point, with no CoreCLR involved.

VERIFIED PREMISE (2026-08-21): NativeAOT publish works in this environment, but
the ILCompiler targets shell out to vswhere.exe, so the Visual Studio Installer
directory must be on PATH or the native link step fails with MSB3073 exit 123.
The test adds it rather than requiring a Developer Command Prompt.

win-x64 only here. linux-x64 needs a Linux toolchain and is the maintainer's
verification - see the M4 docs task.
"""

import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

GEM_ROOT = Path(__file__).resolve().parents[2]
CORE_CSPROJ = GEM_ROOT / "Assets" / "Scripts" / "O3DE.Core" / "O3DE.Core.csproj"
PUBLISH_CMAKE = GEM_ROOT / "Code" / "o3desharp_nativeaot_publish.cmake"
ROOT_CMAKE = GEM_ROOT / "Code" / "CMakeLists.txt"

VS_INSTALLER = r"C:\Program Files (x86)\Microsoft Visual Studio\Installer"


@pytest.mark.unit
def test_publish_module_exists_and_is_opt_in():
    assert PUBLISH_CMAKE.is_file(), f"{PUBLISH_CMAKE} is missing."
    module = PUBLISH_CMAKE.read_text(encoding="utf-8")
    assert "o3de_sharp_publish_nativeaot" in module

    root = ROOT_CMAKE.read_text(encoding="utf-8")
    assert re.search(
        r'option\(O3DESHARP_PUBLISH_NATIVEAOT\s+"[^"]*"\s+OFF\)', root
    ), "the publish must default OFF so the existing build is untouched."
    assert "include(o3desharp_nativeaot_publish.cmake)" in root


@pytest.mark.unit
def test_publish_passes_the_shipping_host_mode():
    module = PUBLISH_CMAKE.read_text(encoding="utf-8")
    for flag in ("-p:PublishAot=true", "-p:NativeLib=Shared", "-p:O3DESharpHostMode=NativeAot"):
        assert flag in module, (
            f"{flag} is required: without NativeLib=Shared there is no shared library, and "
            f"without the host mode HostExportsGenerator emits no exported entry point."
        )


@pytest.mark.unit
def test_deploy_target_is_separate_from_the_coral_deploy():
    root = ROOT_CMAKE.read_text(encoding="utf-8")
    assert 'OUTPUT_SUBDIRECTORY "Bin/Scripts/aot"' in root, (
        "the AOT image must deploy alongside, not over, the Coral/O3DE.Core "
        "managed assemblies - the two artifacts are mutually exclusive per "
        "launcher, and mixing them in one directory invites loading the wrong one."
    )


@pytest.mark.slow
def test_o3de_core_publishes_as_a_native_shared_library(tmp_path):
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    if sys.platform != "win32":
        pytest.skip("win-x64 publish; linux-x64 is verified by the maintainer")

    env = dict(os.environ)
    if Path(VS_INSTALLER).is_dir():
        # ILCompiler's link step shells out to vswhere.exe. Without this the
        # publish fails with MSB3073 exit 123 in a plain shell.
        env["PATH"] = VS_INSTALLER + os.pathsep + env.get("PATH", "")

    out = tmp_path / "aot"
    result = subprocess.run(
        [
            "dotnet", "publish", str(CORE_CSPROJ), "-c", "Release", "-r", "win-x64",
            "-p:PublishAot=true", "-p:NativeLib=Shared",
            "-p:O3DESharpHostMode=NativeAot",
            "-o", str(out), "--nologo",
        ],
        capture_output=True, text=True, timeout=1800, env=env,
    )
    assert result.returncode == 0, result.stdout + result.stderr

    native = out / "O3DE.Core.dll"
    assert native.is_file(), f"no native library in {sorted(p.name for p in out.iterdir())}"
    # Measured 2026-08-22: the managed O3DE.Core.dll (Coral build) is 55-90 KB;
    # the real NativeAOT image measured ~1.19 MB (1,245,696 bytes). 500 KB
    # comfortably distinguishes "published the native library" from "copied
    # the IL one" with margin either direction.
    assert native.stat().st_size > 500_000, (
        f"O3DE.Core.dll is only {native.stat().st_size} bytes - that is the managed "
        "assembly, not a NativeAOT image."
    )
    assert (out / "O3DE.Core.lib").is_file(), "the import library proves a real native link ran"


@pytest.mark.slow
def test_published_library_exports_the_abi_entry_point(tmp_path):
    if shutil.which("dotnet") is None or sys.platform != "win32":
        pytest.skip("win-x64 only")
    dumpbin = shutil.which("dumpbin")
    if dumpbin is None:
        pytest.skip("dumpbin not on PATH; the export is verified at runtime by NativeAotHost")

    env = dict(os.environ)
    if Path(VS_INSTALLER).is_dir():
        env["PATH"] = VS_INSTALLER + os.pathsep + env.get("PATH", "")

    out = tmp_path / "aot"
    subprocess.run(
        [
            "dotnet", "publish", str(CORE_CSPROJ), "-c", "Release", "-r", "win-x64",
            "-p:PublishAot=true", "-p:NativeLib=Shared",
            "-p:O3DESharpHostMode=NativeAot", "-o", str(out), "--nologo",
        ],
        check=True, capture_output=True, text=True, timeout=1800, env=env,
    )

    exports = subprocess.run(
        [dumpbin, "/exports", str(out / "O3DE.Core.dll")],
        capture_output=True, text=True, timeout=300,
    ).stdout
    assert "O3DESharp_GetManagedExports" in exports, (
        "NativeAotHost resolves exactly this symbol; without it the image is unusable."
    )
