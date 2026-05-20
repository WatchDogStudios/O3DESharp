# TeamCity Cloud configuration

This directory holds the build configuration for the O3DESharp
TeamCity Cloud project. The configuration is **Kotlin DSL** —
TeamCity reads `settings.kts` on every push to a branch wired to
the project's Versioned Settings feature and materializes the build
configurations from it.

## One-time setup

The DSL is already checked in; the only work that has to happen in
the TeamCity UI is the initial bootstrap. Do this once when first
connecting the repo to TeamCity Cloud.

### 1. Create the project

1. Sign in to TeamCity Cloud (`<your-org>.teamcity.com`).
2. **Projects → Create project → From a repository URL**.
3. Repository URL: `https://github.com/WatchDogStudios/O3DESharp.git`.
4. Use the GitHub App connection (recommended) or paste a PAT. The
   GitHub App handles webhook plumbing + commit-status publishing
   without manual token rotation.
5. When TeamCity offers to scan the repo for build configurations,
   pick **"I want to use the existing .teamcity directory"** so it
   reads `settings.kts` instead of generating its own.

### 2. Enable Versioned Settings (lock the config to git)

1. **Project Admin → Versioned Settings**.
2. **Synchronization enabled**, set mode to
   **"use settings from VCS"**. This is the mode that makes the
   `.teamcity/settings.kts` file authoritative — TeamCity-UI edits
   are then rejected.
3. VCS root: pick the O3DESharp VCS root (created by the bootstrap).
4. Settings format: **Kotlin**.
5. Optionally check **"Use credentials stored in VCS"** so the
   `credentialsJSON:github_access_token` reference in `settings.kts`
   resolves.

### 3. Provide the GitHub status-publisher token

The commit-status publisher feature uses a token to write
"All checks succeeded / failed" badges back to GitHub commits and
PRs. The DSL references it as `credentialsJSON:github_access_token`;
you supply the actual value out-of-band:

1. **Project Admin → Parameters → Add new parameter**.
2. Name: `github_access_token` (matches what TC auto-generates for the github commit-status-publisher; previously documented as `gh-status-token` but TC translates that to the default).
3. Kind: **Password (Stored as a token in TeamCity)**.
4. Value: a GitHub PAT with `repo:status` scope (classic PAT) or a
   fine-grained PAT scoped to this repo with **Commit statuses:
   Read and write** and **Pull requests: Read**.
5. Save. The DSL's `credentialsJSON:github_access_token` reference now
   resolves to this value at build time without it ever appearing
   in version control.

If you set up the GitHub App connection in step 1, you can skip the
PAT and instead set `authType` to `storedToken` referencing the App
connection — but the App approach requires editing the DSL to
match the connection's UUID, which is less portable across project
re-creations. The PAT approach in the DSL today is the
quickest-to-stand-up option.

### 4. Configure branch protection in GitHub (optional but recommended)

To require CI green before merge:

1. **GitHub → Settings → Branches → Branch protection rules**.
2. Add or edit a rule for `main` (and `development` if you want).
3. **Require status checks to pass before merging**.
4. Search for and select `O3DESharp / All checks` — the composite
   build defined in `settings.kts`. **Don't** require the individual
   matrix builds; the composite is the stable name, and if you ever
   add a Windows-ARM or macOS matrix entry you don't want to have to
   update the branch protection rule.

## Day-to-day workflow

- Edit `.teamcity/settings.kts`, commit, push. TeamCity polls the
  branch every ~60 seconds; you can force an immediate sync via
  **Project Admin → Versioned Settings → "Load project settings
  from VCS"**.
- Build configurations that no longer exist in the DSL are
  **removed** from TeamCity on the next sync. Don't be alarmed if
  history disappears from the UI after a rename — it's preserved
  in the underlying build data, just rebound to the new name.
- Local DSL syntax check (so you don't push a broken settings.kts
  and break CI):
  ```powershell
  cd .teamcity
  mvn -q test-compile
  ```
  Requires Maven + JDK 17. The build prints compile errors with
  file + line numbers; once it's silent, the file's at least
  syntactically valid (TeamCity will catch semantic errors like
  references to non-existent feature versions on its own).

## What the build matrix does

| Build configuration | Agent OS | What it does |
|---|---|---|
| `C# build & test (Linux)` | Ubuntu cloud agent | dotnet build O3DE.Core + BindingGenerator + SourceGenerators + Smoke consumer; dotnet test xUnit (104 tests) |
| `C# build & test (Windows)` | Windows cloud agent | Same as Linux but on the Windows agent so the WPF + Windows-specific paths get coverage |
| `Python editor tests` | Ubuntu cloud agent | pytest under Editor/Tests |
| `All checks` | (composite — no agent) | Aggregator that turns green only when all three matrix entries do. This is the one branch protection should require. |

## What changed from GitHub Actions

Before TeamCity, this gem's CI lived at `.github/workflows/ci.yml`
and ran:
- C# matrix (Ubuntu + Windows) → same dotnet build + test steps
- Python (Ubuntu) → pytest

`ci.yml` is **deleted** when TeamCity is live; the migration is
intentionally lossless. If you ever need to roll back, `git revert`
the deletion commit and disable the TeamCity project until you
get back to GHA.

## Why Kotlin DSL and not the UI

- **Reviewable**: changes to CI behavior show up in PRs as code
  diffs, not as "I changed something in the UI somewhere."
- **Survives server reinstall**: a fresh TeamCity Cloud project
  reconstructs itself from this file. No manual click-through.
- **IDE support**: open `.teamcity/settings.kts` in IntelliJ IDEA
  (or any IDE with Kotlin support) and get full autocomplete from
  the `configs-dsl-kotlin` jar pulled by `pom.xml`.
- **Atomic with code**: a code change that needs new CI steps lands
  in the same commit. No "the code is in but CI hasn't caught up"
  state.
