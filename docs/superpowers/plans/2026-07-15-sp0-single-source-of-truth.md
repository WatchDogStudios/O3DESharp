# SP-0 Single Source of Truth Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `F:\O3DESharp` the only copy of this gem that exists, and make vendored drift structurally impossible.

**Architecture:** Prove (with a committed audit manifest) that the vendored copy at `F:\engine\Gems\O3DESharp` holds nothing not already here, then delete it and have the engine consume the gem via `o3de register --gem-path` instead of containing a copy. Populate the `gem.json` metadata that makes engine/gem mismatch a clean refusal rather than a confusing build failure.

**Tech Stack:** Python 3 (stdlib only) + pytest for the verification tool; the `o3de` CLI for registration; git for the engine-side removal.

## Global Constraints

- **Ordering is non-negotiable.** Task 6 (deletion) is the only destructive act and is gated behind Tasks 2 and 5. Never delete before the manifest gate passes and external registration is proven working.
- **The rescue is already complete** — branch `feat/1b-native-trampoline-rescue`, commit `8a00627`, pushed to origin, 9 files byte-identical. Do not redo it; do not integrate it (that is SP-1).
- **Do not integrate, compile, or evaluate the rescued 1B code.** Out of scope.
- **Tasks 5, 6, 7 are MAINTAINER-EXECUTED** — they require `F:\engine`, which cannot be configured or built from the gem repo. Do not mark them complete on the maintainer's behalf.
- Commit messages must contain **no** Claude/Anthropic co-author or attribution trailers.
- Verified engine baseline (read from `F:\engine\engine.json` on 2026-07-15): `engine_name` `o3de`, `version` `4.2.0`, `api_versions` = `editor 1.0.0`, `framework 1.2.1`, `launcher 1.0.0`, `tools 1.1.0`.
- Verified: `external_subdirectories[48] = "Gems/O3DESharp"` in `F:\engine\engine.json` is the **only** reference to the gem outside its own directory.

## File structure

| File | Responsibility |
|---|---|
| `Tools/sp0_verify_divergence.py` (create) | Pure comparison logic: walk a vendored tree, classify each file against the authoritative repo with line-ending normalization, render a manifest, and evaluate the deletion gate. Stdlib only, no side effects beyond writing the manifest when run as a script. |
| `Editor/Tests/test_sp0_verify_divergence.py` (create) | Unit tests for the classifier and the gate, using temp trees. Lives in `Editor/Tests/` so the existing CI `PythonTests` build type runs it. |
| `docs/sp0-divergence-manifest.md` (create, generated) | The committed audit trail justifying deletion. |
| `gem.json` (modify) | Populate `compatible_engines`, `engine_api_dependencies`, `repo_uri`. |
| `README.md` (modify) | Document register-don't-vendor as the workflow. |
| `F:\engine\engine.json` (modify, **maintainer**) | Remove the `Gems/O3DESharp` external subdirectory entry. |

---

### Task 1: Divergence verification tool

**Files:**
- Create: `Tools/sp0_verify_divergence.py`
- Test: `Editor/Tests/test_sp0_verify_divergence.py`

**Interfaces:**
- Produces (used by Task 2):
  - `normalize(data: bytes) -> bytes`
  - `classify(vendored_root: Path, repo_root: Path) -> dict` returning keys `identical`, `differs`, `only_in_vendored` (each a sorted `list[str]` of forward-slash relative paths)
  - `ALLOWED_ONLY_IN_VENDORED: set[str]`
  - `gate(result: dict) -> list[str]` returning violation paths (empty list = pass)
  - `render_manifest(result: dict, violations: list[str]) -> str`

- [ ] **Step 1: Write the failing tests**

