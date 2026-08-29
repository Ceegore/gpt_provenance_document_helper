# vv1 — Paranoid release-readiness, safety, provenance-integrity, and QA audit

**Audit date:** 2026-08-23  
**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Audited branch:** `main`  
**Audited code commit:** `c5029f0357959fe54be4cc8d98c4c76abd8da23a`  
**Audited tree:** `64a8d006dd3b831b7af494e693bf50004914cfdd`  
**Previous in-repository audit baseline:** `bugs15(1).md`

---

# 0. Executive verdict

## 0.1 Release decision

**FAIL — NOT READY FOR “RISK-FREE” OR TRUSTED PROVENANCE USE.**

No non-trivial desktop program can truthfully be called completely risk-free. More importantly, this exact revision still has several concrete release blockers and material residual risks.

The underlying file-transaction architecture is substantially stronger than a normal small utility. The current implementation has extensive fail-closed behavior, SHA-256 ownership checks, no-overwrite moves, transaction journals, crash recovery, reparse-point defenses, exact provenance checks, and many dedicated regression tests. I did **not** find an obvious normal-path primitive that simply overwrites or recursively deletes arbitrary user data.

However, the application should **not** yet be treated as a trustworthy production provenance recorder or as safe enough for irreplaceable/unbacked-up asset folders. The most important reasons are:

1. **The provenance templates automatically assert facts the program cannot know or prove**, including `Human review: yes`, `IP / trademark review: yes`, `Release approved: yes`, `Status: approved`, source/origin facts, and other workflow facts. This can generate a materially inaccurate provenance record.
2. **“Generation date” is actually the date the user processes the file in this helper**, not necessarily the date on which the image was generated.
3. **The repository hard-pins .NET SDK 8.0.418 / runtime 8.0.24 for a self-contained release**, while the current August 2026 .NET 8 servicing release is 8.0.30 / SDK 8.0.424 and contains multiple security fixes.
4. **Recovery state and the single-instance mutex are tied to the executable directory.** Running another copy from a different folder creates a separate recovery authority and a different mutex, so two copies can operate concurrently against the same asset root; moving/upgrading the portable app can also strand an interrupted session in the old directory.
5. **Hash-then-mutate destructive operations retain a narrow TOCTOU race**, amplified by `ComputeSha256` opening files with `FileShare.ReadWrite | FileShare.Delete`.
6. **There is no successful required-check evidence attached to the audited HEAD through the connected GitHub status surface, `main` is unprotected, and the commit is unsigned.** The workflow exists, but release acceptance must be based on an observed pass for the exact release commit, not merely on workflow definitions.
7. The coverage and smoke gates are weaker than their names imply: the coverage gate has no numeric coverage threshold, mutation coverage omits critical `MainForm.*` workflow/recovery partial files, and the packaged smoke test never creates/cancels/replaces/recovers an asset.

### Recommended use right now

- **Do not rely on generated `.md` files as authoritative legal/compliance provenance records yet.**
- **Do not use the current self-contained build baseline as a release build.**
- If evaluating the UI manually, use only disposable/copied test data and keep an external backup of the asset root.
- Do not run two copies of the application from different directories against the same asset root.

The tool can become release-ready, but the blockers below should be repaired and then revalidated on the exact release commit.

---

# 0.2 Audit scope and evidence

This audit covered:

- the complete repository tree at the audited commit;
- application bootstrap/state placement;
- settings and session persistence;
- Reference creation, replacement, cancellation, and recovery flows;
- Main/no-reference commit and rollback flows;
- path validation and reparse-point defenses;
- hash ownership and mutation boundaries;
- provenance template rendering and semantic correctness;
- image input validation;
- test-project structure and major regression suites;
- CI, coverage, mutation, packaging, and smoke workflows;
- branch/status/signing state visible through connected GitHub;
- current official OpenAI Europe Terms wording/date;
- current official .NET 8 servicing and support state.

### Independent execution limitation

I attempted to obtain an independent local checkout for a fresh `dotnet build` / `dotnet test` / publish run, but the isolated execution environment could not resolve `github.com`. The connected GitHub integration exposes the source and repository metadata but does not expose an action here that can start a new workflow run. Therefore this report **does not claim that I independently executed the test binary**.

That limitation matters: static/source-path validation is strong evidence for design defects, but it is not a replacement for an exact-commit Windows build/test/publish result. The connected GitHub combined-status query for the audited HEAD returned no statuses, so a clean exact-HEAD CI result could not be used as release evidence in this audit.

---

# 0.3 What is already strong and should be preserved

Do **not** simplify away these mechanisms while repairing the findings:

