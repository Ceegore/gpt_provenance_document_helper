# AGENTS.md — AI Asset Provenance Helper

Guidance for any AI agent (or human) running builds, tests, or checks in this repo.
Read this **before** running anything.

## What this is

A .NET 10 **WinForms** Windows desktop app (`src/AssetProvenanceHelper`) with a large
xUnit suite (`tests/AssetProvenanceHelper.Tests`, ~1000+ facts/theories). The app is a
hobby project and is **unsigned** — that fact drives the most important rule below.

## Repo layout for docs and coverage tooling

- `src/AssetProvenanceHelper.Core/` — Headless class library (`OutputType=Library`)
  containing all non-GUI generation domain logic: image size planning, rate limiting,
  retry policies, OpenAI client & batch builders, DPAPI secrets, and job stores.
  Its tests in `tests/AssetProvenanceHelper.Core.Tests/` run fully in-process without
  loading the WinForms GUI subsystem image.
- `docs/audits/` — every bug-bash / QA audit report (`bugs1.md`–`bugs15.md`,
  `_bugRun1.md`, `gaa1.md`, `vv1.md`), oldest first by number/date. Read the
  most recent ones before starting new work; they carry context (known
  environment constraints, prior false starts) that isn't repeated elsewhere.
- `docs/plans/` — remediation/upgrade plans written in response to an audit
  (`_upgrade1.md`, `_changePlan2.md`, `_fixPlan_gaa1.md`).
- `code-coverage-exclusions.json` / `code-coverage-no-executable-code.json` /
  `code-coverage-baseline.json` — inputs to `scripts/verify_coverage.ps1`,
  the coverage gate. See that script's header comment for what each file
  does; do not rename them back to a `coverage*.json` prefix — that pattern
  is in `.gitignore` for CI-generated coverage artifacts and will silently
  make git ignore them.

## Canonical commands

```powershell
dotnet --version                                   # must match global.json (10.0.301, rollForward disabled)
dotnet restore AssetProvenanceHelper.sln
dotnet build  AssetProvenanceHelper.sln -c Debug   --no-restore -warnaserror
dotnet test   AssetProvenanceHelper.sln -c Debug   --no-build
dotnet build  AssetProvenanceHelper.sln -c Release --no-restore -warnaserror
dotnet test   AssetProvenanceHelper.sln -c Release --no-build
```