Create `Editor/Tests/test_sp0_verify_divergence.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Unit tests for the SP-0 divergence verification tool.

The tool decides whether deleting the vendored gem copy is lossless, so its
classifier is the gate on an irreversible action. These tests pin the two
behaviours that matter: CRLF/LF differences must NOT register as divergence
(they would drown the real signal), and any unexpected vendored-only file must
trip the gate.
"""

import importlib.util
import sys
from pathlib import Path

import pytest

_TOOL = Path(__file__).resolve().parents[2] / "Tools" / "sp0_verify_divergence.py"
_spec = importlib.util.spec_from_file_location("sp0_verify_divergence", _TOOL)
sp0 = importlib.util.module_from_spec(_spec)
sys.modules["sp0_verify_divergence"] = sp0
_spec.loader.exec_module(sp0)


def _write(root: Path, rel: str, text: str, newline: str = "\n"):
    p = root / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_bytes(text.replace("\n", newline).encode("utf-8"))


@pytest.mark.unit
def test_normalize_collapses_crlf():
    assert sp0.normalize(b"a\r\nb\r\n") == b"a\nb\n"


@pytest.mark.unit
def test_crlf_only_difference_counts_as_identical(tmp_path):
    vend, repo = tmp_path / "vend", tmp_path / "repo"
    _write(vend, "Code/a.cpp", "int main() {}\n", newline="\r\n")
    _write(repo, "Code/a.cpp", "int main() {}\n", newline="\n")

    result = sp0.classify(vend, repo)

    assert result["identical"] == ["Code/a.cpp"]
    assert result["differs"] == []
    assert result["only_in_vendored"] == []


@pytest.mark.unit
def test_real_content_difference_is_reported(tmp_path):
    vend, repo = tmp_path / "vend", tmp_path / "repo"
    _write(vend, "Code/a.cpp", "old\n")
    _write(repo, "Code/a.cpp", "new\n")

    result = sp0.classify(vend, repo)

    assert result["differs"] == ["Code/a.cpp"]
    assert result["identical"] == []


@pytest.mark.unit
def test_vendored_only_file_is_reported(tmp_path):
    vend, repo = tmp_path / "vend", tmp_path / "repo"
    _write(vend, "Code/ghost.cpp", "x\n")
    repo.mkdir(parents=True, exist_ok=True)

    result = sp0.classify(vend, repo)

    assert result["only_in_vendored"] == ["Code/ghost.cpp"]


@pytest.mark.unit
def test_build_output_directories_are_ignored(tmp_path):
    vend, repo = tmp_path / "vend", tmp_path / "repo"
    _write(vend, "bin/junk.dll", "x\n")
    _write(vend, "Code/obj/junk.o", "x\n")
    _write(vend, ".git/config", "x\n")
    repo.mkdir(parents=True, exist_ok=True)

    result = sp0.classify(vend, repo)

    assert result["only_in_vendored"] == []


@pytest.mark.unit
def test_gate_passes_for_allowlisted_rescued_files():
    result = {"identical": [], "differs": [], "only_in_vendored": sorted(sp0.ALLOWED_ONLY_IN_VENDORED)}
    assert sp0.gate(result) == []


@pytest.mark.unit
def test_gate_fails_for_unexpected_vendored_only_file():
    result = {"identical": [], "differs": [], "only_in_vendored": ["Code/Surprise.cpp"]}
    assert sp0.gate(result) == ["Code/Surprise.cpp"]


@pytest.mark.unit
def test_manifest_states_the_verdict():
    passing = sp0.render_manifest({"identical": ["a"], "differs": [], "only_in_vendored": []}, [])
    assert "DELETION APPROVED" in passing

    failing = sp0.render_manifest({"identical": [], "differs": [], "only_in_vendored": ["x"]}, ["x"])
    assert "DELETION BLOCKED" in failing
    assert "x" in failing
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `python -m pytest Editor/Tests/test_sp0_verify_divergence.py -q`
Expected: collection error — `FileNotFoundError` / `spec_from_file_location` failure, because `Tools/sp0_verify_divergence.py` does not exist yet.

- [ ] **Step 3: Implement the tool**

Create `Tools/sp0_verify_divergence.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""SP-0: prove that deleting the vendored gem copy is lossless.

The engine fork contains a vendored copy of this gem at Gems/O3DESharp that
drifted (pinned 109 commits back, with uncommitted local edits). Before that
copy is deleted, every file in it must be shown to be either identical to this
repo, superseded by this repo, or explicitly allow-listed as already rescued.

