# Rescued: "1B" native-call trampoline work

**Status: UNFINISHED. Quarantined. Not wired into any build. Not expected to compile.**

This directory preserves in-progress work that existed in exactly one place on disk and was
**not versioned in any repository**. It is a verbatim rescue, not an integration.

## Where this came from

- **Source:** the working tree of `F:\engine\Gems\O3DESharp` — a vendored copy of this gem inside
  the WD Studios O3DE engine fork (`git.wdstudios.tech/internal/engine.git`, branch
  `GPU-motivated-v1`).
- **Why it was at risk:** that copy is registered in the engine as a `160000` gitlink with **no
  `.gitmodules` entry**, so it has no remote mapping and a fresh recursive clone of the engine
  produces an empty directory. These files were **uncommitted** in that working tree. They existed
  on one disk, in no git object store, recoverable from no clone. A `git clean -fdx` in the engine
  tree would have destroyed them.
- **That copy's pinned HEAD:** `0847037` ("Add diagnostic logging to OnBrowseScript +
  OpenScriptPicker"), which is **109 commits behind** this repo's `development`.
- **Age:** the newest modified file in that tree was dated **2026-06-30**, i.e. before this repo's
  v1.2.0 release (2026-07-02) and the Linux-parity work (2026-07-15).
- **Rescued:** 2026-07-15.

Everything *else* in that vendored copy was verified to be already present here or superseded by
newer work in this repo (15 files byte-identical after line-ending normalization, 9 trivially
different, 15 substantively different where this repo is a strict superset — zero symbols existed
only in the engine copy). **These 9 files were the sole exception.**

## What it is

A native-call trampoline / static-dispatch layer, in three parts plus generator support:

| File | Lines | Role |
|---|---|---|
| `CoralNativeThunkHost.{h,cpp}` | 206 / 337 | A native→managed **pinned-thunk** path that bypasses `Coral::ManagedObject::InvokeMethod`, which does a managed-side string lookup + reflection dispatch on *every* call. |
| `NativeBindingManifest.{h,cpp}` | 239 / 398 | A **build-time snapshot** of what BehaviorContext exposes *and whether/how each entry can be invoked as a direct native C++ trampoline* instead of through `BehaviorMethod::Call`. |
| `BindingRegistry.{h,cpp}` | 169 / 113 | Runtime lookup from a stable binding-id string (e.g. `"Vector3::GetLength"`) to a generated native trampoline function pointer. |
| `NativeBindingManifestSchema.cs` | 85 | Generator-side schema for the manifest. |
| `NativeBindingGenerator.cs` | 455 | Emits the native trampolines. |
| `ReflectionCallSiteParser.cs` | 756 | Parses reflection call sites to drive generation. |

**Total: 2,758 lines.**

## Missing design document

The source comments reference a **"1B spec §3 point 3 / §6.3"** describing a verified gap in Coral's
native→managed call path. **That document could not be found** anywhere in the engine tree or this
repo. If it exists elsewhere (Perforce, another machine, an old session), it should be recovered —
it would materially inform the work that builds on this.

## Why it is quarantined rather than restored in place

Purely build safety. The paths below mirror their original locations exactly, but restoring the
three `.cs` files to their real paths would immediately pull them into the build: the
`O3DESharp.BindingGenerator` project is SDK-style and globs `**/*.cs`. Since this code is unfinished
and references types that may not exist yet, that would break the BindingGenerator build and its
test suite. Quarantining keeps this rescue provably zero-risk.

The C++ files would have been inert (CMake `FILES` lists are explicit), but they are kept here too
for consistency and so the whole feature travels as one unit.

## How to restore

Paths under this directory map **1:1** onto repository paths. To restore a file, copy it from
`Rescued/1b-native-trampoline/<path>` to `<path>`.

`o3desharp_private_files.cmake.patch` captures the build-wiring hunks from the engine copy that
added the C++ files to `Code/o3desharp_private_files.cmake`. It is **not applied** — this repo's
copy of that file has since diverged, so the patch is preserved as intent, not as something to
`git apply` blindly.

## What happens next

This is the designated starting point for **SP-1 (Marshaling Core)** — the v1.3 BehaviorContext
dispatch refactor — per
`docs/superpowers/specs/2026-07-15-first-class-csharp-program-design.md`. That sub-project will get
its own spec, which decides what of this survives, what is redesigned, and how it relates to the
dual-mode AOT static-dispatch requirement in
`docs/superpowers/specs/2026-07-02-linux-aot-support-design.md`.

Do not integrate this code ad hoc. It is preserved so that decision can be made deliberately.
