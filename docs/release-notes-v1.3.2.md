## Asset Provenance Helper v1.3.2

This release fixes every issue from a second independent verification audit of
v1.3.1 (`docs/audits/b.md`): the one `DEFECT` finding and both `RISK` items.

### Fixes

**DEFECT 1 (MEDIUM)** - three Request-queue tests reached the **real Windows
clipboard** because `MainForm.ClipboardWriter` was never seamed:
`Queue_MouseUpOnEmptyAreaDoesNothing` and
`Queue_ProgressSaveFailureDoesNotBreakCompletion` incidentally activate a real
queue row as a side effect of testing something else - `TryCopyPromptToClipboard`
falls through to `Clipboard.SetText` when `ClipboardWriter` is null, clobbering
whatever the developer or CI runner had on the clipboard. A transient clipboard
failure on that path can additionally fall through to a real `MessageBox.Show`.
`Queue_RealClipboardWriteWhenNoWriterHook` deliberately exercises the real
fallback and clears the clipboard in `finally` without restoring the original
content.

Seamed `ClipboardWriter` in the two incidental tests. The deliberate test can't
be meaningfully seamed without testing nothing (its whole point is proving the
real-OS fallback works), so it's now marked `[Fact(Skip = ...)]` with a clear
rationale, rather than forcing an artificial platform-adapter indirection
around the one line that must call the real Clipboard API.

**RISK 1 (MEDIUM)** - `REG_020`'s failure injection (locking `settings.json`
with an exclusive file lock) didn't actually prove `SettingsService.Save()`
took the atomic temp-then-promote path: a naive `File.WriteAllText` would fail
to open the locked destination and satisfy every assertion just as easily.
Added `SettingsService.OnAfterTempFlushedBeforePromoteHook`, which only fires
once the new content is genuinely written and durably flushed to a real temp
file, and rewrote the test around it. **Verified empirically**: temporarily
replaced `Save()` with a naive direct write and confirmed the new test
correctly fails against it, then restored the real implementation and
confirmed it passes.

**RISK 2 (LOW-MEDIUM)** - the six synthetic coverage-ratchet regression
scenarios (`scripts/verify_coverage.ratchet.tests.ps1`) existed but were never
invoked by CI, so a future regression in the ratchet comparison logic itself
could have survived normal CI undetected. Added as a step to both `ci.yml`'s
Coverage Gate job and `zero-exception-audit.yml`.

### Verification

- Verified from a **clean working tree** via `scripts/verify_like_ci.ps1`:
  Debug and Release builds with `-warnaserror` (0 warnings, 0 errors each),
  full test suite 1025/1026 passed (1 test intentionally skipped - see DEFECT
  1 above), `Category=RecoveryCritical` passed, no test-triggered dialogs.
- Windows CI (build/test/flakiness/publish/smoke + coverage gate, including
  the newly-wired ratchet unit scenarios): both jobs green on the exact merged
  commit.

### Known limitation

The application executable is **unsigned**. Windows Smart App Control blocks
the self-contained published `.exe` by default; run via
`dotnet AssetProvenanceHelper.dll` (the signed host) or via
`scripts/run_smoke_tests_sac_safe.ps1` to verify startup on a SAC-enabled
machine. This is an environment constraint, not a product defect - see
`AGENTS.md` for the full explanation.

### Upgrade

Extract the ZIP into a clean directory rather than overlaying v1.3.1. User
settings and recovery state remain in
`%LOCALAPPDATA%\Ceegore\AssetProvenanceHelper`.