Line endings are normalized before comparison: the vendored copy is largely
CRLF and this repo is largely LF, so a raw byte compare reports ~100% of lines
as different and hides the real signal.

Usage:
    python Tools/sp0_verify_divergence.py <vendored_root> <repo_root> [-o manifest.md]

Exit code 0 = gate passed (deletion approved), 1 = gate blocked.
"""

import argparse
import sys
from pathlib import Path

# Directories that never carry source of record.
IGNORED_DIR_NAMES = {
    ".git", ".vs", ".vscode", "bin", "obj", "build", "Cache",
    "_deps", "node_modules", "__pycache__", ".worktrees",
}

# The only files permitted to exist solely in the vendored copy.
# The nine source files were rescued verbatim to branch
# feat/1b-native-trampoline-rescue (commit 8a00627, pushed) and quarantined
# under Rescued/1b-native-trampoline/. UpgradeLog.htm is Visual Studio junk.
ALLOWED_ONLY_IN_VENDORED = {
    "Code/Source/Scripting/CoralNativeThunkHost.cpp",
    "Code/Source/Scripting/CoralNativeThunkHost.h",
    "Code/Source/Scripting/Reflection/BindingRegistry.cpp",
    "Code/Source/Scripting/Reflection/BindingRegistry.h",
    "Code/Source/Scripting/Reflection/NativeBindingManifest.cpp",
    "Code/Source/Scripting/Reflection/NativeBindingManifest.h",
    "Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Configuration/NativeBindingManifestSchema.cs",
    "Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Generation/NativeBindingGenerator.cs",
    "Code/Tools/BindingGenerator/O3DESharp.BindingGenerator/Parsing/ReflectionCallSiteParser.cs",
    "UpgradeLog.htm",
}


def normalize(data: bytes) -> bytes:
    """Collapse CRLF to LF so line-ending style is not treated as divergence."""
    return data.replace(b"\r\n", b"\n")


def _iter_files(root: Path):
    """Yield forward-slash relative paths of every non-ignored file under root."""
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        rel = path.relative_to(root)
        if any(part in IGNORED_DIR_NAMES for part in rel.parts):
            continue
        yield rel.as_posix()


def classify(vendored_root: Path, repo_root: Path) -> dict:
    """Classify every vendored file as identical / differs / only_in_vendored."""
    vendored_root, repo_root = Path(vendored_root), Path(repo_root)
    identical, differs, only_in_vendored = [], [], []

    for rel in _iter_files(vendored_root):
        counterpart = repo_root / rel
        if not counterpart.is_file():
            only_in_vendored.append(rel)
            continue
        a = normalize((vendored_root / rel).read_bytes())
        b = normalize(counterpart.read_bytes())
        (identical if a == b else differs).append(rel)

    return {
        "identical": sorted(identical),
        "differs": sorted(differs),
        "only_in_vendored": sorted(only_in_vendored),
    }


def gate(result: dict) -> list:
    """Return vendored-only paths that are NOT allow-listed. Empty list = pass."""
    return sorted(set(result["only_in_vendored"]) - ALLOWED_ONLY_IN_VENDORED)


def render_manifest(result: dict, violations: list) -> str:
    """Render the committed audit trail."""
    verdict = "DELETION BLOCKED" if violations else "DELETION APPROVED"
    lines = [
        "# SP-0 Divergence Manifest",
        "",
        "Generated by `Tools/sp0_verify_divergence.py`. This is the audit trail",
        "justifying deletion of the vendored gem copy from the engine fork.",
        "",
        f"**Verdict: {verdict}**",
        "",
        "| Category | Count |",
        "|---|---|",
        f"| Identical to this repo (line-ending normalized) | {len(result['identical'])} |",
        f"| Differs (this repo authoritative) | {len(result['differs'])} |",
        f"| Exists only in the vendored copy | {len(result['only_in_vendored'])} |",
        "",
    ]

    if violations:
        lines += [
            "## Gate violations",
            "",
            "These files exist only in the vendored copy and are NOT allow-listed.",
            "**Do not delete the vendored copy.** Each must be rescued or explicitly discarded.",
            "",
        ] + [f"- `{p}`" for p in violations] + [""]
    else:
        lines += [
            "## Gate",
            "",
            "Every vendored-only file is allow-listed: the nine 1B sources rescued to",
            "`feat/1b-native-trampoline-rescue` (`8a00627`) plus `UpgradeLog.htm` (VS junk).",
            "Deletion is lossless.",
            "",
        ]

    lines += ["## Files existing only in the vendored copy", ""]
    lines += [f"- `{p}`" for p in result["only_in_vendored"]] or ["- (none)"]
    lines += ["", "## Files differing (this repo is authoritative)", ""]
    lines += [f"- `{p}`" for p in result["differs"]] or ["- (none)"]
    lines += [""]
    return "\n".join(lines)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("vendored_root")
    parser.add_argument("repo_root")
    parser.add_argument("-o", "--output", default=None)
    args = parser.parse_args(argv)

    result = classify(Path(args.vendored_root), Path(args.repo_root))
    violations = gate(result)
    manifest = render_manifest(result, violations)

    if args.output:
        Path(args.output).write_text(manifest, encoding="utf-8")

    print(manifest)
    return 1 if violations else 0


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `python -m pytest Editor/Tests/test_sp0_verify_divergence.py -q`
Expected: `8 passed`

- [ ] **Step 5: Confirm no regression in the wider suite**

Run: `python -m pytest Editor/Tests -q`
Expected: all tests pass (the 8 new ones added to the existing count).

- [ ] **Step 6: Commit**

```bash
git add Tools/sp0_verify_divergence.py Editor/Tests/test_sp0_verify_divergence.py
git commit -m "SP-0: add divergence verification tool gating the vendored-copy deletion"
```

---

### Task 2: Generate and commit the divergence manifest (the deletion gate)

**Files:**
- Create: `docs/sp0-divergence-manifest.md` (generated)

**Interfaces:**
- Consumes: `Tools/sp0_verify_divergence.py` from Task 1 (`main`, exit code 0 = pass).

- [ ] **Step 1: Run the verification against the real trees**

Run:
```bash
python Tools/sp0_verify_divergence.py "F:/engine/Gems/O3DESharp" "F:/O3DESharp" -o docs/sp0-divergence-manifest.md
echo "exit code: $?"
```

Expected: the manifest prints, ending with `exit code: 0` and containing **DELETION APPROVED**.

- [ ] **Step 2: If the gate is BLOCKED, stop**

If the exit code is `1`, the manifest lists files existing only in the vendored copy that were not
allow-listed. **Do not proceed to any later task.** Report the violating paths to the maintainer and
stop — each one is unversioned work that must be rescued (see `Rescued/1b-native-trampoline/README.md`
for the pattern) or explicitly discarded before deletion is safe.

- [ ] **Step 3: Sanity-check the manifest against the recorded findings**

Open `docs/sp0-divergence-manifest.md` and confirm it matches the values verified by a dry run on
2026-07-15:

| Category | Verified count |
|---|---|
| Identical to this repo (line-ending normalized) | **178** |
| Differs (this repo authoritative) | **43** |
| Exists only in the vendored copy | **10** |

Note on the differing count: it is larger than the ~39 files git reports as locally modified in the
vendored copy, because the copy is *also* 109 commits behind. Files changed in those 109 commits
differ too, without ever having been locally edited. Both causes land in the same bucket; this is
expected, not a warning sign.

**The third number is the one the gate enforces.** It must be exactly 10, and the listed paths must
be exactly the 9 rescued 1B sources plus `UpgradeLog.htm`. Any additional vendored-only path means
unversioned work exists that has not been rescued — stop immediately (see Step 2).

Counts drifting somewhat on the first two rows is acceptable if the vendored tree has been touched
since 2026-07-15; a change in the third row is not.

- [ ] **Step 4: Commit the audit trail**

```bash
git add docs/sp0-divergence-manifest.md
git commit -m "SP-0: commit divergence manifest proving vendored-copy deletion is lossless"
```

---

### Task 3: Populate gem.json compatibility metadata

**Files:**
- Modify: `gem.json`
- Test: `Editor/Tests/test_gem_metadata.py` (create)

**Interfaces:**
- Produces: `gem.json` keys `compatible_engines`, `engine_api_dependencies`, `repo_uri` populated (non-empty).

- [ ] **Step 1: Write the failing test**

Create `Editor/Tests/test_gem_metadata.py`:

```python
#
# Copyright (c) Contributors to the Open 3D Engine Project.
# For complete copyright and license terms please see the LICENSE at the root of this distribution.
#
# SPDX-License-Identifier: Apache-2.0 OR MIT
#
"""Guards on gem.json compatibility metadata.

