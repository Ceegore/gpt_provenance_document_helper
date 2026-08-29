# Fix Plan — gaa1.md Remediation

**Repo:** `Ceegore/gpt_provenance_document_helper`
**Branch:** `fix/bugrun1-issues` (PR #2)
**Base commit at time of audit:** `6f20c4f`
**Plan authored:** 2026-08-29
**Intended executor:** Claude Sonnet, working in this repo on Windows

---

## 0. How to use this document

Work the phases **in order**. Phase 0 is a hard blocker: CI is red right now, and
nothing else can be verified until it is green.

Each phase states **what is wrong**, **why it is real**, **the fix**, and **how to
prove the fix worked**. Do not skip the proof step — the entire reason this plan
exists is that a previous run reported "993/993 green" from a local working tree
that did not match a fresh checkout.

> **Rule for this plan:** never report a result you did not observe. If a step
> could not be run, say so and say why. "Not proven" is an acceptable outcome.
> "Assumed passing" is not.

---

## 1. Triage of the gaa1.md findings

The audit was independently verified against the repo before writing this plan.
Its factual claims hold up. Its *severity framing* needs adjustment in two places,
and it **undercounted** one problem.

| ID | Claim | Verdict | Notes |
|---|---|---|---|
| **B2-001** | 2 tests fail on CRLF checkouts | ✅ **REAL — top blocker** | Confirmed in CI run `33225089350`. Root cause verified below. |
| **B2-002** | Forbidden production coverage exclusions | ✅ **REAL, and worse than reported** | Audit found 3 sites. There are **12**. See §4. |
| **B2-003** | Coverage far below 100% | ✅ **REAL** | 90.60% lines / 85.06% branches / 447-of-451 methods. |
| **B2-004** | Coverage gate too weak | ✅ **REAL** | Rounds percentages, 90/85 thresholds, hardcoded 13-file list, no method coverage. |
| **B2-005** | Mutation scope incomplete | ✅ **REAL** | `break: 80`, mutates ~6 paths of 55 production files. |
| **B2-006** | CI fail-fast hides later phases | ⚠️ **PARTIALLY REAL** | Correct observation, but fail-fast is *right* for normal CI. Fix by adding a separate audit workflow, not by weakening CI. |
| **B2-007** | PR run tests merge commit, not branch HEAD | ❌ **NOT A DEFECT** | This is standard GitHub `pull_request` behaviour and is arguably more correct (it tests what you'd get after merge). Cheap to satisfy anyway — log the SHA and tree status. Do not restructure CI for this. |

### Two corrections to the audit's framing

1. **"UNPROVEN" is not the same as "defective."** The audit marks packaging,
   smoke, mutation and flake-repeat as FAIL because the CI run aborted before
   reaching them. They were not *observed failing* — they were *not run*. Keep
   that distinction in all future reporting.
2. **Literal 100.00% is not free, and a small exclusion set is legitimate.**
   Three methods in this codebase call `dialog.ShowDialog(this)` — a blocking
   modal Win32 dialog that cannot be driven in an unattended test. The correct
   engineering answer is not to pretend they are covered, nor to leave the
   denominator silently distorted, but to keep a **tiny, explicit,
   gate-enforced allowlist** and cover everything else. See §4.

---

## 2. Phase 0 — Fix the CRLF test failures (BLOCKER)

### What is wrong

Two tests fail on every fresh checkout:

```
RenderReferenceMapsAllNineValues
RenderFinalRefAssistedMapsPromptAndReference
```

```
Assert.Contains() Failure: Sub-string not found
Expected substring: "Prompt:\nexact prompt"
```

### Why it is real (verified root cause)

- The repo has **no `.gitattributes`**, and `core.autocrlf=true` on Windows.
- `UpgradeV13ProviderSessionTests.CreateSnapshot()` builds the template from a
  C# **raw string literal**. A raw string literal's line endings are whatever the
  *source file on disk* uses.
- Fresh checkout → source file has CRLF → literal contains CRLF → rendered output
  contains CRLF.
- The assertions hard-code `\n`. They fail.
- **This is why the previous local run showed 993/993:** the local working tree
  had LF files (git even warned `LF will be replaced by CRLF the next time Git
  touches it`). The local tree was not representative of a clean checkout.

`ProviderTemplateRenderer.Render()` only substitutes tags; it does no line-ending
conversion. **The renderer is correct. The tests are wrong.**

### Fix

**Do not change `ProviderTemplateRenderer` to canonicalise LF.** It is currently
line-ending-preserving, which is the right behaviour for a tool that writes
provenance documents. Changing production behaviour to make a test pass would be
backwards.

**2.0.1 — Make the semantic assertions line-ending agnostic.**

In `tests/AssetProvenanceHelper.Tests/UpgradeV13ProviderSessionTests.cs`, normalise
before asserting on multi-line structure:

```csharp
private static string Normalize(string value) =>
    value.Replace("\r\n", "\n");
```

```csharp
var normalized = Normalize(result);
Assert.Contains($"Prompt:\n{AppConstants.NotRecordedValue}", normalized);
```

```csharp
Assert.Contains("Prompt:\nexact prompt", normalized);
```

Apply to **every** assertion in the test suite that embeds `\n` and is checked
against rendered template output. Grep for the pattern before assuming there are
only two:

```bash
grep -rn 'Assert\.\(Contains\|Equal\).*\\n' tests/
```

**2.0.2 — Add explicit line-ending preservation tests.**

The normalisation above deliberately stops the semantic tests from checking line
endings, so add dedicated tests that *do* check them, with explicitly constructed
inputs that do not depend on the source file's encoding:

```csharp
[Fact]
public void Render_PreservesLfTemplateLineEndings()
{
    var content = "Prompt:\n<<<PROMPT>>>";
    // ...build snapshot from content, render...
    Assert.Contains("Prompt:\nvalue", result);
    Assert.DoesNotContain("\r\n", result);
}

[Fact]
public void Render_PreservesCrLfTemplateLineEndings()
{
    var content = "Prompt:\r\n<<<PROMPT>>>";
    // ...build snapshot from content, render...
    Assert.Contains("Prompt:\r\nvalue", result);
}
```

This is strictly better than what existed before: the semantic mapping and the
line-ending contract are now tested **separately** instead of being accidentally
coupled.

**2.0.3 — Add `.gitattributes` to kill the whole bug class.**

Create `.gitattributes` at the repo root:

```gitattributes
* text=auto

# Source and templates: deterministic LF in the repo and in the working tree,
# so raw string literals mean the same thing on every machine.
*.cs      text eol=lf
*.csproj  text eol=lf
*.sln     text eol=crlf
*.md      text eol=lf
*.json    text eol=lf
*.yml     text eol=lf
*.ps1     text eol=crlf

*.png binary
*.jpg binary
*.ico binary
```

After adding it, renormalise **in its own commit** so the diff is reviewable:

```bash
git add --renormalize .
git status --short
```

> ⚠️ Expect this to touch many files. Commit it separately from logic changes.
> Verify afterwards that `git status` is clean and a fresh clone builds.

### Proof required

```bash
git clone <repo> /tmp/fresh && cd /tmp/fresh && git checkout fix/bugrun1-issues
dotnet build AssetProvenanceHelper.sln -c Debug --no-incremental -warnaserror
dotnet test  AssetProvenanceHelper.sln -c Debug --no-build
```

Both must be green **from a clean clone, not from the existing working tree.**
This is non-negotiable — the clean-clone check is the exact step whose absence
caused the false "993/993".

---

## 3. Phase 1 — Local gate must match CI

### What is wrong

A warm incremental build reports `0 errors` even when a clean build fails
(this is how the original `CS8625` breakage reached CI). The same blind spot
just recurred with line endings.

### Fix

Add `scripts/verify_like_ci.ps1`:

```powershell
#requires -Version 7
$ErrorActionPreference = 'Stop'

Write-Host '== SHA / tree =='
git rev-parse HEAD
git status --porcelain=v1
if ((git status --porcelain=v1).Length -ne 0) {
    throw 'Working tree is dirty — CI tests a clean checkout. Commit or stash first.'
}

Write-Host '== clean build (Debug) =='
dotnet clean AssetProvenanceHelper.sln -c Debug
dotnet build AssetProvenanceHelper.sln -c Debug --no-incremental -warnaserror

Write-Host '== clean build (Release) =='
dotnet clean AssetProvenanceHelper.sln -c Release
dotnet build AssetProvenanceHelper.sln -c Release --no-incremental -warnaserror

Write-Host '== tests =='
dotnet test AssetProvenanceHelper.sln -c Debug   --no-build
dotnet test AssetProvenanceHelper.sln -c Release --no-build
```

Document in `AGENTS.md`: **"A green local run does not count unless it came from
`verify_like_ci.ps1` on a clean tree."**

---

## 4. Phase 2 — Honest coverage denominator

### What is wrong

`[ExcludeFromCodeCoverage]` appears at **12 sites**, not the 3 the audit found:

| File | Sites | What it hides | Verdict |
|---|---|---|---|
| `Program.cs` | 1 (whole class) | `Main()`, `RunApplication()`, mutex, migration, bootstrap, `Application.Run` | ❌ **Remove — testable** |
| `MainForm.Designer.cs` | 2 | `Dispose(bool)`, `InitializeComponent()` | ❌ **Remove — already executed by ~200 tests** |
| `MainForm.Layout.cs` | 6 | `BuildHeader`, `BuildSettingsGroup`, `BuildCurrentAssetGroup`, `BuildCardsSection`, `BuildStatusGroup`, `BuildRequestQueueGroup` | ❌ **Remove — already executed on every form construction** |
| `MainForm.cs` | 2 | `BrowseDownloadFolderWithDialog`, `BrowseAssetRootWithDialog` | ✅ **Keep — real modal `ShowDialog`** |
| `MainForm.RequestQueue.cs` | 1 | `PickManifestPathWithDialog` | ✅ **Keep — real modal `ShowDialog`** |

The 9 removable exclusions are hiding code that **tests already run** — they only
shrink the denominator and flatter the number. Removing them is low-risk and will
likely *raise* measured coverage in several files.

The 3 keepers each look like this, and cannot be executed unattended:

```csharp
[ExcludeFromCodeCoverage]
private void BrowseAssetRootWithDialog()
{
    using var dialog = new FolderBrowserDialog { ... };
    if (dialog.ShowDialog(this) == DialogResult.OK) { ... }   // blocks forever
}
```

Note the codebase **already** routes tests around these via
`FolderBrowserDialogProvider` / `OpenFileDialogProvider` seams — the excluded
method is only the real-dialog fallback.

### Fix

**4.1 — Remove the 9 unjustified exclusions** (`Program.cs`, `MainForm.Designer.cs`,
all 6 in `MainForm.Layout.cs`). Re-measure immediately; most of that code is
already exercised.

**4.2 — Make `Program.cs` testable** by extracting orchestration behind seams,
preserving behaviour exactly:

```csharp
internal static class Program
{
    internal static Action<Form>? ApplicationRunProvider;
    internal static Action<string, string, MessageBoxIcon>? MessageProvider;

    [STAThread]
    private static void Main() => Run();

    internal static void Run()
    {
        try { RunApplication(); }
        catch (Exception ex) { ShowMessage("...could not start.\n\n" + ex.Message,
                                           "Startup error", MessageBoxIcon.Error); }
    }

    internal static void RunApplication()
    {
        // unchanged logic; only the two tail calls go through the seams
        // ...mutex, state dir, migration, CreateContext...
        var form = new MainForm(/* context... */);
        if (ApplicationRunProvider is not null) ApplicationRunProvider(form);
        else Application.Run(form);
    }
}
```

Then test: normal startup, already-running (mutex held), startup-exception path,
state-directory creation, and legacy migration invocation. Assert on **observable
effects** (directory created, migration performed, form constructed with the right
services) — not merely that the method was entered.

**4.3 — Narrow the 3 keepers to the thinnest possible wrapper.** Each excluded
method should contain the `ShowDialog` call and nothing else; any logic around it
moves out into covered code.

**4.4 — Record the allowlist** in `coverage-exclusions.json` so the gate can
enforce it (see Phase 3):

```json
{
  "justification": "Blocking modal Win32 dialogs cannot run unattended. Tests drive these paths via the *DialogProvider seams; only the real-dialog fallback is excluded.",
  "allowed": [
    "AssetProvenanceHelper.MainForm.BrowseDownloadFolderWithDialog",
    "AssetProvenanceHelper.MainForm.BrowseAssetRootWithDialog",
    "AssetProvenanceHelper.MainForm.PickManifestPathWithDialog"
  ]
}
```

**Any `[ExcludeFromCodeCoverage]` not in this file must fail the build.**

---

## 5. Phase 3 — Rewrite the coverage gate

### What is wrong

Current gate (`.github/workflows/ci.yml`, "Verify Coverage Artifact"):

- rounds to 2 dp, so `89.996%` passes as `90.00`
- thresholds are 90/85, not the target
- checks a **hardcoded 13-entry** file list — new files are never noticed
- **never checks method coverage at all**
- counts generated `obj/**/ApplicationConfiguration.g.cs` in the denominator

### Fix

Replace with a script (`scripts/verify_coverage.ps1`) that:

1. Builds the production inventory **dynamically** from
   `src/AssetProvenanceHelper/**/*.cs`, excluding `bin/`, `obj/`.
2. Classifies files with no executable sequence points (e.g. `Models/ImageSlot.cs`,
   a bare enum) as **"no executable code"** rather than silently dropping them.
3. Fails if any production file with executable code is **absent** from the report.
4. Strips generated `obj/**` from the denominator.
5. Compares **exact integer counters**, never percentages:
   ```
   covered_lines    == total_lines
   covered_branches == total_branches
   covered_methods  == total_methods
   ```
6. Cross-checks every `[ExcludeFromCodeCoverage]` in the tree against
   `coverage-exclusions.json` and fails on any unlisted one.
7. Emits a full per-file table plus explicit `UNCOVERED_LINES` /
   `UNCOVERED_BRANCHES` / `UNCOVERED_METHODS` lists.

### ⚠️ Staging — important

**Do not flip the gate to `== 100%` in the same commit that adds it.** That would
pin CI red for the whole of Phase 4 and destroy the signal.

Ship it as a **ratchet** first:

```powershell
# coverage-baseline.json — committed, updated only upward
{ "lines": 7869, "branches": 2432, "methods": 447,
  "totalLines": 8681, "totalBranches": 2859, "totalMethods": 451 }
```

Fail on **any decrease**. Raise the baseline as Phase 4 lands. Flip to strict
equality only once the gaps are actually closed. This also fixes **B-05** from the
previous run (the one-branch cliff) properly: a ratchet cannot be tripped by a
single new `if`.

---

## 6. Phase 4 — Close the coverage gaps

This is the largest phase and is **iterative**. Expect it to span multiple
sessions. Work file-by-file, committing per file, re-measuring each time.

**Current state:** 812 uncovered lines, 427 uncovered branches, 4 uncovered methods.

### The 4 fully-uncovered methods (do these first — cheapest wins)

| Method | Location | How to reach it |
|---|---|---|
| `LoadRecentDocumentsSafe()` | `MainForm.RecentDocuments.cs:130` | Corrupt/unreadable `recent-documents.json`, then trigger the load path |
| `TryDeleteFile(string)` | `AssetProcessorService.FileOps.cs:123` | Force the atomic-write failure path so temp cleanup runs; also cover the swallowed-exception branch with a locked file |
| `ProviderTemplateCatalogService.TemplateDirectory` | getter | Direct assertion |
| `RecentDocumentHistoryService.FilePath` | getter | Direct assertion |

`TryDeleteFile` is a genuine error-recovery path, not a triviality — cover **both**
its success and its exception-swallowing branch.

### Priority order for the bulk gaps

Ordered by risk, not by size — these are the transaction, rollback, recovery and
filesystem paths where a defect means data loss:

| # | File | Lines | Branches |
|---|---|---:|---:|
| 1 | `MainForm.MainWorkflow.cs` | 73.60% | 65.48% |
| 2 | `MainForm.ImageSelection.cs` | 75.86% | 76.09% |
| 3 | `MainForm.Recovery.cs` | 78.40% | 86.09% |
| 4 | `MainForm.ReferenceWorkflow.cs` | 79.46% | 76.39% |
| 5 | `AssetProcessorService.FileOps.cs` | 84.26% | 82.05% |
| 6 | `AssetProcessorService.Main.cs` | 88.52% | 83.02% |
| 7 | `AssetProcessorService.Reference.cs` | 89.52% | 80.93% |
| 8 | `SessionService.cs` | 89.54% | 81.64% |
| 9 | `ValidationService.Session.cs` | 89.59% | 90.79% |
| 10 | `HelpOverlayControl.cs` | 98.48% | **25.00%** |
| 11 | `PromptPreviewOverlayControl.cs` | 95.52% | 62.50% |
| 12 | `MainForm.RecentDocuments.cs` | 90.12% | 75.00% |
| 13 | `ProviderTemplateRenderer.cs` | 96.67% | 75.00% |

`HelpOverlayControl` at 25% branch coverage is a striking outlier for a small file
— check it early, it is probably cheap.

### Quality bar for the new tests

For each gap, cover: **normal case → boundary → failure → observable side effects
→ recovery/retry → state after operation → filesystem after operation.**

> ❌ **Do not** write tests that merely enter a method to move the number.
> A test that calls a method and asserts nothing meaningful is worse than no
> test: it inflates coverage while proving nothing, and Phase 5 will expose it.

Generate the ranked gap list with:

```bash
dotnet test tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj \
  -c Release --no-build --collect:"XPlat Code Coverage" \
  --results-directory artifacts/coverage
```

then sort the Cobertura `<class>` entries by ascending `branch-rate`.

---

## 7. Phase 5 — Mutation testing scope

### What is wrong

`stryker-config.json` mutates 6 path patterns out of 55 production files and sets
`break: 80`, permitting a large surviving-mutant population. Entirely unmutated:
`MainForm.DirectMode.cs`, `ImageSelection.cs`, `Layout.cs`, `PromptPreview.cs`,
`ProviderTemplates.cs`, `RecentDocuments.cs`, `RequestQueue.cs`, `ValidationUi.cs`,
`Program.cs`, all UI controls, all dialogs.

Code can currently count toward coverage while being structurally exempt from
mutation — exactly the combination that lets assertion-free tests hide.

### Fix

```json
{
  "stryker-config": {
    "project": "AssetProvenanceHelper.csproj",
    "solution": "../../AssetProvenanceHelper.sln",
    "reporters": ["progress", "html", "json"],
    "thresholds": { "high": 100, "low": 95, "break": 95 },
    "mutate": ["**/*.cs", "!**/obj/**", "!**/bin/**"]
  }
}
```

Add a reviewed-equivalent-mutant manifest (`mutation-equivalents.json`) keyed by
mutant id with written justification. The final gate accepts a survivor **only**
if its id appears there. Raise `break` toward 100 as survivors are killed or
documented.

> Prefer this to a blanket `break: 100`: genuinely equivalent mutants do exist,
> and the honest move is to document them, not to pretend they are killable.

---

## 8. Phase 6 — Separate audit workflow

### What is wrong

Normal CI is fail-fast, so the two test failures skipped Release tests,
RecoveryCritical, the 20× flake loop, publish and smoke. The audit then had to
mark all of them "unproven".

**Do not make normal CI non-fail-fast** — fast feedback on a broken build is
correct and valuable.

### Fix

Add `.github/workflows/zero-exception-audit.yml`, manually dispatchable
(`workflow_dispatch`), which:

- logs `git rev-parse HEAD`, `git status --porcelain=v1`, `dotnet --info` up front
- runs every phase with `continue-on-error: true`, capturing each exit code
- **always** uploads TRX, Cobertura, mutation, smoke and leak artefacts
- ends with an aggregator step that fails if any required phase failed

This gives complete failure capture without weakening the day-to-day gate.

Also satisfies **B2-007** cheaply: the SHA and clean-tree status are logged as
audit evidence. Do not otherwise restructure CI for B2-007 — testing the merge
commit is correct `pull_request` behaviour.

---

## 9. Phase 7 — Tidy, commit, push, release

Run only after Phases 0–6 are done and a **clean-clone** verification is green.

### 9.1 Tidy the workspace

The repo root has accumulated loose working documents. Current untracked/loose
`.md` files include `AGENTS.md`, `_bugRun1.md`, `_upgrade1.md`, `vv1.md`,
`gaa1.md`, this plan, plus 16 historical `bugs*.md`.

```
docs/
  audits/      bugs1.md … bugs15.md, _bugRun1.md, gaa1.md
  plans/       _upgrade1.md, _fixPlan_gaa1.md, _changePlan2.md
  vv1.md → docs/audits/
AGENTS.md      stays at root (agent entry point)
README.md      stays at root
```

Verify `.gitignore` covers `bin/`, `obj/`, `artifacts/`, `StrykerOutput/`, `TestResults/`.
Confirm no build output or mutable runtime state (`settings.json`, `session.json`,
`recent-documents.json`, `request-progress.json`) is tracked.

### 9.2 Update docs

- `README.md` — current test/coverage story, how to run `verify_like_ci.ps1`
- `AGENTS.md` — the clean-tree rule from Phase 1; correct any stale claims
- `CHANGELOG.md` — create if absent; entry for v1.3.0

### 9.3 Commit and push

Separate, reviewable commits:

```
1. fix(tests): make provider renderer assertions line-ending agnostic
2. chore: add .gitattributes and renormalise line endings
3. test(coverage): remove unjustified ExcludeFromCodeCoverage, add startup seams
4. ci: exact-counter coverage gate with dynamic production inventory
5. test: close coverage gaps in <area>          ← repeat per area
6. ci: full-scope mutation config + equivalent-mutant manifest
7. ci: add zero-exception audit workflow
8. docs: reorganise audit/plan documents, update README and AGENTS
```

Push to `fix/bugrun1-issues`; PR #2 updates automatically. **Wait for CI to be
green before proceeding.** Do not merge on a red or skipped run.

### 9.4 Release

Only once PR #2 is green and merged to `main`:

```bash
git checkout main && git pull
git tag -a v1.3.0 -m "v1.3.0 — provider templates, request queue, direct mode, QA hardening"
git push origin v1.3.0

gh release create v1.3.0 \
  --title "v1.3.0" \
  --notes-file docs/release-notes-v1.3.0.md \
  --latest
```

Notes:
- `<Version>` in the csproj is already `1.3.0`; no bump needed.
- Existing releases v1.2.0/v1.2.1 are marked **pre-release**. v1.3.0 should be a
  **real** release (`--latest`, no `--prerelease`) — this is the user's explicit ask.
- Attach the published self-contained build if the publish step is green.
- Release notes must state plainly that the Windows executable is **unsigned**, so
  Smart App Control / SmartScreen will block it by default.

---

## 10. Definition of done

| Item | Required evidence |
|---|---|
| Clean-clone Debug build `-warnaserror` | PASS |
| Clean-clone Release build `-warnaserror` | PASS |
| Debug tests | PASS, 0 failed, 0 unexplained skips |
| Release tests | PASS |
| `Category=RecoveryCritical` | PASS |
| Coverage | exact counters at target, or ratchet raised with gaps listed |
| Unlisted `[ExcludeFromCodeCoverage]` | 0 |
| Mutation | full scope; survivors 0 or documented as equivalent |
| Leaked temp workspaces | 0 |
| Flake loop | 20× suite green |
| Packaging | structural verification PASS |
| PR #2 | green, merged |
| Release | v1.3.0 published as latest |

State honestly which of these were **observed** and which were **not run**.

---

## Appendix A — Handoff prompt for the third-party review AI

Post this verbatim once the release is published.

---

> # Independent Verification Request — `Ceegore/gpt_provenance_document_helper`
>
> You are performing an independent code and test audit of a public GitHub
> repository. Please read the repository directly.
>
> **Target:** `https://github.com/Ceegore/gpt_provenance_document_helper`
> **Release:** `v1.3.0`
> **Branch:** `main`
> **Prior audits to read first:** `docs/audits/_bugRun1.md`, `docs/audits/gaa1.md`,
> `docs/plans/_fixPlan_gaa1.md`
>
> The project is a .NET 10 WinForms tool (Windows-only) that produces AI-asset
> provenance documents. Your previous audit (`gaa1.md`) was accurate and useful —
> its CRLF finding was a genuine blocker and has been fixed. This round asks you to
> verify the fixes and continue to full depth.
>
> ## Environment restrictions — please read carefully
>
> You are a web chat with repository read access. You **cannot**:
>
> - **Run the application.** It is a Windows GUI app, and the published binary is
>   unsigned, so Smart App Control blocks it. Do not report "could not launch the
>   exe" as a defect — it is an environment constraint.
> - **Run GitHub Actions.** The project has no Actions credits remaining this
>   month. Do not request new CI runs. Read the *existing* logged runs instead.
> - **Execute tests, builds, coverage or mutation runs.** No compiler or .NET SDK
>   is available to you.
> - **Push, commit, or open PRs.**
>
> You **can and should**: read every source and test file, read committed CI
> configuration and any committed run logs/artefacts, reason about correctness
> statically, and trace each production path to the test that exercises it.
>
> ## How to report — this part matters most
>
> Your previous report was thorough but conflated three different things. Please
> separate them explicitly this time, using exactly these labels:
>
> - **`DEFECT`** — you can point at specific code and explain a concrete failure:
>   inputs/state → wrong behaviour. Include the mechanism.
> - **`RISK`** — code that looks fragile or under-tested, but you cannot
>   demonstrate a failure. Say what would confirm or refute it.
> - **`NOT VERIFIABLE HERE`** — requires running something you cannot run.
>   List these plainly. **Do not score them as failures.** "I could not execute
>   the packaging step" is not a product defect.
>
> Specifically, please avoid these patterns from last time:
>
> 1. **Do not mark unexecuted steps as FAIL.** Steps that CI skipped after an
>    earlier failure were *not run*; they did not *fail*. That distinction changes
>    the verdict.
> 2. **Do not treat any coverage below 100% as automatically a defect.** Tell us
>    *which specific uncovered branch could hide a real bug*, and why. A ranked,
>    reasoned list of the 20 most dangerous gaps is far more useful than a
>    pass/fail on a percentage.
> 3. **A small documented exclusion set is intentional.** Three methods call a
>    blocking modal `ShowDialog` and cannot run unattended; they are listed in
>    `coverage-exclusions.json` with justification, and the surrounding logic is
>    tested through injected seams. Please *audit that the allowlist is minimal and
>    honest* rather than treating its existence as a violation. If you find any
>    `[ExcludeFromCodeCoverage]` **not** in that file, that **is** a defect.
> 4. **Please report in English**, and quantify claims (file:line).
> 5. **Verify counts before asserting them.** Your last report said there were 3
>    coverage-exclusion sites; there were 12. The extra 9 were a real finding you
>    missed by not enumerating. Enumerate.
>
> ## What to examine, in priority order
>
> 1. **Verify the CRLF fix.** `.gitattributes` plus normalised assertions plus the
>    new LF/CRLF preservation tests. Is the renderer still line-ending preserving?
>    Are there other tests that embed `\n` or `\r\n` and would break on the other
>    checkout mode? Grep for them.
> 2. **Verify the coverage-exclusion cleanup.** Is every remaining exclusion in
>    `coverage-exclusions.json`? Is each genuinely unautomatable? Is `Program.cs`
>    now actually tested for: normal startup, already-running mutex, startup
>    exception, state-directory creation, legacy migration?
> 3. **Audit test *quality*, not just presence.** This is where you add the most
>    value and where a coverage number cannot help. Find tests that execute code
>    without meaningfully asserting on it. Find assertions that would still pass if
>    the production logic were inverted or deleted.
> 4. **Trace the data-loss-critical paths exhaustively** — the transaction,
>    rollback, recovery, cancellation and filesystem-promotion logic in
>    `AssetProcessorService.*`, `SessionService`, `MainForm.Recovery.cs`,
>    `MainForm.MainWorkflow.cs`. For each: what happens on crash between any two
>    steps? Is every partial state either recoverable or refused?
> 5. **Re-check the historical `bugs*.md` and `_bugRun1.md` fixes** for regression
>    or silent reversal.
> 6. **Check the gate logic itself** (`scripts/verify_coverage.ps1`,
>    `.github/workflows/*.yml`): can it be satisfied without actually meeting the
>    standard? Gates that can be gamed are worth reporting.
>
> ## Output format
>
> 1. Verdict — with the three categories counted separately
> 2. `DEFECT` findings — severity, file:line, mechanism, concrete failure
>    scenario, suggested fix
> 3. `RISK` findings — ranked, each with what would confirm it
> 4. `NOT VERIFIABLE HERE` — plain list, explicitly not counted against the verdict
> 5. Regression matrix vs. `_bugRun1.md` and `gaa1.md`
> 6. What is genuinely healthy (please keep including this — it prevents
>    good mechanisms from being refactored away)
>
> Be rigorous and go deep. Prefer one demonstrated defect over twenty
> unsubstantiated concerns.

---

*End of plan.*