The suite runs **non-parallel by design** (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`
plus `xunit.runner.json`, both verified by regression tests) — do not add parallelism.
Tests reach `internal` members via `InternalsVisibleTo` and run **fully in-process**;
they never spawn the app executable.

### ⚠️ A green local run does not count unless it came from a clean tree

Two real bugs have reached CI while every *local* run looked green:

1. An incremental/warm build reported `0 errors` on a nullable-reference bug that
   only a `--no-incremental` build surfaced (the local build was stale).
2. A working tree with LF-normalized files passed locally while CI's checkout
   (CRLF) failed two tests whose C# raw string literals embed the source file's
   actual line endings.

**Both failures were invisible locally because the local tree did not match a
fresh checkout.** Before trusting or reporting a "tests pass" result, run:

```powershell
powershell -File scripts/verify_like_ci.ps1
```

This refuses to run on a dirty tree, always builds `--no-incremental`, and runs
the same Debug/Release/RecoveryCritical matrix CI runs. A pass from anything
else (a warm build, an uncommitted working tree, `dotnet build` without
`--no-incremental`) is not equivalent to a CI-green result and should not be
reported as one.

## Releasing

Releases are cut from a **version tag**, and the downloadable packages are attached
**automatically** by `.github/workflows/release.yml`.

```powershell
# 1. Bump <Version> in src/AssetProvenanceHelper/AssetProvenanceHelper.csproj
# 2. Write docs/release-notes-v<version>.md   (used as the release body if the
#    release does not exist yet; an existing release's notes are never overwritten)
# 3. Merge to main, then tag the merged commit:
git tag -a v1.4.0 -m "v1.4.0 - <summary>"
git push origin v1.4.0
```

The tag push builds and attaches:

| Asset | Purpose |
|---|---|
| `AssetProvenanceHelper-v<ver>-win-x64.zip` | Self-contained; end users need no .NET install |
| `AssetProvenanceHelper-v<ver>-framework-dependent.zip` | Fallback for users whose machine enforces SAC; runs via `dotnet AssetProvenanceHelper.dll` |
| `SHA256SUMS.txt` | Checksums for both archives |

The workflow **refuses to publish** if the tag does not match the csproj `<Version>`
(so `v1.5.0` on a `1.4.0` build fails instead of shipping a mislabelled zip), and it
verifies the assets are actually attached afterwards rather than trusting the upload.

`workflow_dispatch` accepts a tag input, so a release can be re-packaged or backfilled
without moving the tag.

> **History:** v1.3.0, v1.3.1 and v1.3.2 shipped with **no downloads at all**. `ci.yml`
> builds the zip but only uploads it via `actions/upload-artifact`, which is a
> short-lived Actions artifact, not a release asset. Do not "simplify" release.yml away
> by pointing at those CI artifacts — end users cannot download them.

## ⚠️ Windows Smart App Control (SAC) — the rule that governs how we test

Dev machines here run with **SAC on**, and the app is unsigned.

> **🛑 READ THIS BEFORE BELIEVING ANY FAILING TEST RUN.**
> If a `dotnet test` run reports a large number of failures and/or
> `Der Testhostprozess ist abgestürzt` / `test host process crashed`,
> **first grep the output for `0x800711C7`**. If it is there, those failures are
> **fake** — SAC blocked the assembly, no test actually executed, and nothing was
> proven about the code. Do **not** debug them, do **not** "fix" anything, and do
> **not** revert work because of them. Re-run via
> `powershell -File scripts/run_tests_sac_safe.ps1`.
>
> This has burned real time more than once. Full evidence and the general
> playbook: [`docs/sac-test-execution.md`](docs/sac-test-execution.md).
> Machine-wide, project-agnostic field guide (shared with other repos on this
> box): `C:\Projects\SACsolutions.md`.

Two different artifacts can trip SAC. Keep them straight:

- ❌ **The published apphost.** `artifacts/publish/AssetProvenanceHelper.exe` is a freshly
  built, unsigned *native apphost*. SAC blocks/kills it on launch.
  `scripts/run_smoke_tests.ps1` does `Start-Process` on exactly that exe, so it can fail
  with **"Process exited prematurely" / "Main window was not created"**. **Environment
  block, not a product bug — never report it as a defect.**
- ⚠️ **The product assembly itself, during `dotnet test`.** `AssetProvenanceHelper.dll`
  is a GUI-subsystem *executable image* (`OutputType=WinExe`), and SAC **does** evaluate
  it when `testhost.exe` loads it. It is blocked **intermittently** with `0x800711C7`.
  Measured on this repo: **80 Code Integrity block events, 100% of them against
  `AssetProvenanceHelper.dll`** — never against `AssetProvenanceHelper.Tests.dll` and
  never against a NuGet dependency.

  **The trigger is rate-based, not content-based.** A single run, or a targeted filtered
  run, is reliable. What provokes it is a long chain of *rebuild → run the whole suite*
  cycles in quick succession (independently confirmed in a sibling project: the block
  appeared only after 40+ sequential runs, never on individual ones). Waiting ~30–60s
  after a rebuild clears it.

  ⚠️ An earlier version of this file claimed the test suite "runs SAC-free" and that the
  published exe was "the only SAC-tripping artifact". **Both were wrong**, and that wrong
  claim is precisely what caused a later agent to misread a SAC block as a code
  regression. Do not restore those claims.

### Rules for agents

1. Do **all** functional and bug-finding work through `dotnet test`. It is *usually* fine
   — but see rule 6 for how to keep it that way and how to spot a block.
2. **Do not** launch `artifacts/publish/AssetProvenanceHelper.exe`, and **do not** run
   `scripts/run_smoke_tests.ps1` as-is on a SAC machine. That self-contained exe tests
   packaging, not app logic.
3. If you need to verify the **published package**, do it SAC-safely:
   - **Structurally** — publish, then verify the folder contents (templates/,
     provider_templates/, examples/, core DLLs, no shipped mutable state) **without
     launching** the exe; and/or
   - **Live GUI, SAC-safe** — build **framework-dependent** (NOT `--self-contained`) and
     launch through the signed host with `dotnet <dir>\AssetProvenanceHelper.dll`. Use
     `scripts/run_smoke_tests_sac_safe.ps1` for this (see below). Never launch the native
     apphost `.exe` directly.
4. **Never** toggle SAC, Defender, or any security setting to get a build to run. Turning
   SAC off is a machine-owner decision with a hard caveat (it can only be re-enabled by
   reinstalling Windows). If a step is genuinely SAC-blocked, record it as
   *skipped — environment* and move on.
5. **Do not assume GitHub Actions is available** — CI may be disabled/out of credits. The
   full suite must be runnable locally; don't push just to trigger CI or rely on its output.
6. **Keep test runs under the SAC trigger threshold.** In order of preference:
   - **Default to targeted runs while iterating:**
     `dotnet test AssetProvenanceHelper.sln -c Release --no-build --filter "FullyQualifiedName~<TestClass>"`
     Small filtered runs are fast and do not provoke the block.
   - **Use the wrapper for full-suite runs**, which settles, canaries, detects
     `0x800711C7`, backs off and retries, and exits **42** (not 1) on an environment block
     so it can never be confused with a real failure:
     ```powershell
     powershell -File scripts/run_tests_sac_safe.ps1                 # full suite, Release
     powershell -File scripts/run_tests_sac_safe.ps1 -Filter "FullyQualifiedName~FeatureV14"
     powershell -File scripts/run_tests_sac_safe.ps1 -SettleSeconds 0 # nothing was rebuilt
     ```
     It streams live output with a 30s heartbeat and enforces hard timeouts, so it
     can never sit silent. Exit codes: **0** pass, **1** real failure, **42**
     environment block, **43** timeout/inconclusive, **44** filter matched nothing.
   - **Wait ~30–60s after a rebuild** before the first heavy run.
   - **Do not re-run the full suite once it is green.** Repeat full runs are the single
     biggest contributor to the block.
   - **Avoid `--collect:"XPlat Code Coverage"` in tight loops** — the coverage collector
     roughly triples run time and was the most block-prone path observed. Run coverage
     once, at the end.
7. **Always seam external process and dialog hooks in tests.**
   When tests exercise MainForm commit workflows (e.g. `HandleMainImage`, `ExecuteMainCommit`),
   they must seam:
   - `MainForm.OpenFolderProvider = _ => { };`
   - `MainForm.MessageBoxProvider = (_, _, _, _, _) => { };`
   - `MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;`
   - `TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;`
   If `OpenFolderProvider` is left unassigned (`null`), committing an asset triggers a real
   `Process.Start("explorer.exe", ...)`, which can cause Windows Security / Smart App Control /
   Defender alerts and hang the test host.

## SAC-safe smoke test

`scripts/run_smoke_tests_sac_safe.ps1` is the SAC-safe counterpart to
`scripts/run_smoke_tests.ps1`. It launches the app through the **signed `dotnet` host**
(`dotnet AssetProvenanceHelper.dll`) instead of the self-contained apphost, then verifies
the main window comes up with title **"AI Asset Provenance Helper"** and shuts down cleanly.

Use `powershell` (Windows PowerShell 5.1, always present) locally; `pwsh`
(PowerShell 7) is not guaranteed to be installed. The scripts run under both.

```powershell
dotnet build AssetProvenanceHelper.sln -c Release --no-restore -warnaserror
powershell -File scripts/run_smoke_tests_sac_safe.ps1
# defaults to: src/AssetProvenanceHelper/bin/Release/net10.0-windows
# or point at a framework-dependent publish dir:
#   dotnet publish src/AssetProvenanceHelper/AssetProvenanceHelper.csproj -c Release -o artifacts/publish-fd
#   powershell -File scripts/run_smoke_tests_sac_safe.ps1 -AppDir artifacts/publish-fd
```

`scripts/run_smoke_tests.ps1` (self-contained exe launch + release archive) remains the
**release/CI** smoke check and is expected to run only where the exe is trusted (signed
build, SAC off, or a CI runner) — not on a SAC-enabled dev box.