- atomic-ish write-to-temp + durable flush + move for session/journal JSON;
- deterministic transaction identifiers and temp paths;
- no-overwrite canonical promotion;
- SHA-256 ownership authority before destructive cleanup;
- exact provenance ownership validation;
- fail-closed behavior when ownership cannot be proven;
- explicit Reference/Main/cancellation/replacement transaction phases;
- startup recovery before normal work;
- preservation of files when a session record is untrusted;
- direct-child asset-folder validation;
- Windows reserved-name handling;
- reparse-point rejection on critical asset/reference/ingame folders;
- dedicated RecoveryCritical and paranoid regression tests;
- repeated test execution for flakiness detection;
- Stryker mutation threshold (`break: 80`).

The latest `c5029f0...` source also appears to implement the three previously reported R15 repairs: recovery cloning/durable OLD provenance authority, cancellation boundary separation, and legacy raw provenance hash handling.

---

# 1. Finding summary

| ID | Severity | Release blocker | Area | Summary |
|---|---:|:---:|---|---|
| **V1-001** | **CRITICAL** | **YES** | provenance semantics | Templates hard-code unverified review, approval, origin, third-party-input, and usage claims as facts |
| **V1-002** | **HIGH** | **YES** | provenance timestamps | `Generation date` is populated from helper processing time, not proven generation time |
| **V1-003** | **HIGH** | **YES** | runtime security | Self-contained release is pinned to .NET SDK 8.0.418 / runtime 8.0.24 instead of current security-serviced 8.0.30 / SDK 8.0.424 |
| **V1-004** | **HIGH** | **YES** | recovery/concurrency | Settings/session/replacement journal and mutex are installation-directory scoped; another copy can run with independent recovery state |
| **V1-005** | **MEDIUM-HIGH** | **YES for high-assurance release** | file mutation race | Hash/path authority is checked before mutation, but files remain replaceable between check and move/delete |
| **V1-006** | **MEDIUM-HIGH** | **YES** | release governance | No exact-HEAD status evidence was available; `main` is unprotected and audited commit is unsigned |
| **V1-007** | **MEDIUM** | **YES** | QA gates | Coverage gate has no minimum rate; mutation selection misses critical `MainForm.*` workflow/recovery partials |
| **V1-008** | **MEDIUM** | **YES** | packaged smoke/E2E | Smoke test checks startup/title/icon/shutdown but not a real asset transaction or recovery; it also uses uninitialized state variables |
| **V1-009** | **MEDIUM** | SHOULD | input integrity | Image validation checks extension/length/magic bytes, not complete decodability/container validity |
| **V1-010** | **MEDIUM** | SHOULD | version/release provenance | Product remains `1.1.0`; materially different builds can produce the same archive name/version |
| **V1-011** | **MEDIUM** | SHOULD | supply chain | Actions are tag-pinned, not SHA-pinned; no release attestation/SBOM/code-signing gate is visible |
| **V1-012** | **LOW-MEDIUM** | depends on distribution | legal/repository hygiene | No `LICENSE` file; replacement journal is not explicitly ignored |

---

# 2. V1-001 — CRITICAL — provenance files can assert facts that were never verified

## Evidence

`src/AssetProvenanceHelper/templates/final.md` hard-codes, among other statements:

```text
Generator: OpenAI ChatGPT
Third-party visual reference: None known / none used.
Generation conversation retained: no
Final use: Commercial video game asset
Store asset: no
Human review: yes
IP / trademark review: yes
Release approved: yes
Status: approved
```

`src/AssetProvenanceHelper/templates/final_no_reference.md` similarly hard-codes:

```text
Generator: OpenAI ChatGPT
Reference image used for the final generation: No.
Generation conversation retained: no
Final use: Commercial video game asset
Store asset: no
Human review: yes
IP / trademark review: yes
Release approved: yes
Status: approved
```

`src/AssetProvenanceHelper/templates/reference.md` hard-codes:

```text
Generator: OpenAI ChatGPT
Text input only for the original generation.
No third-party source image was used ...
Prompt retained: no
Generation conversation retained: no
Commercial project use: yes
Human review: yes
IP / trademark review: yes
Status: approved
```

The application verifies that an input file looks like a supported image, but it has no mechanism that proves the image was generated by ChatGPT, proves whether a third-party reference was used, checks whether a conversation was retained, performs an IP/trademark review, or records an explicit approval action corresponding to these statements.

## Impact

This is not cosmetic wording. The central product purpose is to create provenance/rights records. A record that automatically says an IP/trademark review occurred when it did not occur can be worse than having no record, because it creates false confidence and weakens evidentiary value.

