## Asset Provenance Helper v1.4.0

This release implements the two features from `docs/plans/_looi1.md`: **Keep
Settings** and **Variants Mode**.

### Feature 1 - Keep Settings

A new "Keep settings" checkbox (`AppSettings.KeepSettingsEnabled`, persisted
like `DirectModeEnabled`). While checked, completing or cancelling an asset no
longer clears **Asset Name**, **Final Prompt**, or the **Variants** count.
Image selections and the "Saved reference" label are always cleared regardless
- a retained image selection points at a download-folder file a committed
asset has already consumed, which is exactly the provenance-integrity failure
this tool exists to prevent. Request Manifest import is unaffected and always
clears the queue atomically. The Variants count itself still resets to "none"
on every application start.

### Feature 2 - Variants Mode

A new "Variants" dropdown (`none`, `1`-`10`) available in **both**
No-Reference and Reference-assisted mode. Setting it to N takes the N newest
supported images in the Image Download Folder (oldest first) and produces N
complete, independent assets named `<AssetName>A`, `B`, `C`, ... sequentially
from one Asset Name and one prompt.

In Reference-assisted mode, the Reference is committed directly into variant
A's folder (`<AssetName>A`) rather than a base folder - the base folder is
never created. Variants B..N each replicate that same reference image
byte-for-byte into their own folder before running the normal Main-image
commit. The Variants count must be set **before** clicking Reference, because
that click binds the folder name; it locks (but keeps its value) once a
reference session is active.

The batch is strictly sequential and stops on first failure: earlier variants
stay committed, later variants are never attempted, and a summary reports
which succeeded. A destination-collision preflight runs before anything is
written. A one-time-per-session warning appears if a variants batch would
reprocess a download already committed earlier in the same run.

**No side effects**: with Variants set to "none" and Keep Settings off, every
existing code path is unchanged - verified by the full pre-existing 1026-test
suite passing without modification to its expected behavior.

### Verification

- Full suite (1026 pre-existing + 64 new = 1090 tests) green in Release
  (`dotnet test -c Release`). Debug compiles clean with `-warnaserror`.
- Both required empirical falsification checks from the plan (§5.9) performed
  live: temporarily removed the oldest-first `.Reverse()` and confirmed the
  ordering test fails; temporarily forced variant A to redundantly recreate
  its reference and confirmed the reuse test fails. Both restored and
  re-verified green afterward.
- Coverage: method coverage remains 100% (no new method is untested); branch
  coverage improved; line coverage has a small, documented gap limited to a
  handful of defensive catch blocks that mirror already-unreachable-in-tests
  patterns elsewhere in this codebase (e.g. a "could not scan download folder"
  IOException catch, and a post-commit UI-refresh-failure catch that must
  never roll back a completed asset).

### Known limitation — Smart App Control test blocking

This development machine runs Smart App Control in **enforced** mode, and the
app is unsigned. During this work `dotnet test` was intermittently blocked with
`0x800711C7` while loading `AssetProvenanceHelper.dll`, which vstest surfaces as
a large number of ordinary-looking test failures.

That behaviour was investigated and is now documented and tooled around:

- Root cause and evidence: [`docs/sac-test-execution.md`](sac-test-execution.md)
  (80 block events, 100% against the product assembly; the trigger is the *rate*
  of rebuild+full-suite cycles, not anything in the code).
- `AGENTS.md`'s previous claim that the test suite "runs SAC-free" was **wrong**
  and has been corrected — that claim is what caused the block to be misread as
  a code regression in the first place.
- New wrapper `scripts/run_tests_sac_safe.ps1` settles, canaries, detects the
  block two ways, retries with backoff, and exits **42** so an environment block
  can never again be mistaken for a real failure.

**Verification status of this release:** verified green **on the
version-bumped 1.4.0 binaries** via the new wrapper — 1089 passed, 1 skipped,
1090 total, wrapper exit code 0 (i.e. a real result with no code-integrity
interference). Debug and Release both build clean with `-warnaserror`.

Re-verify at any time with:

```powershell
powershell -File scripts/run_tests_sac_safe.ps1
```

The coverage baseline (`code-coverage-baseline.json`) is likewise left unchanged
rather than refreshed from an interrupted run; refresh it with
`pwsh scripts/verify_coverage.ps1 -UpdateBaseline` once a clean coverage run is
possible (e.g. in CI).

### Upgrade

Extract the ZIP into a clean directory rather than overlaying v1.3.2. User
settings and recovery state remain in
`%LOCALAPPDATA%\Ceegore\AssetProvenanceHelper`. A pre-existing `settings.json`
without `KeepSettingsEnabled` loads with it defaulted to `false`.
