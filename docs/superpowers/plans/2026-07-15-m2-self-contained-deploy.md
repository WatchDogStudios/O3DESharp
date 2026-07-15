# M2 — Self-Contained Deployment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a shipped O3DE game run C# scripts **without the player installing .NET**, by bundling a private CoreCLR runtime next to the launcher and pointing Coral's hostfxr at it — fully compatible with today's editor/Coral/hot-reload model (no dispatch changes).

**Architecture:** Coral currently boots a *framework-dependent* runtime: `nethost` → `hostfxr` → `hostpolicy` resolves a machine-wide .NET 9 install via `Coral.Managed.runtimeconfig.json` (`rollForward: LatestMinor`). M2 makes this *optionally self-contained*: (1) a CMake step produces a private runtime bundle per desktop RID via `dotnet publish --self-contained` and harvests the runtime files; (2) CMake deploys that bundle next to the launcher; (3) O3DESharp passes the bundle directory to Coral as a `dotnet_root` override; (4) a small **cross-repo** change in the `WatchDogStudios/Coral` fork honors that override in `HostInstance::Initialize` (via `get_hostfxr_parameters.dotnet_root`), falling back to the default machine-wide search when unset. Everything is behind an opt-in option (`O3DESHARP_BUNDLE_DOTNET_RUNTIME`), so the current framework-dependent path is unchanged by default. **Untrimmed** (the reflection/JSON dispatch fights the trimmer; trimming is out of scope).