The same problem exists even if the primary operator normally follows the intended workflow: the software should record **what was actually declared/verified for this asset**, not what is usually expected to be true.

## Current legal wording check

As of this audit, the ownership sentence itself is aligned with the current OpenAI Europe Terms of Use updated **2026-01-16**: as between the user and OpenAI and to the extent permitted by law, the user owns Output and OpenAI assigns its right/title/interest, if any, in Output.

However, the Europe Terms also state that Business Terms govern ChatGPT Enterprise, APIs, and other business/developer services. Therefore a hard-coded `OpenAI Europe Terms of Use` basis is not universally correct for every possible OpenAI workflow.

Official current terms: https://openai.com/policies/eu-terms-of-use/

## Required fix

Introduce a first-class provenance-facts model instead of encoding assumptions in templates.

Suggested model shape:

```text
Provider: OpenAI / Other
Service: ChatGPT individual / ChatGPT Business / Enterprise / API / Other
Jurisdiction/terms profile: Europe consumer / Business / Other
Generation method: text-to-image / reference-assisted / other
Reference used: yes / no / unknown
Third-party visual input used: yes / no / unknown
Prompt retained: yes / no / unknown
Conversation retained: yes / no / unknown
Human review completed: yes / no
IP/trademark review completed: yes / no
Release approval: draft / reviewed / approved / rejected
Intended use: free text or controlled selection
Store asset: yes / no / not applicable
```

Rules:

1. **Default every evidentiary claim to `unknown`, `not recorded`, or `draft`, never `yes`.**
2. Require explicit user action before recording `Human review: yes`, `IP / trademark review: yes`, or `Release approved: yes`.
3. Distinguish `user-declared` facts from tool-verifiable facts.
4. Never infer generator/provider merely from the filename or image bytes.
5. If this application is intentionally ChatGPT-only, require an explicit per-asset declaration such as `I confirm this asset is OpenAI ChatGPT Output` and record that as a declaration, not as independently verified fact.
6. Make terms profile/service type explicit. API/Enterprise/Business output must not automatically cite individual Europe Terms.
7. Include the terms URL and terms updated date actually selected for that record.
8. Consider storing a SHA-256 of a retained local terms snapshot if legal/audit durability is a goal.
9. Render unknown values truthfully, e.g. `IP / trademark review: not recorded`, not optimistically.
10. Split **provenance facts** from **release-review attestations**. A generated image can have provenance without being release-approved.

## Required tests

Add table-driven tests covering every tri-state / status combination and asserting that:

- no `yes` or `approved` appears unless explicitly set;
- `unknown` survives save/reload/recovery;
- Reference and NoReference modes render only facts relevant to their mode;
- service/terms profile mismatch is rejected;
- old sessions migrate conservatively to `unknown`, never to `yes`;
- exact provenance ownership/hash checks still work after schema/template changes.

## Acceptance criterion

A user must be unable to create a provenance file containing `IP / trademark review: yes` or `Release approved: yes` without an explicit corresponding user declaration/action in the current asset workflow.

---

# 3. V1-002 — HIGH — “Generation date” is actually helper processing time

## Evidence

The UI uses `DateTimeOffset.Now` when the helper processes a selected image:

- `MainForm.MainWorkflow.cs`: `processedAt = DateTimeOffset.Now`
- `MainForm.ReferenceWorkflow.cs`: `now = DateTimeOffset.Now`

The templates then render that value under the label:

```text
Generation date: {{GENERATION_DATE}}
```

The program does not retrieve the image’s actual ChatGPT generation timestamp.

## Impact

If a user downloads an image on Monday and records it with the helper on Friday, the provenance file says the image was **generated Friday**. That is an inaccurate historical statement.

Using only `yyyy-MM-dd` also loses time and offset evidence.

## Required fix

Store at least two separate timestamps:

```text
Record created at: <actual helper timestamp, ISO-8601 with offset>
Generation time/date: <user-declared or imported authoritative value, or "unknown/not recorded">
```

Do **not** silently use file creation/modified time as generation time; those values can change during download/copy and are not authoritative.

Recommended behavior:

- default `Generation date/time` to `not recorded`;
- optionally let the user enter/confirm the actual generation date/time;
- store `RecordedAt` automatically with full offset;
- store a flag identifying generation timestamp provenance: `user-declared`, `service-export`, or `unknown`.

## Acceptance criterion

Processing an old image today must not cause the provenance record to claim that today was its generation date unless the user explicitly confirms that fact.

---

# 4. V1-003 — HIGH — release toolchain embeds an outdated .NET 8 security baseline

