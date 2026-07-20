# SP-0 — Single Source of Truth — Design

**Date:** 2026-07-15
**Status:** Design accepted; ready for implementation planning.
**Author:** Mikael K. Aboagye (WD Studios Corp.), via collaborative brainstorm 2026-07-15.
**Parent program:** `docs/superpowers/specs/2026-07-15-first-class-csharp-program-design.md` (SP-0)

---

## 1. Goal

Make `F:\O3DESharp` the **only** copy of this gem that exists, and make vendored drift
**structurally impossible** rather than merely discouraged.

Today the engine fork contains a second, diverging copy at `F:\engine\Gems\O3DESharp`. Everything
in the first-class-C# program is unsafe to build until there is exactly one authoritative source.

## 2. Locked decisions

| Decision | Choice |
|---|---|
| Consumption model | **External registered gem, no vendoring.** The engine references the gem's own checkout via `o3de register --gem-path`; it never contains a copy. |
| Why not a submodule | A submodule permits the identical in-place drift that just occurred — it only renders it *visible* as dirty submodule state. Rejected. |
| Why not a subtree | In-place editing is expected and syncing back requires `subtree push` discipline. Highest re-drift risk. Rejected. |
| CI / reproducibility | Solved by pinning a **published gem version via the remote gem repo** (per the program spec's distribution layer), **not** by vendoring a path. |
| The unversioned "1B" work | **Rescued** and designated the starting point for SP-1. Complete — see §4. |

## 3. Verified current state

Measured 2026-07-15 against `F:\engine\Gems\O3DESharp`.

### 3.1 How the engine references the gem

- Registered in the engine index as a **`160000` gitlink** pinned at `0847037`
  ("Add diagnostic logging to OnBrowseScript + OpenScriptPicker").
- **No `.gitmodules` file exists in the engine repo.** The gitlink therefore has no URL mapping:
  `git submodule update --init --recursive` on a fresh engine clone produces an **empty directory**.
- `F:\engine\engine.json:65` lists `Gems/O3DESharp` as an external subdirectory.
- The pinned commit is **109 commits behind** this repo's `development`.
- The vendored working tree has **66 dirty entries** (39 modified, 26 untracked, 1 deleted).
- Vendored `gem.json` reports version **1.0.0**; this repo is at **1.3.0**.

### 3.2 What the divergence actually contains

The headline "+6,489 insertions" is measured against the vendored copy's **stale HEAD**, not against
this repo. Compared against this repo (line-ending normalized), the real picture is:

| Category | Count | Disposition |
|---|---|---|
| Modified files byte-identical to this repo | 15 | No action |
| Modified files trivially different (≤12 lines) | 9 | This repo wins |
| Modified files substantively different | 15 | **This repo is a strict superset** |
| "Untracked" paths that already exist here | 16 | No action (untracked only because HEAD is stale) |
| **Files existing only in the engine copy** | **9** | **Rescued — see §4** |
| Visual Studio junk (`UpgradeLog.htm`) | 1 | Discard |
| Deleted (`.github/workflows/ci.yml`) | 1 | Already absent here (TeamCity migration); consistent, no action |

Evidence that this repo is authoritative for the 15 substantively-different files:

- **Zero symbols exist only in the engine copy** across the highest-delta files
  (`csharp_editor_tools.py` ~306 differing lines, `csharp_project_manager.py`,
  `GenericDispatcher.cpp` ~278). This repo has *additional* symbols (e.g. the Task 11 QThread build
  workers, `_track_worker`).
- `csharp_editor_bootstrap.py` is **byte-identical** (1,404 lines both sides).
- Where content differs, this repo holds the *newer, corrected* text — e.g.
  `O3DESharpEditorSystemComponent.cpp` differs by exactly 6 lines, and the engine copy carries the
  stale "Phase 10 dormant scaffolding" comment that Task 14 corrected here.
- The vendored tree's newest modification is dated **2026-06-30**, predating this repo's v1.2.0
  release (2026-07-02) and Linux-parity work (2026-07-15).

**Interpretation:** the vendored copy is a stale in-place workspace whose content was subsequently
committed to this repo, after which this repo continued to improve those same files. It holds no
unique work **except** the 9 files in §4.

### 3.3 `gem.json` metadata gaps

`compatible_engines` and `engine_api_dependencies` are both empty, which causes
`compatibility.py:222` to short-circuit the check entirely — engine/gem mismatches surface as a
confusing C++ build failure instead of a clean refusal. `repo_uri` is also empty, blocking the
remote-gem-repo distribution model the program spec selected.

## 4. Prerequisite: the rescue (COMPLETE)

Nine files existed **only** in the vendored working tree — uncommitted, in a submodule with no
remote mapping, therefore in no git object store and recoverable from no clone.

Rescued verbatim to branch `feat/1b-native-trampoline-rescue` (commit `8a00627`, pushed to origin),
quarantined under `Rescued/1b-native-trampoline/`:

| File | Lines |
|---|---|
| `CoralNativeThunkHost.{h,cpp}` | 543 |
| `NativeBindingManifest.{h,cpp}` | 637 |
| `BindingRegistry.{h,cpp}` | 282 |
| `NativeBindingGenerator.cs` | 455 |
| `ReflectionCallSiteParser.cs` | 756 |
| `NativeBindingManifestSchema.cs` | 85 |
| **Total** | **2,758** |

All nine verified **byte-identical** to source. Quarantined rather than restored in place because
`O3DESharp.BindingGenerator` is SDK-style and globs `**/*.cs` — restoring the three C# files to
their real paths would pull unfinished code into the build. Verified zero-risk: BindingGenerator
builds clean and none of the rescued sources compile. CMake wiring preserved as an **unapplied**
`.patch`.

This work is the designated starting point for **SP-1**; it is not integrated here.

## 5. Design

Strictly ordered. Step 5.3 is the only destructive act and is gated behind 5.1 and 5.2.

### 5.1 Exhaustive pre-deletion verification

The §3.2 analysis was **sampled** (three files at symbol level, plus path-existence checks). Before
any deletion, run an exhaustive, scripted comparison of the entire vendored tree against this repo:

- For every file in the vendored working tree, compare against this repo **with line endings
  normalized** (CRLF↔LF differences must not mask or manufacture divergence).
- Classify each as: identical / differs / exists-only-in-engine.
- Produce a written manifest of every `exists-only-in-engine` and every `differs` path.
- **Gate:** the only acceptable `exists-only-in-engine` entries are the 9 already rescued plus
  `UpgradeLog.htm`. Any other entry halts the cutover pending a decision.
- For `differs` entries, confirm this repo is the superset (no symbol present only in the engine
  copy). Any counter-example halts the cutover.

The manifest is committed as the audit trail justifying the deletion.

### 5.2 Register the gem externally

- `o3de register --gem-path F:\O3DESharp`
- Confirm the engine and a test project resolve the gem from its own checkout (gem appears in the
  Project Manager / `o3de` gem listing; a project can enable it).
- **This must succeed before anything is deleted** — registration is the replacement mechanism, so
  it is proven working first.

### 5.3 Remove the vendored copy

- Remove the `160000` gitlink from the engine index (`git rm --cached Gems/O3DESharp`).
- Delete `F:\engine\Gems\O3DESharp` from the working tree.
- Reconcile `F:\engine\engine.json:65`'s `external_subdirectories` entry so it no longer points at
  the removed path.
- Commit in the **engine** repo with a message recording why (single-source-of-truth; gem is now
  externally registered; rescue commit referenced by SHA).

### 5.4 Populate `gem.json` metadata

In this repo:

- `compatible_engines` — the engine baseline(s) this gem supports.
- `engine_api_dependencies` — so `compatibility.py` performs a real check instead of
  short-circuiting. This single change converts the most common support failure ("mysterious C++
  error 40 minutes into a build") into an immediate, legible refusal.
- `repo_uri` — pointing at the gem repo, enabling the remote-gem-repo distribution model.

### 5.5 Document the workflow

Add a short section to `README.md` (and/or `CONTRIBUTING.md`) stating:

- The gem lives at its own checkout and is consumed by `o3de register --gem-path`.
- It must **never** be copied into an engine tree. Re-vendoring is an explicitly rejected choice,
  with a one-line note on why (this incident).
- The setup step a new developer runs.

## 6. Verification strategy

| What | How | Who |
|---|---|---|
| Rescue fidelity | Byte-comparison of all 9 files vs source | **Done** (9/9 identical) |
| Rescue is build-safe | BindingGenerator builds clean; no rescued source compiled | **Done** |
| Nothing else is lost | §5.1 exhaustive normalized comparison + committed manifest | Implementer |
| Gem resolves externally | `o3de register`; gem visible to engine + test project | **Maintainer** (needs the engine) |
| Engine still configures/builds | Full engine configure + build after removal | **Maintainer** (cannot be done from the gem repo) |
| Metadata gating works | Deliberately mismatch `engine_api_dependencies`; confirm a clean refusal, not a build failure | Implementer |

## 7. Risks

1. **Deletion is irreversible for uncommitted content.** Mitigated by the completed rescue (§4) and
   the §5.1 gate. Ordering is non-negotiable; no deletion before both pass.
2. **Engine-side steps cannot be verified from this repo.** 5.2, 5.3 and the engine build are the
   maintainer's to run. The spec must not claim they are done when they are not.
3. **Loss of the self-contained engine clone.** Accepted: a fresh engine clone will require a gem
   clone plus one register step. This is the deliberate cost of drift-immunity, and it is documented
   in 5.5.
4. **No inherent version pin.** Accepted and deferred to the remote-gem-repo pin for CI/release, per
   the program spec. Not solved here.
5. **The engine fork may reference the gem path elsewhere** (build scripts, CI, project templates)
   beyond `engine.json:65`. §5.1's sweep should grep the engine tree for `Gems/O3DESharp` and
   enumerate every reference before deletion.

## 8. Non-goals

- **Not** finishing, integrating, compiling, or even evaluating the rescued 1B work — that is SP-1.
- **Not** any first-class-C# functionality (components, inspector, assets, tooling).
- **Not** standing up the remote gem repo itself — SP-0 only populates `repo_uri`; the repo is SP-P.
- **Not** resolving CI version pinning.

## 9. Open questions

1. **The missing "1B spec".** The rescued sources cite a *"1B spec §3 point 3 / §6.3"* describing a
   verified gap in Coral's native→managed call path. It does not exist in the engine tree or this
   repo. If recoverable (Perforce, another machine, an earlier session), it should be added to the
   repo — it would materially inform SP-1. Not a blocker for SP-0.
2. **Engine baseline for `compatible_engines`.** The exact O3DE baseline string(s) to declare depend
   on the fork's `engine.json`; to be read during implementation rather than guessed here.