**Tech Stack:** CMake, `dotnet publish` (self-contained, per-RID), C++ (Coral host wrapper `CoralHostManager` + the Coral fork's `HostInstance`), `.NET 9` runtime packs. Desktop RIDs: `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`.

**Verified premise (2026-07-15):** `dotnet publish -c Release -r linux-x64 --self-contained true` from a Windows box produces a bundle containing `libcoreclr.so`, `libhostfxr.so`, `libhostpolicy.so`, `libclrjit.so`, `System.Private.CoreLib.dll`, and a native ELF apphost (189 files). So the bundle-production mechanism works cross-platform and is CI-runnable on the existing Windows and Linux agents.

---

## Prerequisite / ordering

- **Depends on M1** (Linux parity, PR #14). M2's deploy plumbing rides on the M1 `${DOTNET_EXECUTABLE}` resolution and the `Bin/Scripts` staging that M1 makes portable. Land M1 first (or rebase M2 onto it).
- **One task is cross-repo** (`WatchDogStudios/Coral`, Task 4). It cannot be completed or verified from inside this repo. It is called out explicitly and owned by the maintainer.
- **No BehaviorContext / AOT / dispatch work here.** M2 is deployment only.

## File structure

- **Create:** `Code/o3desharp_runtime_bundle.cmake` — the runtime-bundle production + staging logic (one responsibility: produce/stage the private runtime per RID). Kept out of the already-large `Code/CMakeLists.txt`; included from it.
- **Create:** `Code/Tools/RuntimeBundle/probe/probe.csproj` + `probe/Program.cs` — a minimal console app published `--self-contained` purely to harvest the runtime pack for a RID (no game logic; it is never shipped, only its runtime siblings are).
- **Create:** `Editor/Tests/test_runtime_bundle_contents.py` — a repeatable version of the verified smoke: publish the probe for the host RID and assert the bundle contains the private CoreCLR + hostfxr.
- **Modify:** `Code/CMakeLists.txt` — add the `O3DESHARP_BUNDLE_DOTNET_RUNTIME` option, `include(o3desharp_runtime_bundle.cmake)`, and deploy the bundle to launcher/export targets alongside the existing Coral/O3DE.Core deploy block (near `Code/CMakeLists.txt:521-556`).
- **Modify:** `Code/Source/Scripting/CoralHostManager.h` / `.cpp` — add an optional `m_dotnetRootOverride` to `CoralHostConfig`, resolved from the deployed bundle dir at startup, and pass it into the Coral `HostInstance` settings.
- **Modify (CROSS-REPO, `WatchDogStudios/Coral`):** `HostInstance` init — honor a `dotnetRoot` override via `get_hostfxr_parameters.dotnet_root`; fall back to default search when empty.
- **Modify:** `README.md` — a "Shipping without requiring .NET (experimental)" section.

---

### Task 1: Minimal runtime-harvest probe project

**Files:**
- Create: `Code/Tools/RuntimeBundle/probe/probe.csproj`
- Create: `Code/Tools/RuntimeBundle/probe/Program.cs`

- [ ] **Step 1: Create the probe csproj**

`Code/Tools/RuntimeBundle/probe/probe.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <!--
      This app is NEVER shipped. It exists only so `dotnet publish
      --self-contained -r <rid>` lays down the private .NET runtime pack
      (libcoreclr / libhostfxr / System.*.dll) for a target RID, which the
      runtime-bundle CMake step harvests. Keeping it trivial keeps the
      publish fast and the harvested set = pure runtime.
    -->
    <InvariantGlobalization>true</InvariantGlobalization>
    <SelfContained>true</SelfContained>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Create the probe entry point**

`Code/Tools/RuntimeBundle/probe/Program.cs`:

```csharp
// Copyright (c) Contributors to the Open 3D Engine Project.
// SPDX-License-Identifier: Apache-2.0 OR MIT
//
// Intentionally trivial. See probe.csproj: this app is only published to
// harvest the self-contained .NET runtime for a target RID; it is not shipped.
System.Console.WriteLine("O3DESharp runtime probe");
```

- [ ] **Step 3: Verify the probe publishes self-contained for the host RID**

Run (host RID; substitute `linux-x64` on Linux CI):
```bash
dotnet publish Code/Tools/RuntimeBundle/probe/probe.csproj -c Release -r win-x64 --self-contained true -o "$TMPDIR/o3sharp-probe-out"
```
Expected: `Build succeeded`, and the output dir contains `hostfxr.dll`/`libhostfxr.so`, `coreclr.dll`/`libcoreclr.so`, and `System.Private.CoreLib.dll`.

- [ ] **Step 4: Commit**

```bash
git add Code/Tools/RuntimeBundle/probe/probe.csproj Code/Tools/RuntimeBundle/probe/Program.cs
git commit -m "M2: add minimal runtime-harvest probe for self-contained bundling"
```

---

### Task 2: Repeatable bundle-contents test (adapts the verified smoke)

**Files:**
- Create: `Editor/Tests/test_runtime_bundle_contents.py`

- [ ] **Step 1: Write the test**

`Editor/Tests/test_runtime_bundle_contents.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Proves the M2 runtime-bundle mechanism: publishing the probe self-contained
for the host RID yields a private CoreCLR + hostfxr (no machine .NET needed).

Marked `slow` + skipped when dotnet is unavailable, so it never blocks the fast
unit run but does exercise the real mechanism on CI (Windows + Linux agents)."""

import shutil
import subprocess
import sys
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PROBE = REPO / "Code" / "Tools" / "RuntimeBundle" / "probe" / "probe.csproj"

# hostfxr + coreclr filenames per host OS (RID inferred from the runner).
_EXPECTED = {
    "win32": ("hostfxr.dll", "coreclr.dll"),
    "linux": ("libhostfxr.so", "libcoreclr.so"),
    "darwin": ("libhostfxr.dylib", "libcoreclr.dylib"),
}


def _host_key():
    if sys.platform.startswith("linux"):
        return "linux"
    return sys.platform


@pytest.mark.slow
def test_probe_publish_yields_private_runtime(tmp_path):
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet not available")
    if not PROBE.exists():
        pytest.skip("probe project not present")

    out = tmp_path / "out"
    result = subprocess.run(
        ["dotnet", "publish", str(PROBE), "-c", "Release",
         "--self-contained", "true", "-o", str(out)],
        capture_output=True, text=True, timeout=600,
    )
    assert result.returncode == 0, result.stdout + result.stderr

    hostfxr, coreclr = _EXPECTED[_host_key()]
    files = {p.name for p in out.iterdir()}
    assert hostfxr in files, f"{hostfxr} missing from bundle: {sorted(files)[:20]}"
    assert coreclr in files, f"{coreclr} missing from bundle"
    assert "System.Private.CoreLib.dll" in files
```

- [ ] **Step 2: Run it (host RID)**

Run: `python -m pytest Editor/Tests/test_runtime_bundle_contents.py -q -m slow`
Expected: PASS on a machine with dotnet (or SKIP where dotnet is absent). It is excluded from the default fast run by the `slow` marker.

- [ ] **Step 3: Commit**

```bash
git add Editor/Tests/test_runtime_bundle_contents.py
git commit -m "M2: repeatable test that the runtime bundle ships a private CoreCLR"
```

---

### Task 3: CMake — produce + stage + deploy the runtime bundle (opt-in)

**Files:**
- Create: `Code/o3desharp_runtime_bundle.cmake`
- Modify: `Code/CMakeLists.txt` (add option + include + deploy)

- [ ] **Step 1: Write the bundle production/staging module**

Create `Code/o3desharp_runtime_bundle.cmake`. It defines a function that, for the active RID, publishes the probe self-contained into a staging dir and exposes that dir as `O3DESHARP_RUNTIME_BUNDLE_DIR`. It reuses `${DOTNET_EXECUTABLE}` (set by `o3de_sharp_netverify()` in M1).

```cmake
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
# Produces a private, self-contained .NET runtime bundle for the target RID so
# a shipped game can host C# via Coral without a machine-wide .NET install.
# Opt-in via O3DESHARP_BUNDLE_DOTNET_RUNTIME (default OFF): the default build
# stays framework-dependent and is unchanged.

function(o3de_sharp_stage_runtime_bundle out_dir_var)
    # Map CMake system + processor to a .NET RID.
    if(WIN32)
        set(_rid "win-x64")
    elseif(APPLE)
        if(CMAKE_SYSTEM_PROCESSOR MATCHES "arm64|aarch64")
            set(_rid "osx-arm64")
        else()
            set(_rid "osx-x64")
        endif()
    else()
        set(_rid "linux-x64")
    endif()

    get_property(_gem_root GLOBAL PROPERTY "@GEMROOT:${gem_name}@")
    set(_probe "${_gem_root}/Code/Tools/RuntimeBundle/probe/probe.csproj")
    set(_bundle "${CMAKE_BINARY_DIR}/Gems/O3DESharp/RuntimeBundle/${_rid}")

    add_custom_target(${gem_name}.StageRuntimeBundle ALL
        COMMENT "O3DESharp: staging private .NET runtime bundle (${_rid})"
        COMMAND ${CMAKE_COMMAND} -E make_directory "${_bundle}"
        COMMAND ${DOTNET_EXECUTABLE} publish "${_probe}"
                -c Release -r ${_rid} --self-contained true
                -o "${_bundle}"
        VERBATIM
    )
    set_property(TARGET ${gem_name}.StageRuntimeBundle
        PROPERTY FOLDER "${relative_o3desharp_gem_root}/Deploy")

    set(${out_dir_var} "${_bundle}" PARENT_SCOPE)
endfunction()
```

- [ ] **Step 2: Wire the option + include into `Code/CMakeLists.txt`**

Add near the other options (after the `O3DESHARP_AUTO_GENERATE_BINDINGS` option around line 272):

```cmake
# Bundle a private .NET runtime so shipped games don't require a machine-wide
# .NET install (M2, experimental). OFF keeps the framework-dependent default.
option(O3DESHARP_BUNDLE_DOTNET_RUNTIME
    "Bundle a private self-contained .NET runtime for shipping (experimental)" OFF)

if(O3DESHARP_BUNDLE_DOTNET_RUNTIME)
    include(o3desharp_runtime_bundle.cmake)
    o3de_sharp_stage_runtime_bundle(O3DESHARP_RUNTIME_BUNDLE_DIR)
    message(STATUS "O3DESharp: runtime bundle -> ${O3DESHARP_RUNTIME_BUNDLE_DIR}")
endif()
```

- [ ] **Step 3: Deploy the bundle to launcher/export targets**

In the existing launcher-deploy block (near `Code/CMakeLists.txt:521-556`, where Coral + O3DE.Core are copied to `Bin/Scripts`), add — guarded by the option — a copy of the whole bundle into a `dotnet/` subfolder next to the scripts, so the launcher can find it at a stable relative path:

```cmake
if(O3DESHARP_BUNDLE_DOTNET_RUNTIME AND TARGET ${gem_name}.StageRuntimeBundle)
    ly_add_target_files(TARGETS ${the_launcher_target}
        FILES "${O3DESHARP_RUNTIME_BUNDLE_DIR}"
        OUTPUT_SUBDIRECTORY "Bin/Scripts/dotnet")
    add_dependencies(${the_launcher_target} ${gem_name}.StageRuntimeBundle)
endif()
```

(Match the exact deploy idiom already used in that block for Coral/O3DE.Core — if it uses `add_custom_command(TARGET ... POST_BUILD ... copy_directory)` rather than `ly_add_target_files`, mirror that form. The engineer must read `Code/CMakeLists.txt:521-556` and follow the established pattern verbatim.)

- [ ] **Step 4: Configure-time verification**

Run a configure with the option on (desktop):
```bash
cmake -S . -B build -DO3DESHARP_BUNDLE_DOTNET_RUNTIME=ON
cmake --build build --target O3DESharp.StageRuntimeBundle
```
Expected: the bundle dir contains the host's hostfxr + coreclr (same set Task 2 asserts). **Pending Linux/full-engine env** for the launcher-deploy edge; the `StageRuntimeBundle` target alone is verifiable anywhere dotnet exists.

- [ ] **Step 5: Commit**

```bash
git add Code/o3desharp_runtime_bundle.cmake Code/CMakeLists.txt
git commit -m "M2: opt-in CMake step to build+stage+deploy a private .NET runtime"
```

---

### Task 4: Coral host wiring (this repo) + fork override (CROSS-REPO)

**Files:**
- Modify: `Code/Source/Scripting/CoralHostManager.h` (add `m_dotnetRootOverride` to `CoralHostConfig`)
- Modify: `Code/Source/Scripting/CoralHostManager.cpp` (resolve deployed bundle dir; pass override into Coral settings)
- Modify (CROSS-REPO `WatchDogStudios/Coral`): `HostInstance` init to honor the override

- [ ] **Step 1: Add the override field to `CoralHostConfig`**

In `Code/Source/Scripting/CoralHostManager.h`, add to the `CoralHostConfig` struct:

```cpp
        // Absolute path to a bundled, self-contained .NET runtime (the M2
        // "no machine .NET install" bundle). Empty => use the default
        // machine-wide hostfxr/nethost search (framework-dependent, current
        // behavior). When set, Coral points get_hostfxr_parameters.dotnet_root
        // at this directory so the private runtime is used instead.
        AZStd::string m_dotnetRootOverride;
```

- [ ] **Step 2: Resolve the deployed bundle at startup**

In `CoralHostManager::Initialize` (`.cpp`), before constructing the Coral settings, probe for the deployed bundle next to the scripts dir and set the override when present. Use `AZ::IO::Path` (portable, per M1):

```cpp
        // M2: if a private runtime bundle was deployed (Bin/Scripts/dotnet),
        // prefer it so the game runs with no machine-wide .NET install. Absent
        // => leave the override empty and fall back to the machine runtime.
        {
            AZ::IO::FixedMaxPath bundleDir = <scriptsDir>; // the same base used for Coral.Managed
            bundleDir /= "dotnet";
            if (AZ::IO::FileIOBase::GetInstance() &&
                AZ::IO::FileIOBase::GetInstance()->IsDirectory(bundleDir.c_str()))
            {
                config.m_dotnetRootOverride = bundleDir.String();
                AZLOG_INFO("[O3DESharp] Using bundled .NET runtime at %s", bundleDir.c_str());
            }
        }
```

(The engineer must locate the existing `<scriptsDir>` derivation in this file — the same `Bin/Scripts` base already used to find `Coral.Managed.dll` — and reuse it, rather than re-deriving the path.)

- [ ] **Step 3: Pass the override into the Coral `HostInstance` settings**

Where the Coral `HostSettings` / init struct is populated, forward the override (field name per the Coral fork API added in Step 4):

```cpp
        if (!config.m_dotnetRootOverride.empty())
        {
            settings.DotnetRootOverride = config.m_dotnetRootOverride.c_str();
        }
```

- [ ] **Step 4: CROSS-REPO — honor the override in the Coral fork**

**This lands in `WatchDogStudios/Coral`, not this repo; the maintainer owns it.** In `HostInstance::Initialize`, when a `DotnetRootOverride` is provided, pass it to nethost:

```cpp
    // Coral fork: HostInstance.cpp
    get_hostfxr_parameters params{ sizeof(get_hostfxr_parameters), nullptr, nullptr };
    if (settings.DotnetRootOverride && *settings.DotnetRootOverride)
    {
        params.dotnet_root = /* widened */ settings.DotnetRootOverride;
    }
    get_hostfxr_path(buffer, &bufferSize, &params);
```

Falling back to the current default (`params.dotnet_root = nullptr`) preserves today's machine-wide behavior when the override is empty.

- [ ] **Step 5: Verification (Windows + pending-Linux)**

- Windows source-build: with the option OFF, confirm the editor + a launcher still host C# unchanged (override empty → default path).
- With the option ON and a bundle deployed: run the launcher on a machine/container **with no .NET installed** and confirm scripts run (this is the M2 payoff; **pending a real launcher build + a clean no-.NET Linux/Windows environment**).
- **Not compile-verifiable in this repo** without the Coral fork Step 4 landed; commit the this-repo side with a `// pending Coral DotnetRootOverride API` note if the fork API isn't merged yet, guarded so it compiles (e.g., `#if CORAL_HAS_DOTNET_ROOT_OVERRIDE`).

- [ ] **Step 6: Commit (this-repo side only)**

```bash
git add Code/Source/Scripting/CoralHostManager.h Code/Source/Scripting/CoralHostManager.cpp
git commit -m "M2: pass a bundled-runtime dotnet_root override to Coral (fork API pending)"
```

---

### Task 5: Docs — shipping without a .NET install

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Add the section**

Add a "### Shipping without requiring .NET (experimental)" subsection under the deployment docs: explain `-DO3DESHARP_BUNDLE_DOTNET_RUNTIME=ON`, that it bundles a private CoreCLR into `Bin/Scripts/dotnet`, that it is per-RID and **untrimmed**, that it needs the Coral fork's `DotnetRootOverride` support, and that it is experimental. Note the default (OFF) remains framework-dependent (requires .NET 9 on the player's machine).

- [ ] **Step 2: Verify + commit**

```bash
python -c "import pathlib; assert 'Shipping without requiring .NET' in pathlib.Path('README.md').read_text(encoding='utf-8')"
git add README.md
git commit -m "Docs: experimental self-contained (no .NET install) shipping"
```

---

## Testing strategy

- **Runnable anywhere with dotnet (CI Windows + Linux agents):** Task 2's `test_runtime_bundle_contents.py` (`-m slow`) publishes the probe and asserts a private CoreCLR + hostfxr. This is the mechanism proof and runs on both OS agents.
- **Configure-time (desktop):** Task 3 Step 4 — `StageRuntimeBundle` produces the bundle.
- **Pending real launcher + clean no-.NET environment:** the end payoff (a game running C# on a machine with no .NET) — Task 4 Step 5. Reviewer/maintainer verifies on Linux and on a clean Windows VM.
- **Regression:** with the option OFF, the full existing editor/launcher path is byte-for-byte unchanged (all M2 code is behind `O3DESHARP_BUNDLE_DOTNET_RUNTIME` / an empty override) — confirm the default build is unaffected.

## Risks & mitigations

1. **Cross-repo Coral dependency (Task 4 Step 4).** The this-repo side compiles/guards without it, but the payoff needs the fork API. Mitigation: guard behind `#if CORAL_HAS_DOTNET_ROOT_OVERRIDE`; land the fork change in lockstep.
2. **Bundle size.** A self-contained runtime is ~70-200 MB/RID. Mitigation: opt-in only; document; trimming deferred (its own risk given reflection).
3. **RID/arch matrix.** Only desktop RIDs in scope; consoles/mobile are the later Mono milestone, not this.
4. **hostfxr version match.** The bundled runtime must satisfy `Coral.Managed.runtimeconfig.json`. Mitigation: the probe targets the same `net9.0`; keep the channel in sync with `o3desharp_netverify.cmake`'s `9.0`.

## Non-goals

- No trimming (untrimmed bundle first).
- No NativeAOT / Mono / dispatch / BehaviorContext work (later milestones).
- No console/mobile RIDs.
- No change to the default framework-dependent behavior (M2 is strictly additive + opt-in).