## Evidence

`global.json`:

```json
{
  "sdk": {
    "version": "8.0.418",
    "rollForward": "disable"
  }
}
```

`.github/workflows/ci.yml` also installs:

```yaml
dotnet-version: '8.0.418'
```

and publishes:

```text
-r win-x64 --self-contained true
```

Microsoft’s .NET 8 release notes identify SDK **8.0.418** as the SDK that contains runtime **8.0.24** (2026-02-10).

The current August servicing release is **.NET 8.0.30 / SDK 8.0.424** (2026-08-11). Microsoft states that 8.0.30 carries security and non-security fixes and lists multiple CVEs, including remote-code-execution and elevation-of-privilege vulnerabilities.

Official release note: https://github.com/dotnet/core/blob/main/release-notes/8.0/8.0.30/8.0.30.md

.NET 8 is also scheduled to leave support on **2026-11-10**. Official support policy: https://dotnet.microsoft.com/en-us/platform/support/policy

## Impact

Because the project publishes self-contained, the release package carries its runtime rather than automatically inheriting the user machine’s latest installed .NET servicing patch. A release process deliberately pinned to an old SDK/runtime is therefore unacceptable for a new security-conscious release.

## Required fix

Immediate minimum repair:

1. Update `global.json` to `8.0.424`.
2. Update all CI/mutation workflow SDK references to `8.0.424`.
3. Keep `rollForward: disable` only if reproducibility is desired **and** there is an automated monthly servicing update process.
4. Rebuild from scratch and verify the published runtime version.
5. Add a CI check that fails if the SDK/runtime is below the approved security floor.

Strategic repair:

- migrate the application and tests to **.NET 10 LTS** (`net10.0-windows`) before .NET 8 support ends;
- run the full regression/recovery/mutation suite under .NET 10;
- publish a new major/minor version rather than silently replacing a 1.1.0 binary.

## Acceptance criterion

The release artifact must be built by a currently supported and currently serviced SDK/runtime, and CI must prove the exact SDK/runtime versions embedded in the self-contained package.

---

# 5. V1-004 — HIGH — recovery authority and mutex are scoped to executable directory

## Evidence

`AppBootstrap.GetSettingsPath()` and `GetSessionPath()` place mutable state under `baseDirectory`.

`Program.cs` supplies `AppContext.BaseDirectory`.

The replacement journal is placed next to `session.json`.

`BuildSingleInstanceMutexName(baseDirectory)` hashes the executable base directory into the mutex name.

Consequently:

```text
C:\Tool-v1\AssetProvenanceHelper.exe
C:\Tool-v2\AssetProvenanceHelper.exe
```

receive different mutexes and different session/replacement-journal locations even if both are configured for the exact same asset root.

## Impact

1. **Portable upgrade/re-extraction risk:** an interrupted transaction journal can remain in the old application folder while the user starts a new copy from a new folder. The new copy does not automatically see or recover the old transaction.
2. **Concurrent-instance risk:** two copies from different directories can run simultaneously and target the same asset root.
3. **Read-only install risk:** placing the app under `Program Files` or another non-writable directory can prevent durable settings/session persistence.
4. The transaction design assumes the current session journal is the durable authority. Multiple independent authorities undermine that assumption.

## Required fix

Move mutable state to a stable per-user data directory, for example:

```text
%LOCALAPPDATA%\Ceegore\AssetProvenanceHelper\
  settings.json
  session.json
  reference-replacement.json
  logs\
```

Keep read-only shipped templates under the application directory.

Add migration:

1. On first launch, if stable-state files do not exist, inspect the current legacy executable folder for old state.
2. Validate legacy session/journal before moving/copying it.
3. Never silently choose between two conflicting active journals; fail closed and show both paths.
4. Persist a schema/version field in state.

Change single-instance handling:

- use a stable per-user mutex name independent of install directory; and
- preferably add an **asset-root lock** acquired before any session that can mutate an asset root.

The asset-root lock should be derived from the canonical normalized root path and held for the full session/transaction lifetime. It should fail cleanly with a message identifying that another helper instance owns the root.

## Required tests

- run two application copies from different directories;
- point both at the same root and confirm the second cannot start a mutation session;
- crash v1 in a transaction, start v2 from another directory, and prove v2 discovers/reconciles the stable journal;
- run from a read-only install directory as a non-admin user;
- migration with valid, invalid, and conflicting legacy journals.

---

# 6. V1-005 — MEDIUM-HIGH — destructive hash checks still have a TOCTOU window

## Evidence

`ValidationService.ComputeSha256()` opens a file using:

