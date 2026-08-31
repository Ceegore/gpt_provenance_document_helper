# Running tests under Smart App Control — evidence and playbook

**Status:** current. Supersedes the (incorrect) claim in earlier revisions of
`AGENTS.md` that the test suite "runs SAC-free".

**TL;DR** — `dotnet test` on this repo intermittently dies with `0x800711C7`
("An application control policy has blocked this file" /
"Eine Anwendungssteuerungsrichtlinie hat diese Datei blockiert"). It surfaces as
*hundreds of ordinary-looking test failures*, which is indistinguishable from a
real regression. It is an **environment block, not a defect**. It is
**rate-triggered**, and it clears on its own. Use
`scripts/run_tests_sac_safe.ps1`, which detects it and exits **42** instead of 1.

---

## 1. What was actually observed

During the v1.4.0 feature work a long chain of `rebuild → run full suite →
rebuild → run coverage` cycles produced runs like:

```
Der aktive Testlauf wurde abgebrochen. Grund: Der Testhostprozess ist abgestürzt.
System.IO.FileLoadException: Could not load file or assembly
'...\bin\Release\net10.0-windows\AssetProvenanceHelper.dll'.
Eine Anwendungssteuerungsrichtlinie hat diese Datei blockiert. (0x800711C7)

Fehler! : Fehler: 278, erfolgreich: 0, übersprungen: 0, gesamt: 278
```

278 "failures", zero of which ran. On another run it was 179; on another, 430.
The numbers move around because vstest keeps launching test hosts until it gives
up. **Nothing in those numbers says anything about the code.**

## 2. Evidence gathered

All of the following was measured on this machine against this repo.

### 2.1 Exactly one file is ever blocked

Aggregating Windows Code Integrity block events (`Microsoft-Windows-CodeIntegrity/Operational`,
event IDs 3033/3077):

| Blocked file | Block events |
|---|---|
| `AssetProvenanceHelper.dll` | **80** |
| `AssetProvenanceHelper.Tests.dll` | 0 |
| any NuGet dependency | 0 |

The xUnit message `could not load dependent assembly 'AssetProvenanceHelper.Tests'`
is misleading: the **dependency** (`AssetProvenanceHelper.dll`) is what was blocked.

### 2.2 The blocked file is structurally different from the one that is never blocked

Reading the PE headers of the two unsigned, freshly built assemblies that sit in
the same output folder:

| Assembly | `IMAGE_FILE_DLL` | Subsystem | Ever blocked |
|---|---|---|---|
| `AssetProvenanceHelper.dll` | false | **2 = WINDOWS_GUI** | **yes, 80×** |
| `AssetProvenanceHelper.Tests.dll` | false | 3 = WINDOWS_CUI | no |
| `xunit.core.dll` | true | 3 = WINDOWS_CUI | no |

Both product and test assemblies are *executable images* (they are `Exe`/`WinExe`
outputs, so `IMAGE_FILE_DLL` is clear on both). The one structural difference
between the blocked and never-blocked assembly is the **GUI subsystem**, which is
consistent with an unsigned GUI executable being the shape security heuristics
treat most conservatively.

> `testhost.exe` was checked and is **validly Microsoft-signed** — the loading host
> is not the problem, so there is nothing to fix there.

### 2.3 It is NOT simply "fresh binary ⇒ blocked"

14 consecutive builds, each producing a genuinely new file hash (verified via
`Get-FileHash`, forced with the csproj's existing `SourceRevisionId` property so
no source file had to be touched), each loaded immediately afterwards through the
real .NET 10 loader (`AssemblyLoadContext.LoadFromAssemblyPath`):

```
14 of 14 → LOADED.  0 blocks.
```

So a new, unsigned, GUI-subsystem assembly loads fine in isolation. **Freshness
alone does not cause the block.**

### 2.4 The trigger is rate / volume of test-host launches

This matches an independent finding in a sibling project on the same machine
(`C:\Projects\ScreenshotBoy`, `AGENTS.md`), which investigated the identical
`0x800711C7` symptom and concluded:

> *"Einzelne `dotnet test --no-build --filter`-Runs (≤30 Tests) sind zuverlässig
> in <1 min und lösen 0x800711C7 NICHT aus. Der frühere Trigger entstand durch
> 40+ SEQUENTIELLE Runs, nicht durch einzelne."*
>
> (Single filtered runs of ≤30 tests are reliable and do **not** trigger it. The
> trigger came from **40+ sequential runs**, not from individual ones.)