O3DE's compatibility check short-circuits entirely when both compatible_engines
and engine_api_dependencies are empty (see o3de's compatibility.py), which turns
an engine/gem mismatch into a confusing C++ build failure 40 minutes in instead
of an immediate, legible refusal. These guards keep that from silently
regressing to empty.
"""

import json
from pathlib import Path

import pytest

GEM_JSON = Path(__file__).resolve().parents[2] / "gem.json"


@pytest.fixture
def gem():
    return json.loads(GEM_JSON.read_text(encoding="utf-8"))


@pytest.mark.unit
def test_gem_json_is_valid_json(gem):
    assert gem["gem_name"] == "O3DESharp"


@pytest.mark.unit
def test_compatible_engines_is_declared(gem):
    assert gem["compatible_engines"], "compatible_engines must not be empty"
    assert all(isinstance(e, str) and e for e in gem["compatible_engines"])


@pytest.mark.unit
def test_engine_api_dependencies_declared(gem):
    deps = gem["engine_api_dependencies"]
    assert deps, "engine_api_dependencies must not be empty (empty short-circuits the check)"
    # The gem uses AzCore/AzFramework, so the framework API is mandatory.
    assert any(d.startswith("framework") for d in deps), f"framework API not declared: {deps}"


@pytest.mark.unit
def test_repo_uri_is_set(gem):
    assert gem["repo_uri"].startswith("http"), "repo_uri must point at the gem repository"
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `python -m pytest Editor/Tests/test_gem_metadata.py -q`
Expected: 3 failures — `compatible_engines`, `engine_api_dependencies` and `repo_uri` are currently `[]`, `[]` and `""`.

- [ ] **Step 3: Populate the metadata**

In `gem.json`, replace:

```json
    "repo_uri": "",
    "compatible_engines": [
    ],
    "engine_api_dependencies":[
    ]
```

with:

```json
    "repo_uri": "https://github.com/WatchDogStudios/O3DESharp",
    "compatible_engines": [
        "o3de>=4.2.0"
    ],
    "engine_api_dependencies":[
        "framework>=1.2.1",
        "editor>=1.0.0",
        "tools>=1.1.0"
    ]
```

These values come from the WD Studios engine fork's `engine.json` read on 2026-07-15
(`engine_name` `o3de`, `version` `4.2.0`, `api_versions` = `editor 1.0.0`, `framework 1.2.1`,
`launcher 1.0.0`, `tools 1.1.0`). `launcher` is deliberately omitted: the gem's launcher-side code
is built through the framework API and declaring an unused dependency would over-constrain
compatibility. Use `>=` rather than `==` so the gem is not pinned to a single engine revision.

- [ ] **Step 4: Run the test to verify it passes**

Run: `python -m pytest Editor/Tests/test_gem_metadata.py -q`
Expected: `4 passed`

- [ ] **Step 5: Confirm gem.json is still valid and the suite is green**

Run:
```bash
python -c "import json,io; json.load(io.open('gem.json',encoding='utf-8')); print('gem.json valid')"
python -m pytest Editor/Tests -q
```
Expected: `gem.json valid`, then all tests pass.

- [ ] **Step 6: Commit**

```bash
git add gem.json Editor/Tests/test_gem_metadata.py
git commit -m "SP-0: declare engine compatibility metadata so mismatches fail legibly"
```

---

### Task 4: Document register-don't-vendor as the workflow

**Files:**
- Modify: `README.md`
- Test: `Editor/Tests/test_gem_metadata.py` (extend from Task 3)

- [ ] **Step 1: Write the failing test**

Append to `Editor/Tests/test_gem_metadata.py`:

```python
README = Path(__file__).resolve().parents[2] / "README.md"


@pytest.mark.unit
def test_readme_documents_external_registration():
    text = README.read_text(encoding="utf-8")
    assert "o3de register --gem-path" in text, "README must document the registration workflow"
    assert "never be copied into an engine tree" in text, (
        "README must state that vendoring the gem is a rejected practice"
    )
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `python -m pytest Editor/Tests/test_gem_metadata.py -q`
Expected: 1 failure on `test_readme_documents_external_registration`.

- [ ] **Step 3: Add the README section**

Insert the following immediately before the `## Installation` heading in `README.md`:

```markdown
### How this gem is consumed (do not vendor it)

O3DESharp lives at its own checkout and is consumed by reference. Register it once:

```bash
o3de register --gem-path /path/to/O3DESharp
```

Then enable it in your project as normal. The engine references the gem; it never contains a copy.

**The gem must never be copied into an engine tree.** This is a deliberate, rejected practice, not
an oversight. A vendored copy previously drifted 109 commits behind the source of record while
accumulating ~2,700 lines of work that existed in no repository at all — recoverable from no clone,
one `git clean` from destruction. Referencing a single checkout makes that class of drift
structurally impossible. If you need reproducible builds pinned to a gem version, pin a published
release from the gem repository rather than copying files into the engine.
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `python -m pytest Editor/Tests/test_gem_metadata.py -q`
Expected: `5 passed`

- [ ] **Step 5: Commit**

```bash
git add README.md Editor/Tests/test_gem_metadata.py
git commit -m "Docs: document external gem registration and why vendoring is rejected"
```

---

### Task 5: Register the gem externally and prove resolution — **MAINTAINER-EXECUTED**

**Files:** none in this repo.

> This task requires the O3DE CLI and the engine at `F:\engine`. It cannot be performed or verified
> from the gem repository. Do not mark it complete on the maintainer's behalf.

- [ ] **Step 1: Register the gem from its own checkout**

Run (using O3DE's bundled Python environment / the `o3de` CLI from `F:\engine`):
```bash
o3de register --gem-path F:/O3DESharp
```
Expected: success, and `F:/O3DESharp` appears in the user manifest (`~/.o3de/o3de_manifest.json`)
under the registered gems.

- [ ] **Step 2: Confirm the engine resolves the gem from the external path**

Run:
```bash
o3de get-registered --gem-name O3DESharp
```
Expected: the path printed is `F:/O3DESharp` — **not** `F:/engine/Gems/O3DESharp`.

- [ ] **Step 3: Confirm a project can still enable it**

In a test project, confirm O3DESharp is listed and can be enabled (Project Manager gem list, or
`o3de enable-gem --gem-name O3DESharp --project-path <project>`).
Expected: the gem enables and resolves to the external path.

- [ ] **Step 4: Record the outcome**

Report to the maintainer/team that external registration is proven working. **Task 6 must not begin
until this task has passed** — registration is the replacement mechanism, so it is proven before the
copy is removed.

---

### Task 6: Remove the vendored copy — **MAINTAINER-EXECUTED, DESTRUCTIVE**

**Files:**
- Delete: `F:\engine\Gems\O3DESharp` (whole directory + gitlink)
- Modify: `F:\engine\engine.json` (remove the `Gems/O3DESharp` external subdirectory entry)

> **Gate:** do not start until Task 2 reported **DELETION APPROVED** and Task 5 passed.

- [ ] **Step 1: Re-confirm both gates**

Confirm `docs/sp0-divergence-manifest.md` says **DELETION APPROVED**, and that Task 5 step 2 printed
the external path. If either is untrue, stop.

- [ ] **Step 2: Remove the gitlink from the engine index**

```bash
cd F:/engine
git rm --cached Gems/O3DESharp
```
Expected: `rm 'Gems/O3DESharp'`. This removes the `160000` entry; it does not touch the working tree.

- [ ] **Step 3: Delete the working-tree directory**

```bash
cd F:/engine
rm -rf Gems/O3DESharp
```
Expected: the directory no longer exists. (This is the destructive step; both gates protect it.)

- [ ] **Step 4: Remove the external subdirectory entry from engine.json**

In `F:\engine\engine.json`, remove the `"Gems/O3DESharp"` string from the `external_subdirectories`
array (verified 2026-07-15 as index 48 and the only reference to the gem outside its own directory).
Take care to leave the surrounding JSON array valid — no trailing comma.

Verify:
```bash
cd F:/engine
python -c "import json,io; d=json.load(io.open('engine.json',encoding='utf-8')); assert not [e for e in d['external_subdirectories'] if 'O3DESharp' in str(e)], 'entry still present'; print('engine.json valid, O3DESharp entry removed')"
```
Expected: `engine.json valid, O3DESharp entry removed`

- [ ] **Step 5: Confirm no other engine reference remains**

```bash
cd F:/engine
grep -rIl "O3DESharp" --include=*.json --include=*.cmake --include=CMakeLists.txt --include=*.py --include=*.setreg Registry scripts Templates ./*.json 2>/dev/null
```
Expected: no output. (Verified 2026-07-15 that `engine.json` was the only such reference; this
re-checks in case the fork moved on.)

- [ ] **Step 6: Commit in the engine repository**

```bash
cd F:/engine
git add engine.json Gems/O3DESharp
git commit -m "Consume O3DESharp as an externally registered gem, not a vendored copy

The vendored copy at Gems/O3DESharp was a 160000 gitlink with no .gitmodules
entry, so it had no remote mapping and a recursive clone produced an empty
directory. It had drifted 109 commits behind the gem repo while accumulating
uncommitted work that existed in no repository.

That unversioned work (2,758 lines of native-call trampoline code) was rescued
verbatim to the gem repo first - branch feat/1b-native-trampoline-rescue,
commit 8a00627. Deletion was then gated on an exhaustive line-ending-normalized
comparison proving every remaining vendored file is identical to, or superseded
by, the gem repo (audit trail: docs/sp0-divergence-manifest.md in the gem repo).

The gem is now consumed via 'o3de register --gem-path', so only one copy ever
exists and this class of drift is structurally impossible."
```

---

### Task 7: Verify the engine still configures and builds — **MAINTAINER-EXECUTED**

**Files:** none.

> Requires the full engine + O3DE build environment. Cannot be done from the gem repository.

- [ ] **Step 1: Configure the engine**

Run a normal CMake configure of `F:\engine` for a project that has O3DESharp enabled.
Expected: configure succeeds and O3DESharp is found via the registered external path. A failure
mentioning a missing `Gems/O3DESharp` path means a reference was missed in Task 6 step 5.

- [ ] **Step 2: Build**

Build the project's Editor (or the target normally used).
Expected: build succeeds, with O3DESharp compiled from `F:/O3DESharp`.

- [ ] **Step 3: Smoke-test the gem in the Editor**

Launch the Editor and confirm the C# tooling loads (the O3DESharp Python bootstrap logs on startup)
and a C# script component can still be added to an entity.
Expected: unchanged behaviour versus before the migration.

- [ ] **Step 4: Report completion**

SP-0 is complete when this passes. Record the outcome so SP-1 can begin against a single source of
truth.

---

## Sequencing note

`gem.json` is also modified by the in-flight Linux-parity branch (PR #14), which bumps `version` to
`1.3.0`. Task 3 here touches only `compatible_engines`, `engine_api_dependencies` and `repo_uri`, so
the two changes are on different keys and should merge cleanly. If PR #14 lands first, re-run Task
3's tests before committing to confirm.