```csharp
FileShare.ReadWrite | FileShare.Delete
```

Destructive helpers generally follow this shape:

```text
path/reparse checks
hash file
close hash handle
File.Move(...) or File.Delete(...)
```

The implementation often performs excellent last-moment rechecks and invokes race-test hooks, but there is still a small interval after the final authority check in which another local process can replace/change the path before the mutation occurs.

## Impact

In a normal single-user/single-instance desktop environment this race is difficult to hit accidentally. It becomes more realistic when V1-004 allows two copies to operate concurrently, and it remains a correctness gap against an adversarial local process.

## Required fix

Use layered mitigation:

1. Fix V1-004 first: one stable instance + one exclusive asset-root authority.
2. Stop using `FileShare.ReadWrite | FileShare.Delete` for ownership checks immediately preceding destructive operations unless a specific recovery reason requires it.
3. For deletion, prefer an ownership-checked atomic rename into a transaction-specific quarantine name, then delete the renamed object after authority is bound to that object.
4. On Windows, consider binding identity using a file handle/file ID where practical rather than relying solely on pathname identity.
5. Keep reparse-point checks at the mutation boundary.
6. Keep `overwrite:false` moves.
7. Add adversarial race tests that replace a file after hash verification and before the mutation boundary.

## Acceptance criterion

A second process replacing a same-named file during a destructive operation must cause fail-closed preservation, not deletion/movement of the replacement file.

---

# 7. V1-006 — MEDIUM-HIGH — release governance cannot prove this exact HEAD is accepted

## Evidence

For audited `main`:

```text
HEAD: c5029f0357959fe54be4cc8d98c4c76abd8da23a
commit verification: unsigned
branch protection: disabled
required status checks: none
```

The connected combined-status query returned no statuses for the audited HEAD.

The repository contains a CI workflow, but a workflow file is not evidence that the exact release commit passed it.

## Impact

A direct push can change `main` without any required build/test gate. A release binary can therefore be built from a commit with no enforced test evidence.

## Required fix

1. Protect `main`.
2. Require pull requests for code changes.
3. Require exact-commit passing checks for Windows Build & Test and Coverage Gate.
4. Add a release gate requiring current mutation evidence for the release candidate.
5. Require the branch to be up to date before merge.
6. Create releases only from signed/annotated version tags pointing to a commit whose required checks passed.
7. Generate release checksums and GitHub artifact/build provenance attestations.
8. Prefer signed commits/tags for release-authority commits.

## Acceptance criterion

It must be impossible through normal repository permissions to mark/publish a release from a commit for which the mandatory release checks are absent or failing.

---

# 8. V1-007 — MEDIUM — coverage and mutation gates miss important risk

## 8.1 Coverage gate has no numeric floor

The CI reads and prints line/branch rates but only fails when:

```text
lines-valid <= 0 OR branches-valid <= 0
```

It then verifies that selected production class/file entries exist in the Cobertura report.

It does **not** fail if line or branch coverage drops from, for example, 95% to 20%.

For partial classes, the required entries name base files such as:

```text
Services\AssetProcessorService.cs
Services\ValidationService.cs
MainForm.cs
```

Critical logic actually lives heavily in:

```text
AssetProcessorService.Main.cs
AssetProcessorService.Reference.cs
AssetProcessorService.FileOps.cs
ValidationService.Paths.cs
ValidationService.Session.cs
MainForm.MainWorkflow.cs
MainForm.ReferenceWorkflow.cs
MainForm.Recovery.cs
```

Presence of the base class entry does not enforce adequate coverage of those critical partial files.

## 8.2 Mutation selection misses critical MainForm partials

Stryker is configured with a useful `break: 80` threshold, but `mutate` includes:

```text
**/Services/*.cs
**/Models/*.cs
**/MainForm.cs
```

That includes service partials, but **not** `MainForm.MainWorkflow.cs`, `MainForm.ReferenceWorkflow.cs`, `MainForm.Recovery.cs`, or `MainForm.ImageSelection.cs`.

The recovery orchestration is one of the most safety-critical parts of the program.

Mutation runs are also weekly/manual rather than a direct required release-candidate check.

## Required fix

Suggested baseline for this safety-sensitive utility:

- overall line coverage: **>= 90%**;
- overall branch coverage: **>= 85%**;
- explicit critical-file coverage thresholds for recovery/destructive-operation files, ideally >= 95% line / >= 90% branch;
- Stryker `break >= 80` retained;
- include `MainForm.MainWorkflow.cs`, `MainForm.ReferenceWorkflow.cs`, and `MainForm.Recovery.cs` in mutation scope;
- mutation report for the exact release candidate must exist and pass before release.