That project's other recorded mitigations — wait 30–60s after a rebuild, combine
build+test into one invocation, don't re-run after a green result — are consistent
with everything seen here.

### 2.5 Deterministic builds cache verdicts — beware the false causality

.NET builds deterministically: rebuilding **unchanged** source produces a **byte-identical**
assembly that reuses SAC's existing verdict. Any real change — including a one-line
`<Version>` bump — produces a new hash that needs a **fresh** verdict.

Observed here: bumping `1.3.2 → 1.4.0` (pure metadata, zero behaviour change) turned a
green suite into a fully blocked one; reverting to `1.3.2` made it pass again. That looks
exactly like "the change broke the tests" and **it is an illusion** — the revert simply
reproduced an already-approved binary. Never attribute a block to a code change without
running the §1 check.

### 2.6 Things that do *not* help

| Attempted | Result |
|---|---|
| Copying the assembly to another drive/path | Still blocked — the verdict follows content, not path |
| Deleting `bin`/`obj` and doing a full clean rebuild | Still blocked |
| Retrying immediately in a tight loop (10× / 20s apart) | Still blocked; too fast to let it clear |
| Switching Debug ↔ Release | Helps only because it is a *different* file, i.e. luck |

Things that **do** help: waiting (minutes, not seconds), and reducing the number of
back-to-back full-suite runs.

## 3. The playbook

### While iterating — targeted runs
```powershell
dotnet test AssetProvenanceHelper.sln -c Release --no-build --filter "FullyQualifiedName~FeatureV14VariantsAndKeepSettingsTests"
```
Small filtered runs are fast and effectively never blocked. Make this the default.

### For a full-suite run — use the wrapper
```powershell
powershell -File scripts/run_tests_sac_safe.ps1
powershell -File scripts/run_tests_sac_safe.ps1 -Filter "FullyQualifiedName~UpgradeV13"
powershell -File scripts/run_tests_sac_safe.ps1 -SettleSeconds 0   # nothing rebuilt
```

The wrapper:
1. lets a fresh build **settle** (default 20s) before the first test-host launch;
2. runs a **cheap canary** test first, so a block costs ~2s instead of ~2min;
3. **detects** a block two ways — the locale-independent string `0x800711C7` in the
   output, and new Code Integrity 3033/3077 events naming our assembly (this
   secondary detector was validated: true-positive over a window containing the 80
   historical blocks, true-negative over a clean run);
4. **backs off and retries** (default 4 attempts, 30s × attempt);
5. exits **42** for an environment block — never 1 — so it is impossible to confuse
   with a real test failure.

| Exit code | Meaning |
|---|---|
| `0` | Tests ran and passed |
| `1` | Tests ran and genuinely failed — **investigate the code** |
| `42` | SAC block persisted — **nothing was proven; do not touch the code** |

### Rules of thumb
- **Do not re-run the full suite once it is green.** Repeat full runs are the
  biggest contributor to the block.
- **Run coverage once, at the end.** `--collect:"XPlat Code Coverage"` roughly
  triples run time and was by far the most block-prone path observed.
- **Wait ~30–60s after a rebuild** before a heavy run.
- **Never** disable SAC or Defender to work around this (`AGENTS.md` rule 4).

## 4. Options deliberately not taken

| Option | Why not |
|---|---|
| Split the product into a `Core` class library + thin `WinExe` shell, so tests only ever load a true library (subsystem 3) | This is the *proper* structural fix and would likely remove the trigger entirely. Rejected **for now** as a large refactor of a codebase with a hard "no side effects" rule and 1090 tests — it deserves its own dedicated change, not a drive-by. |
| `<UseAppHost>false</UseAppHost>` on the product project | Would drop the unsigned `AssetProvenanceHelper.exe` from build output, but also from `dotnet publish` output, breaking `scripts/run_smoke_tests.ps1` and the release artifact. |
| `--runtime win-x64` (the sibling project's workaround for a *discovery* variant of this error) | Introduces a documented silent stale-build hazard: `dotnet build` does not refresh the RID-specific output, so tests would run against stale assemblies. This repo has already been burned twice by stale/mismatched builds; not worth it. |
| Self-signing the assemblies | SAC trusts reputation, not arbitrary self-signed certificates. Does not help. |

If the block ever becomes constant rather than intermittent, the library split in
row 1 is the recommended real fix.
