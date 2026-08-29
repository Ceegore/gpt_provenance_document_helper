# Independent Verification Request — `Ceegore/gpt_provenance_document_helper`

You are performing an independent code and test audit of a public GitHub
repository. Please read the repository directly rather than relying on this
prompt's summary.

**Target:** `https://github.com/Ceegore/gpt_provenance_document_helper`
**Release:** `v1.3.1`
**Branch:** `main`
**Read first:** `docs/audits/a.md` (your own prior audit) and this file's "What
changed" section below, so you don't re-report things already fixed.

The project is a .NET 10 WinForms tool (Windows-only) that produces AI-asset
provenance documents.

## What changed since `a.md`

Both `DEFECT` findings and all 10 `RISK` items from `a.md` were addressed in
[PR #3](https://github.com/Ceegore/gpt_provenance_document_helper/pull/3),
released as v1.3.1:

- **DEFECT 1** (tests mutating the real per-user state directory) — fixed via
  new `AppBootstrap.StateDirectoryOverride` / `Program.BaseDirectoryOverride`
  seams; `ProgramStartupTests` rewritten with integration tests through
  `Program.RunApplication()` itself.
- **DEFECT 2** (coverage ratchet comparing only covered counts) — fixed by
  extracting `Test-CoverageRatchet` (`scripts/CoverageRatchet.ps1`), which now
  ratchets on *uncovered* counts, with 6 synthetic regression scenarios in
  `scripts/verify_coverage.ratchet.tests.ps1`.
- **RISK 1–5** (transaction/rollback/recovery edge cases) — targeted tests
  added for each specific branch you cited.
- **RISK 6–9** (test-quality gaps — tests that executed code but asserted
  less than their names claimed) — all four rewritten with exact-state
  assertions.
- **RISK 10** (audit workflow's prerequisite steps were fail-fast) — fixed.

**Two real dialog-triggering bugs were found and fixed along the way**, both
only by running the *actual* full suite rather than individual test classes:
new tests missing `MainForm.MessageBoxProvider`/`Program.MessageProvider`
seams, and a **pre-existing** test (`MainFormUiTests.cs`) invoking "Open
Downloads Folder" without seaming `MainForm.OpenFolderProvider`, which fired
a real `Process.Start(explorer.exe)` against a temp folder deleted moments
later. Please specifically check whether any *other* instance of this class
of bug (a test reaching a real OS-level side effect — dialog, process launch,
file-system action outside its own workspace — because a seam wasn't
installed) still exists anywhere in the suite. This is exactly the kind of
defect that's easy to miss by reading code and only surfaces by tracing
which seam each production method actually depends on.

## What is still genuinely open — do not re-flag these as new findings

- **Literal 100% line/branch coverage is not achieved.** Method coverage is
  100% (463/463); line/branch gaps remain, concentrated in
  `MainForm.MainWorkflow.cs`, `MainForm.Recovery.cs`, and the
  `AssetProcessorService.*` partial classes. RISK 1–5's fixes closed the
  *specific* branches you named, not the files' overall totals.
- **Mutation testing has still not been run against the widened scope.**
  `stryker-config.json` mutates `**/*.cs` (except `MainForm.Designer.cs`) but
  the actual survivor count for that scope is unverified — this remains
  `NOT VERIFIABLE HERE` for you too, since you cannot execute Stryker.
- The 20× flakiness loop, `win-x64` publish, and packaged startup smoke *are*
  now confirmed green on real CI for the merged v1.3.1 commit (previously
  pending/unproven in your `a.md` audit) — you can treat those as verified,
  not as open gaps.

## Environment restrictions — please read carefully

You are a web chat with repository read access. You **cannot**:

- **Run the application.** It is a Windows GUI app, and the published binary
  is unsigned, so Smart App Control blocks it. Do not report "could not
  launch the exe" as a defect.
- **Trigger GitHub Actions runs.** No Actions credits remain this month.
  Read the *existing*, already-completed run logs linked from the PRs/release
  instead of requesting new ones.
- **Execute tests, builds, coverage, or mutation runs locally.** No compiler
  or .NET SDK is available to you.
- **Push, commit, merge, or open PRs.**

You **can and should**: read every source and test file, read committed CI
configuration and logged run results, reason about correctness statically,
and trace each production path to the test that exercises it.

## How to report

Use exactly these three categories, as before:

- **`DEFECT`** — you can point at specific code and explain a concrete
  failure: inputs/state → wrong behavior. Include the mechanism.
- **`RISK`** — under-tested or fragile-looking code where you cannot
  demonstrate a failure. Say what would confirm or refute it.
- **`NOT VERIFIABLE HERE`** — requires running something you cannot run.
  Never score these against the verdict.

Please also:

1. **Enumerate before asserting counts** (file:line, not estimates).
2. **Verify the new seams are minimal and honest** — `code-coverage-exclusions.json`
   should still list exactly the methods that wrap one call to a blocking
   dialog, the message loop, or the DPI initializer. Flag anything broader.
3. **Audit test quality, not just presence** — this is where you add the
   most value. Look for tests that execute code without meaningfully
   asserting on it, the same pattern RISK 6–9 caught last time.
4. Report in English, most-severe first.