Do not chase percentages with meaningless tests; add branch/fault-injection cases at mutation boundaries.

---

# 9. V1-008 — MEDIUM — packaged smoke test is not a workflow smoke test

## Evidence

`scripts/run_smoke_tests.ps1` verifies:

- executable exists and hashes it;
- templates exist;
- selected runtime DLLs exist;
- process starts;
- expected main-window title appears;
- icon can be extracted;
- app closes gracefully;
- release ZIP can be created and hashed.

It does **not** execute:

- Reference creation;
- Main creation;
- NoReference creation;
- replacement;
- cancellation;
- session recovery;
- replacement-journal recovery;
- real provenance output validation;
- permission failure;
- path/reparse failure;
- dual-instance prevention.

The current script also uses `$elapsedMs`, `$windowTitle`, and `$hasWindow` before explicit initialization. PowerShell’s normal loose variable behavior masks this today, but the script is fragile and would fail or behave differently under strict mode.

## Required fix

Immediate script repair:

```powershell
Set-StrictMode -Version Latest
$elapsedMs = 0
$windowTitle = ""
$hasWindow = $false
```

Then add a **published-package functional smoke** using a temporary workspace and tiny valid fixture images.

Minimum release smoke matrix:

1. launch clean package as non-admin;
2. set temp Download and Asset Root;
3. Reference-assisted: create Reference -> create Main -> validate exact output tree + hashes + provenance;
4. NoReference: create Main -> validate output tree + hashes + provenance;
5. create Reference -> replace Reference -> validate old backup cleanup and new authority;
6. create Reference -> cancel -> validate only tool-owned artifacts are removed;
7. force-kill at each durable phase hook/journal phase -> relaunch -> prove rollback/commit-forward invariants;
8. run path containing spaces and Unicode;
9. run executable directory read-only while state directory remains writable;
10. launch a second copy and prove it cannot mutate the same root.

The existing 20x unit-test flakiness loop is useful and should remain.

---

# 10. V1-009 — MEDIUM — accepted “image” may be structurally invalid

## Evidence

`ValidateImageFile()` checks:

- file exists;
- configured extension;
- non-zero size;
- PNG/JPEG/WebP header magic bytes.

It does not fully parse/decode the image/container.

A file with a valid first 8–12 bytes and truncated/corrupt remainder can pass this validation and be recorded/copied as a production asset.

## Impact

This is primarily an integrity/quality issue rather than an arbitrary-code issue in the current code path, because the helper mostly copies/hashes the file rather than decoding it. But the output may later be unusable by the game/toolchain while the provenance record says the asset is complete/approved.

## Required fix

- fully validate supported image container structure or decode the image using a well-maintained Windows-capable decoder;
- enforce sane maximum file size and dimensions if decoding is introduced, to reduce decompression-bomb/resource risks;
- reject truncated PNG, JPEG, and WebP fixtures with valid magic bytes;
- preserve exact source bytes; validation must not re-encode the production asset.

---

# 11. V1-010 — MEDIUM — release version is not uniquely tied to code

## Evidence

`AssetProvenanceHelper.csproj` contains:

```xml
<Version>1.1.0</Version>
```

The smoke script derives the release ZIP name from the executable ProductVersion:

```text
AssetProvenanceHelper-v1.1.0-win-x64.zip
```

The repository has undergone many substantial safety-fix commits while retaining this product version.

## Impact

Different binaries can share the same human-visible version and archive filename. That makes bug reports, rollback, audit evidence, and provenance-tool trust much harder.

## Required fix

- derive release version from an immutable signed Git tag;
- embed commit SHA in `AssemblyInformationalVersion`;
- create a release manifest containing:

```text
ProductVersion
CommitSha
Build timestamp
SDK version
Embedded runtime version
EXE SHA-256
ZIP SHA-256
reference.md SHA-256
final.md SHA-256
final_no_reference.md SHA-256
CI run identifier
```

- never overwrite a published release artifact with a different binary under the same version.

---

# 12. V1-011 — MEDIUM — supply-chain hardening is incomplete

## Evidence

Workflows reference mutable major-version tags such as:

```yaml
actions/checkout@v4
actions/setup-dotnet@v4
actions/upload-artifact@v4
```

No source-tree workflow for SBOM generation, build-provenance attestation, or Authenticode signing is present.

## Required fix

