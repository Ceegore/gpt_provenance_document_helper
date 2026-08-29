# AGENTS.md — AI Asset Provenance Helper

Guidance for any AI agent (or human) running builds, tests, or checks in this repo.
Read this **before** running anything.

## What this is

A .NET 10 **WinForms** Windows desktop app (`src/AssetProvenanceHelper`) with a large
xUnit suite (`tests/AssetProvenanceHelper.Tests`, ~900+ facts/theories). The app is a
hobby project and is **unsigned** — that fact drives the most important rule below.

## Canonical commands

```powershell
dotnet --version                                   # must match global.json (10.0.301, rollForward disabled)
dotnet restore AssetProvenanceHelper.sln
dotnet build  AssetProvenanceHelper.sln -c Debug   --no-restore -warnaserror
dotnet test   AssetProvenanceHelper.sln -c Debug   --no-build
dotnet build  AssetProvenanceHelper.sln -c Release --no-restore -warnaserror
dotnet test   AssetProvenanceHelper.sln -c Release --no-build
```

The suite runs **non-parallel by design** (`xunit.runner.json`) — do not add parallelism.
Tests reach `internal` members via `InternalsVisibleTo` and run **fully in-process**;
they never spawn the app executable.

## ⚠️ Windows Smart App Control (SAC) — the rule that governs how we test

Dev machines here run with **SAC on**, and the app is unsigned. SAC evaluates the
**executable image that Windows launches**. The consequences:

- ✅ `dotnet build` / `dotnet test` / `dotnet run` all launch **Microsoft-signed hosts**
  (`dotnet.exe`, `testhost.exe`). App code loads as a managed **DLL inside** those trusted
  processes, so SAC never evaluates it. **The entire test suite — including the in-process
  UI tests that `new MainForm()` and drive the form — runs SAC-free.** This is the intended
  path and where all bug-finding happens.
- ❌ The **only** SAC-tripping artifact in the repo is the **self-contained, win-x64
  published** `artifacts/publish/AssetProvenanceHelper.exe` — a freshly built, unsigned
  *native apphost*. SAC blocks/kills it on launch. `scripts/run_smoke_tests.ps1` does
  `Start-Process` on exactly that exe, so it can fail with **"Process exited prematurely" /
  "Main window was not created"** on a SAC-enabled machine. **That is an environment block,
  not a product bug — never report it as a defect.**

### Rules for agents

1. Do **all** functional and bug-finding work through `dotnet test`. It is SAC-safe.
2. **Do not** launch `artifacts/publish/AssetProvenanceHelper.exe`, and **do not** run
   `scripts/run_smoke_tests.ps1` as-is on a SAC machine. That self-contained exe tests
   packaging, not app logic, and is the one thing SAC blocks.
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