- pin GitHub Actions to full audited commit SHAs;
- enable Dependabot/Renovate for action and NuGet/tool updates;
- produce SPDX or CycloneDX SBOM for the self-contained package;
- use GitHub artifact attestations/build provenance for release artifacts;
- publish SHA-256 checksums;
- if distributing the executable to other Windows users, strongly consider Authenticode signing;
- add CodeQL or another static security scan as defense in depth.

This project has very few runtime third-party package dependencies, which is a positive supply-chain property; preserve that simplicity.

---

# 13. V1-012 — LOW-MEDIUM — repository legal/runtime hygiene

## 13.1 No LICENSE file

The audited tree contains no `LICENSE`, `LICENSE.md`, or equivalent software license file.

For the repository owner this does not prevent use of their own code. For external users/contributors/distribution, however, a public GitHub repository without an explicit software license does not clearly grant normal reuse/modification/distribution rights.

**Fix:** add the intended license before inviting external reuse/distribution.

## 13.2 Replacement journal is not explicitly ignored

`.gitignore` includes `settings.json` and `session.json`, but does not explicitly include the replacement journal filename (`reference-replacement.json`). Normal build/run locations are generally already under ignored `bin/`/artifact paths, so this is low severity, but runtime authority files should be explicitly excluded.

**Fix:** add the replacement journal name/pattern and transaction temp journal patterns to `.gitignore`.

---

# 14. Release-blocking implementation plan

The following order minimizes the chance of fixing symptoms while leaving an architectural hazard underneath.

## PHASE A — provenance truthfulness

### A1. Add explicit provenance facts

Create a versioned model, e.g. `ProvenanceRecordInput` / `ReviewAttestations`, with tri-state values and explicit service/terms profile.

### A2. Split recording timestamp from generation timestamp

Add:

```text
RecordedAt
GenerationAt? / GenerationDate?
GenerationTimestampSource
```

Do not infer generation time from file filesystem timestamps.

### A3. Rework templates

Remove all unearned hard-coded `yes` / `approved` claims. Render facts from the model. Mark declarations as user-declared where appropriate.

### A4. Add schema migration

Old sessions must migrate conservatively. Missing historical fields become `unknown`, not affirmative.

### A5. Tests

Snapshot all template modes and negative cases. Verify exact bytes/hashes and recovery after migration.

**Exit gate:** no generated record can claim review/approval/origin facts that were not explicitly recorded.

---

## PHASE B — runtime and state authority

### B1. Patch current .NET immediately

Update to SDK 8.0.424 / runtime 8.0.30 for the immediate repair branch.

### B2. Move mutable state to LocalAppData

Make the state path stable across application upgrades/re-extraction.

### B3. Stable per-user mutex + asset-root lock

Prevent parallel authorities from different executable folders.

### B4. Legacy-state migration

Fail closed on conflicting journals.

### B5. Harden mutation identity

Reduce file sharing during authority checks and add quarantine/identity-bound destructive operations where feasible.

**Exit gate:** two copies cannot mutate one asset root, and an interrupted transaction is recoverable after launching a newer copy from a different install folder.

---

## PHASE C — QA gate repair

### C1. Numeric coverage thresholds

Add enforceable line/branch floors plus critical-file checks.

### C2. Expand mutation scope

Include Main workflow/reference/recovery partials.

### C3. Fix smoke script strictness

Initialize variables and enable StrictMode.

### C4. Add real packaged functional smoke

Exercise Reference, Main, NoReference, replacement, cancellation, and recovery from the published package/environment.

### C5. Concurrency/race/fault injection

Test:

- second process;
- source mutation;
- canonical-file substitution;
- junction/symlink insertion;
- permission loss;
- disk-full/write failure;
- crash after every persisted phase;
- corrupted/truncated journals;
- corrupt image with valid magic bytes.

**Exit gate:** exact release candidate passes all mandatory QA, not merely unit tests.

---

## PHASE D — release governance and distribution

### D1. Protect `main`

Require exact-commit CI.

### D2. Version uniquely

Use signed tag + commit SHA informational version.

### D3. Pin actions and attest artifacts

Full SHA action pins; checksums; SBOM; build provenance.

### D4. Add software license if distributing

### D5. Migrate to .NET 10 LTS

Do this before .NET 8 EOL on 2026-11-10, preferably before declaring long-lived public release readiness.

**Exit gate:** every downloadable artifact is uniquely attributable to an immutable, fully validated commit and a supported runtime.

---

# 15. Mandatory retest matrix after fixes

A repaired candidate should not be accepted until all of the following pass on the **same immutable release commit**.

## Build/static

- Debug build warnings-as-errors — PASS
- Release build warnings-as-errors — PASS
- analyzer/static security checks — PASS
- templates validate — PASS
- no unsupported runtime package/advisory — PASS
- SDK/runtime security-floor check — PASS

## Unit/regression

- full Debug tests — PASS
- full Release tests — PASS
- RecoveryCritical tests — PASS
- full suite repeated 20x — PASS
- coverage thresholds — PASS
- mutation score >= configured break threshold with critical files included — PASS

## Provenance correctness

- every hard-coded factual assertion removed or explicitly justified — PASS
- unknown values render as unknown/not recorded — PASS
- actual record timestamp distinct from generation timestamp — PASS
- multiline/quote-heavy prompt preserved exactly — PASS
- terms profile reflects selected service/jurisdiction — PASS
- old records/sessions migrate without inventing facts — PASS

## Filesystem safety

- new asset in new folder — PASS
- new asset in existing empty folder — PASS
- collision with foreign root file — FAIL CLOSED
- collision with foreign ingame file — FAIL CLOSED
- foreign provenance collision — FAIL CLOSED
- same-name foreign file substituted at mutation hook — PRESERVED / FAIL CLOSED
- reparse asset root — REJECTED
- reparse asset folder — REJECTED
- reparse reference folder — REJECTED
- reparse ingame folder — REJECTED
- junction inserted after preflight — FAIL CLOSED
- unauthorized folder — FAIL CLOSED
- path with spaces/Unicode — PASS
- reserved device names — REJECTED
- read-only installation directory — PASS with stable external state dir

## Recovery

For **every durable phase boundary**:

- force process termination;
- relaunch from same install directory;
- relaunch from different/new install directory;
- prove exactly one safe outcome: complete commit or complete rollback;
- prove unknown/foreign files are preserved;
- prove journals are deleted only after durable finalization.

Cover:

- initial Reference prepared/promoted;
- Main temp/provenance/root/ingame promotions;
- cancellation Prepared / FilesRenamed;
- replacement Prepared / OldBackupPending / OldBackedUp / NewPromotionPending / NewPromoted / SessionSwitchPending / SessionSwitched / CleanupPending.

## Concurrency

- two copies same install path — second blocked
- two copies different install paths — second blocked
- two copies different asset roots — behavior explicitly defined/tested
- second non-tool process edits owned file during destructive operation — FAIL CLOSED

## Input integrity

- valid PNG/JPEG/WebP — PASS
- zero-byte — REJECT
- extension mismatch — REJECT
- valid magic + truncated body — REJECT
- malformed container — REJECT
- oversized/decompression-bomb case — bounded/rejected

## Packaged Windows validation

Use a clean, non-developer Windows 11 x64 VM/user account:

- unzip release — PASS
- no .NET preinstall dependency — PASS for self-contained release
- launch as standard user — PASS
- complete Reference-assisted workflow — PASS
- complete NoReference workflow — PASS
- cancel — PASS
- replace Reference — PASS
- crash/recover — PASS
- upgrade/re-extract/recover — PASS
- Windows Defender/SmartScreen distribution experience documented
- executable/archive hashes match release manifest
- artifact provenance/attestation verifies

---

# 16. Final acceptance rule

Do **not** change this audit to PASS merely because the code compiles or because the existing unit suite passes.

A future `PASS_RELEASE_READY` requires all of the following simultaneously:

1. V1-001 through V1-008 repaired and regression-tested;
2. no unresolved HIGH/CRITICAL findings;
3. no unresolved MEDIUM finding affecting data integrity, recovery, or provenance truthfulness;
4. current supported runtime/security patch;
5. exact-release-commit CI evidence available and green;
6. release-candidate coverage and mutation gates green;
7. published-package functional smoke green;
8. clean-VM manual acceptance green;
9. release artifact uniquely versioned and checksummed/attested;
10. a final fresh paranoid audit finds no new release blocker.

Until then, the correct status is:

```text
NOT_RELEASE_READY
SAFE_ENOUGH_ONLY_FOR_CONTROLLED_TESTING_WITH_BACKUPS
PROVENANCE_RECORDS_NOT_YET TRUSTWORTHY AS AUTOMATIC LEGAL/RELEASE ATTESTATIONS
```

---

# 17. Bottom line

The file-safety engineering is already unusually defensive and the repeated bug-fix history has clearly improved it. The remaining work is not a reason to discard that architecture.

The largest problem is now **semantic trust**: a provenance helper must never invent review/approval/origin facts. The second urgent problem is the outdated self-contained runtime baseline. After those are fixed, the stable-state/concurrency model and the release gates should be tightened so the strong transaction logic is not undermined by parallel authorities or weak release evidence.

**Current answer to “is this ready to use without any risks?”: No.**
