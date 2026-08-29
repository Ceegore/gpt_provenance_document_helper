# Bug Run 1 — Full Test Pass Report

**Project:** AI Asset Provenance Helper (.NET 10 WinForms, Windows)
**Repo:** `D:\Projects\_fertig\GPTprovenanceDocumentHelper`
**Branch:** `main` (working tree dirty — see *Scope* below)
**Date:** 2026-08-29
**Executed by:** automated full test pass (local only; GitHub Actions unavailable this month)

---

## 1. Verdict

> ### 🔴 RUN IS **NOT** GREEN

Two mandated steps failed:

| | |
|---|---|
| **Step 3** — Debug build with `-warnaserror` | ❌ **FAILED** (3 × `CS8625`) |
| **Step 5** — Release build with `-warnaserror` | ❌ **FAILED** (same 3 × `CS8625`) |

Additionally, a **genuine intermittent test failure** was reproduced
(`Overlay_TimerKeepsOpenWhileCursorOverPreviewAndClosesAfter`, ~7–20 % failure rate),
and the project's documented *"suite runs non-parallel by design"* invariant was found
**not to be in effect at all**.

The 20× flakiness loop itself passed 20/20, but that is **not** sufficient to call the run
green — the flake was surfaced by other configurations, and the build gate fails outright.

---

## 2. Environment confirmation

| Item | Expected | Actual | Status |
|---|---|---|---|
| `dotnet --version` | `10.0.301` | `10.0.301` | ✅ match |
| `global.json` | pinned, `rollForward: disable` | `{"sdk":{"version":"10.0.301","rollForward":"disable"}}` | ✅ |
| Solution | `AssetProvenanceHelper.sln` | present (3431 bytes) | ✅ |
| Test project TFM | `net10.0-windows` | `net10.0-windows` | ✅ |
| xunit | 2.9.2 + runner.visualstudio 2.8.2 | as declared | ✅ |
| `InternalsVisibleTo` | tests reach internals | declared in app `.csproj` | ✅ |
| Host shell locale | — | German (`de-DE`) — test output is localised | ℹ️ |
| Smart App Control | ON, app unsigned | published exe reports `NotSigned` | ℹ️ constraint |

All test execution ran in-process under Microsoft-signed `dotnet.exe` / `testhost.exe`.
**The app executable was never launched.** No security setting was changed.

---

## 3. Scope note (important when reading results)

The working tree is **dirty**: 24 modified files and ~15 untracked new source files
(`MainForm.DirectMode.cs`, `MainForm.PromptPreview.cs`, `MainForm.ProviderTemplates.cs`,
`MainForm.RequestQueue.cs`, `Models/AssetRequest*.cs`, `Models/ProviderTemplate*.cs`, …),
plus untracked `AGENTS.md`, `_upgrade1.md`, `scripts/run_smoke_tests_sac_safe.ps1`.

These results describe the **working tree as it stands**, not commit `adf63e6`.
Test count is **991**, not the ~923 quoted in the brief — the suite has grown.

---

## 4. Per-step results

| # | Step | Result | Counts |
|---|---|---|---|
| 1 | `dotnet --version` | ✅ PASS | `10.0.301` |
| 2 | `dotnet restore` | ✅ PASS | 2 projects |
| 3 | Debug build `-warnaserror` | ❌ **FAIL** | 0 warnings, **3 errors** |
| 3b | Debug build (no `-warnaserror`) | ✅ PASS | 0 warnings, 0 errors |
| 4 | Debug tests | ✅ PASS | **991** total / 991 passed / 0 failed / 0 skipped — 44 s |
| 5 | Release build `-warnaserror` | ❌ **FAIL** | 0 warnings, **3 errors** |
| 5b | Release build (no `-warnaserror`) | ✅ PASS | 0 warnings, 0 errors |
| 6 | Release tests | ✅ PASS | **991** / 991 / 0 / 0 — 47 s |
| 7 | `Category=RecoveryCritical` | ✅ PASS | **161** / 161 / 0 / 0 — 19 s (96 `[Trait]` sites) |
| 8 | Flakiness loop 20× Release | ✅ PASS | 20/20 iterations, 991 passed each |
| 9 | Coverage | ⚠️ PASS (razor-thin) | line **90.6 %** / branch **85.04 %** — see B-05 |
| 10 | Packaging (structural, SAC-safe) | ✅ PASS | 278 files, 118 MB, all required content present |
| — | **Serial-execution run** (extra) | ❌ **1 FAILURE** | 990 / 991 — see B-02 |

### 4.1 Build failure detail (steps 3 & 5)

```
tests/AssetProvenanceHelper.Tests/UpgradeV13ParanoidMicroTests.cs(181,51): error CS8625
tests/AssetProvenanceHelper.Tests/UpgradeV13ParanoidMicroTests.cs(265,32): error CS8625
tests/AssetProvenanceHelper.Tests/UpgradeV13ParanoidMicroTests.cs(287,32): error CS8625
    "Cannot convert null literal to non-nullable reference type."
```

> ⚠️ **This is masked by incremental builds.** A warm `dotnet build` reports
> `0 Warnung(en), 0 Fehler` in ~1.7 s because the test assembly is up to date and is never
> recompiled. It only appears after `dotnet clean` (~9 s full compile).
> **CI always checks out fresh, so CI is currently red** — on all three jobs
> (`ci.yml:41`, `ci.yml:55`, `ci.yml:137`).

### 4.2 Flakiness loop (step 8) — full detail

20/20 iterations green, 991 passed each. No test failed in any iteration.

| iter | exit | wall | iter | exit | wall | iter | exit | wall | iter | exit | wall |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 0 | 48 s | 6 | 0 | 63 s | 11 | 0 | 55 s | 16 | 0 | 49 s |
| 2 | 0 | 50 s | 7 | 0 | 63 s | 12 | 0 | 50 s | 17 | 0 | 45 s |
| 3 | 0 | 59 s | 8 | 0 | 51 s | 13 | 0 | 48 s | 18 | 0 | 51 s |
| 4 | 0 | 55 s | 9 | 0 | 51 s | 14 | 0 | 46 s | 19 | 0 | 52 s |
| 5 | 0 | 59 s | 10 | 0 | 48 s | 15 | 0 | 45 s | 20 | 0 | 43 s |

**The loop is a weaker flake detector than it looks** — it only ever exercises the default
(parallel) configuration. The one real flake in the suite reproduces far more readily in
isolation (see B-02).

### 4.3 Long-running tests

No test exceeded **2.76 s**. 0 tests > 5 s; 44 tests > 1 s. **No hang or deadlock indicators.**

Slowest five (Release TRX):

| Duration | Test |
|---|---|
| 2.756 s | `ComprehensiveBugFixTests.BUG_008_HelpOverlay_EscapeKey_ClosesOverlay` |
| 2.659 s | `ComprehensiveCoverageTests.MainForm_StartupRecovery_InvalidUnfinishedSession_UserDeletesVsExits` |
| 2.006 s | `UpgradeV13ParanoidBranchTests.MainForm_KeyDownF1ShowsHelp` |
| 1.841 s | `UpgradeV13MainFormTests.NoValidProvidersBlocksNewAssetsButNotRecoveredSession` |
| 1.818 s | `UpgradeV13ParanoidUiTests.Overlay_TimerKeepsOpenWhileCursorOverPreviewAndClosesAfter` |

> Note: TRX per-test `duration` values sum to 242 s against a 45 s wall clock and are
> unreliable in this adapter; wall-clock timings were used for all conclusions.

### 4.4 Packaging (step 10) — structural only, exe never launched

`dotnet publish -c Release -r win-x64 --self-contained true -p:SourceRevisionId=localtestrun`

| Check | Result |
|---|---|
| No shipped mutable state (`settings.json`, `session.json`, `reference-replacement.json`, `recent-documents.json`, `request-progress.json`) | ✅ all absent |
| `templates/` — `reference.md`, `final.md`, `final_no_reference.md` | ✅ present |
| `provider_templates/` — `ChatGPT.md`, `_TEMPLATE.md` | ✅ present |
| `examples/` — `asset_request_manifest_template.json`, `asset_request_conversion_prompt.txt` | ✅ present |
| Core assemblies + apphost | ✅ present |
| Version stamping | ✅ `FileVersion 1.3.0.0`, `ProductVersion 1.3.0+localtestrun` |
| Output size | 278 files / 118 MB |

---

## 5. Prioritised bug list

### B-01 · 🔴 High · CI-breaking — `-warnaserror` fails on 3 nullable errors in test code

* **Where:** `tests/AssetProvenanceHelper.Tests/UpgradeV13ParanoidMicroTests.cs` lines **181**, **265**, **287**
* **Repro:** `dotnet clean AssetProvenanceHelper.sln -c Debug` then
  `dotnet build AssetProvenanceHelper.sln -c Debug --no-restore -warnaserror` → exit 1.
* **Cause:** reflection invocations pass `new object[] { null }`. The array element type
  `object` is non-nullable under `<Nullable>enable</Nullable>`, so the `null` literal raises
  `CS8625`, promoted to an error by `-warnaserror`.
* **Impact:** all three CI jobs fail at the build step; nothing downstream ever runs.
* **Why it was missed:** incremental builds skip recompilation and report `0 Fehler`.

**Suggested fix** — change the array element type to nullable at the three sites:

```csharp
// line 181 — HandleRequestQueueItemActivate(null)
activate!.Invoke(form, new object?[] { null });

// lines 265 and 287 — dragEnter(null, drag) / dragEnter(null, dragFiles)
dragEnter!.Invoke(form, new object?[] { null, drag });
dragEnter.Invoke(form, new object?[] { null, dragFiles });
```

`MethodBase.Invoke` takes `object?[]`, so this is the type-correct form and needs no
suppression. **Do not** silence it with `#pragma warning disable CS8625` or by setting
`<WarningsNotAsErrors>` — that would hide real nullability defects elsewhere.

**Guard against recurrence:** make the local gate match CI by adding a clean-build step,
e.g. `dotnet build -c Debug --no-incremental -warnaserror` in a pre-push hook, so warm builds
can never mask this class of error again.

---

### B-02 · 🔴 High · Genuinely flaky test — `Overlay_TimerKeepsOpenWhileCursorOverPreviewAndClosesAfter`

* **Where:** test `tests/AssetProvenanceHelper.Tests/UpgradeV13ParanoidUiTests.cs:170`;
  failing assertion at `:233` (`Assert.True(overlay.Visible)`).
* **Product code involved:** `src/AssetProvenanceHelper/MainForm.PromptPreview.cs:80-89` (timer)
  and `:132-159` (`IsCursorOverPreviewOrOverlay`).

**Observed failure rate**

| Configuration | Runs | Failures | Rate |
|---|---|---|---|
| Full suite, default (parallel) | 22 | 0 | 0 % |
| Full suite, forced serial | 3 | **1** | ~33 % |
| Test alone, default config | 5 | **1** | ~20 % |

**Failure message (identical in every occurrence):**

```
System.AggregateException : One or more errors occurred. (Assert.True() Failure
Expected: True
Actual:   False)
---- Assert.True() Failure
Expected: True
Actual:   False
   at ...UpgradeV13ParanoidUiTests.RunOnSta(Action action) in UpgradeV13ParanoidUiTests.cs:line 38
   at ...Overlay_TimerKeepsOpenWhileCursorOverPreviewAndClosesAfter() in UpgradeV13ParanoidUiTests.cs:line 170
----- Inner Stack Trace -----
   at ...<Overlay_TimerKeepsOpenWhileCursorOverPreviewAndClosesAfter>b__6_0() in UpgradeV13ParanoidUiTests.cs:line 233
```

**Root cause — two independent hazards, both in the test:**

1. **The real timer races the manual tick.** `ShowPromptOverlay()` calls
   `_promptOverlayTimer.Start()`, and the timer interval is **100 ms**. The test then calls
   `PumpMessages(50)`, which spins on `Application.DoEvents()`. `DoEvents` dispatches the
   **real** `WM_TIMER`, whose handler calls `HidePromptOverlay()` if the cursor is not yet over
   the preview. The overlay is therefore already hidden before the test's own
   `onTick.Invoke(...)` runs, and the assertion fails. `PumpMessages` overshoots its 50 ms
   budget (its loop body sleeps 10 ms per pass), so on a loaded machine it readily crosses the
   100 ms timer boundary.

2. **It depends on the physical OS mouse cursor.** The test assigns
   `Cursor.Position = new Point(previewScreen.Left + 5, previewScreen.Top + 5)` and the product
   reads `Cursor.Position` back. Windows clamps the cursor to the virtual desktop, the
   assignment is not synchronous with respect to subsequent hit-testing, and anything else that
   moves the pointer (a human, a screensaver, a focus-stealing window) changes the result.

**Suggested fix** — remove both sources of nondeterminism rather than adding sleeps:

```csharp
// 1. Stop the real timer before driving OnTick manually, so only the explicit
//    invocation can change overlay state.
var timer = (System.Windows.Forms.Timer)timerField!.GetValue(form)!;
timer.Stop();                       // <— add, after ShowPromptOverlay()
// ... position cursor, then:
onTick!.Invoke(timer, new object[] { EventArgs.Empty });
```

```csharp
// 2. Better: make the hit-test seam injectable so the test never touches the real cursor.
//    In MainForm.PromptPreview.cs:
internal static Func<Point>? CursorPositionProvider;      // test seam, matches the
                                                          // existing hook convention
private bool IsCursorOverPreviewOrOverlay()
{
    var cursor = CursorPositionProvider?.Invoke() ?? Cursor.Position;
    ...
}
```

Option 2 is strongly preferred: the codebase already uses ~30 `internal static` test seams
(`MessageBoxProvider`, `FileAttributesProvider`, …), so this is idiomatic here, and it removes
the last test that hijacks the developer's physical mouse. Assert on
`IsCursorOverPreviewOrOverlay` behaviour via the seam instead of on real screen geometry.

If neither is adopted, at minimum replace `PumpMessages(50)` with a deterministic wait on the
overlay's expected state, and never let a real `Timer` run while a test drives `OnTick` by hand.

---

### B-03 · 🟠 High · The documented "non-parallel by design" invariant is **not in effect**

* **Where:** `tests/AssetProvenanceHelper.Tests/xunit.runner.json`,
  `tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj`; doc claim at `AGENTS.md:23`.

`AGENTS.md:23` states:

> The suite runs **non-parallel by design** (`xunit.runner.json`) — do not add parallelism.

**That is not what happens.** `xunit.runner.json` is never copied to the output directory, so
xunit never reads it and the suite runs with **stock defaults**
(`parallelizeTestCollections = true`, `maxParallelThreads = <cpu count>`).

**Evidence**

1. `xunit.runner.json` is **absent** from
   `tests/AssetProvenanceHelper.Tests/bin/{Debug,Release}/net10.0-windows/`
   (only `xunit.runner.*.dll` files are there). It is absent from `obj/` too.
2. The `.csproj` contains **no** `<None Update="xunit.runner.json">` item and no `<Content>` items.
3. No xunit package in this graph copies it: searching for `xunit.runner.json` across
   `xunit.core/2.9.2`, `xunit/2.9.2` and `xunit.runner.visualstudio/2.8.2` build targets returns
   **nothing**. Auto-copy is *not* a feature of these packages — the item must be declared by the project.
4. There is **no** `[assembly: CollectionBehavior(DisableTestParallelization = true)]` anywhere in
   the test project. The only collection control is a single `[Collection("MainFormUiCollection")]`
   on `MainFormUiTests.cs:16`.
5. **Decisive timing evidence:** forcing serial execution via an explicit `.runsettings` nearly
   doubles wall time — **76–94 s serial vs 43–63 s parallel** for the same 991 tests.
6. `diagnosticMessages: true` and `longRunningTestSeconds: 5` from the JSON never take effect
   (no xunit diagnostic output is emitted at any verbosity).

**Why this matters:** the suite assigns **~600 process-wide mutable statics**, e.g.
`MainForm.MessageBoxProvider` (194 sites), `ValidationService.FileAttributesProvider` (87),
`MainForm.OpenFileDialogProvider` (65), `AssetProcessorService.OnBeforeDeleteFileHook` (38),
`AssetProcessorService.OnFileCopiedHook` (35), `SessionService.OnCancelPhaseSavingHook` (23).
These are plain `internal static` fields — **not** thread-static. Under parallel collections one
test class can observe another's hooks. It has not bitten yet, but the safety property the project
believes it has is simply absent.

**Suggested fix** — pick **one** mechanism and make it authoritative:

*Preferred (robust, cannot be silently lost):*

```csharp
// tests/AssetProvenanceHelper.Tests/AssemblyInfo.cs  (new file)
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
```

*Or, if `xunit.runner.json` is meant to stay authoritative, make it actually ship:*

```xml
<!-- tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj -->
<ItemGroup>
  <None Update="xunit.runner.json">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

Doing **both** is cheapest and safest. Then **verify** rather than assume — the regression guard
is a one-line assertion that the file reached the output directory:

```csharp
[Fact]
public void RunnerConfigIsDeployedAlongsideTestAssembly() =>
    Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "xunit.runner.json")));
```

⚠️ **Expect the suite to get ~2× slower** once this is genuinely enforced (≈ 45 s → ≈ 85 s per
iteration; the 20× CI loop grows from ~17 min to ~30 min). Budget the CI `timeout-minutes`
accordingly. Also note that enabling serialisation is what surfaced **B-02** — more latent
order-dependent failures may appear; that is the mechanism working, not a regression.

---

### B-04 · 🟡 Medium · Test workspace temp-directory leak (`REG_063`) — 684 directories accumulated

* **Where:** `tests/AssetProvenanceHelper.Tests/RegressionTests.cs:1217`
  (`REG_063_MainForm_HandleMainImage_IncompleteRollbackPreservesSessionMetadata`);
  cleanup swallow in `TestWorkspace.Dispose`.
* **Measured:** `%TEMP%\AssetProvenanceHelperTests\` holds **684** leaked workspaces (645 KB), of
  which **605 (88 %)** contain `Assets\asset_reg63\` — i.e. essentially the entire leak is this one
  test. Growth measured across this run: 669 → 684 over 26 suite executions ≈ **0.6 leaked
  directories per suite run**. Leak dates span 2026-08-17 → 2026-08-29.

**Root cause — disposal ordering.** Inside the test's thread body:

```csharp
FileStream? destLock = null;
try
{
    using var workspace = new TestWorkspace();   // scope ends with the TRY block
    ...
    AssetProcessorService.OnMainPromotedHook = dest =>
    {
        destLock = new FileStream(dest, FileMode.Open,
                                  FileAccess.ReadWrite, FileShare.None);   // exclusive lock
        throw new IOException("Simulated disk error during promotion");
    };
    ...
}
finally
{
    destLock?.Dispose();          // runs AFTER workspace.Dispose()
}
```

`using var` disposes at the end of its **enclosing scope — the `try` block** — so
`workspace.Dispose()` runs *before* the `finally` releases `destLock`. `TestWorkspace.Dispose`
calls `Directory.Delete(Root, recursive: true)` while `main.png` is still held with
`FileShare.None`; the delete throws, and `TestWorkspace`'s `catch { }` swallows it silently. The
partially-deleted tree is orphaned. (It leaks ~60 % rather than 100 % of runs because Windows
delete semantics occasionally succeed anyway — hence the nondeterministic rate.)

**Suggested fix** — release the lock before the workspace is torn down:

```csharp
FileStream? destLock = null;
using var workspace = new TestWorkspace();   // hoist ABOVE the try → disposed last
try
{
    ...
}
finally
{
    destLock?.Dispose();                     // now runs BEFORE workspace.Dispose()
    MainForm.MessageBoxProvider = null;
    AssetProcessorService.OnMainPromotedHook = null;
}
```

**Make the leak visible instead of silent.** `TestWorkspace.Dispose`'s bare `catch { }` is what
turned a deterministic bug into an invisible one for twelve days. Suggest retry-then-report:

```csharp
public void Dispose()
{
    for (var attempt = 0; attempt < 3; attempt++)
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return;
        }
        catch (IOException) when (attempt < 2) { Thread.Sleep(50); }
        catch (UnauthorizedAccessException) when (attempt < 2) { Thread.Sleep(50); }
    }
    // Last attempt: let it surface so the owning test is identifiable.
    if (Directory.Exists(Root))
        throw new IOException($"TestWorkspace leaked: {Root}");
}
```

**Housekeeping:** the 684 stale directories are safe to remove manually —
`Remove-Item -Recurse -Force "$env:TEMP\AssetProvenanceHelperTests"`.
*(Left in place by this run; deleting user files was not in scope.)*

---

### B-05 · 🟡 Medium · Coverage gate has a slack of exactly **one branch**

* **Where:** gate defined in `.github/workflows/ci.yml` (Coverage job):
  `lineRate < 90 -or branchRate < 85` → fail.
* **Measured this run:**

| Metric | Covered | Valid | Rate | Gate | Slack |
|---|---|---|---|---|---|
| Lines | 7 869 | 8 685 | **90.60 %** | ≥ 90 % | 52 lines |
| Branches | 2 433 | 2 861 | **85.04 %** | ≥ 85 % | **1 branch** |

Branch coverage needs 2 432 covered branches to clear 85 %; the suite delivers 2 433.
**Adding a single uncovered `if` / `??` / `?:` anywhere in the product breaks CI**, with an error
that points at a coverage percentage rather than at the change that caused it.

**Suggested fix** — this is a policy decision, not a code defect; two sane options:

1. **Buy headroom (recommended).** Target the least-covered files and lift branch coverage to
   ~87 % so ordinary changes stop tripping the gate. Generate the ranked gap list with:

   ```bash
   dotnet test tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj -c Release --no-build --collect:"XPlat Code Coverage" --results-directory artifacts/coverage
   ```

   then sort the Cobertura `<class>` entries by ascending `branch-rate`.
2. **Make the gate ratchet instead of a cliff.** Store the current rate in a checked-in baseline
   file and fail only on a *decrease* (with a small tolerance, e.g. 0.2 pp). This prevents
   backsliding without failing the build for a single new `if`.

Do **not** simply lower the threshold to 80 % — that discards the signal the gate exists to give.

---

### B-06 · 🟢 Low · `Environment.TickCount` overflow in `PumpMessages` (latent, uptime-dependent)

* **Where:** `tests/AssetProvenanceHelper.Tests/UpgradeV13ParanoidUiTests.cs:86-94`;
  3 call sites (`:229`, `:237`, `:310`).

```csharp
var end = Environment.TickCount + milliseconds;
while (Environment.TickCount < end) { Application.DoEvents(); Thread.Sleep(10); }
```

`Environment.TickCount` is a **signed 32-bit** millisecond counter that wraps roughly every
**24.9 days** of machine uptime. When `TickCount` is near `int.MaxValue`,
`TickCount + milliseconds` overflows to a large negative value, the loop condition is immediately
false, and **no message pumping happens at all** — silently turning `PumpMessages(50)` into a
no-op and making the overlay tests fail for reasons nothing in the output explains.
Symmetrically, just after a wrap the loop can spin far longer than intended.

This is latent today (it needs ~24.9 days uptime to hit) but it compounds **B-02** exactly when it
does fire, on a long-lived build machine — the hardest possible debugging scenario.

**Suggested fix** — use the 64-bit counter, which never wraps in practice:

```csharp
private static void PumpMessages(int milliseconds)
{
    var end = Environment.TickCount64 + milliseconds;
    while (Environment.TickCount64 < end)
    {
        Application.DoEvents();
        Thread.Sleep(10);
    }
}
```

`Stopwatch.StartNew()` with `sw.ElapsedMilliseconds < milliseconds` is equally correct.

---

### B-07 · 🟢 Low · Tests drive the physical mouse cursor

* **Where:** `UpgradeV13ParanoidUiTests.cs` — `Cursor.Position = ...` at `:224`, `:236`, `:308`
  (and the `Point(2, 2)` "park the mouse in the corner" idiom).

Running the suite **moves the developer's actual mouse pointer**, and conversely a human touching
the mouse mid-run can fail these tests. It also means the tests cannot run correctly on a locked
workstation or a headless/session-0 agent.

This is the same underlying coupling as **B-02** and is fixed by the same `CursorPositionProvider`
seam proposed there. Until then, these tests should carry a
`[Trait("Category", "RequiresInteractiveDesktop")]` so they can be filtered out of unattended runs:

```bash
dotnet test AssetProvenanceHelper.sln -c Release --no-build --filter "Category!=RequiresInteractiveDesktop"
```

---

### B-08 · 🟢 Low · Ambient-clock re-derivation can straddle midnight

* **Where:** `src/AssetProvenanceHelper/MainForm.MainWorkflow.cs:23`
  (`var processedAt = DateTimeOffset.Now;`) vs `:327`
  (`session.MainProcessedAt ?? DateTimeOffset.Now`); also `MainForm.Recovery.cs:145`.

The overall design here is **good** — services take `processedAt` as a parameter and every date is
rendered with `ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)`, so provenance documents are
culture-independent. The residual risk is narrow: where the session has no stored timestamp, the
fallback re-reads the clock. A workflow that starts at 23:59:59 and completes after midnight can
stamp the provenance record with a different date than the one validated against.

Also note `DateTimeOffset.Now` (local) rather than `UtcNow`, so a DST transition mid-workflow has
the same shape of effect.

**Suggested fix** — capture once per workflow and thread it through; never re-read the clock as a
fallback:

```csharp
// capture at workflow entry, pass down, and persist immediately
session.MainProcessedAt ??= processedAt;   // set once, reuse everywhere downstream
```

Recovery paths that genuinely have no timestamp should surface that as a validation error rather
than silently inventing "now".

---

### B-09 · ℹ️ Info · `CultureInfo.CurrentCulture` mutation in `REG_008`

* **Where:** `tests/AssetProvenanceHelper.Tests/RegressionTests.cs:196-223` — the
  `ar-SA` / `th-TH` / `ja-JP` / … theory sets `CultureInfo.CurrentCulture` and restores it in `finally`.

On .NET Core `CurrentCulture` is per-thread (`AsyncLocal`-backed), not process-global, and the test
restores it correctly — so **this is currently safe**, including under the parallelism described in
B-03. Recorded only because it is a shared-thread mutation on pooled threads and would become a
hazard if the restore were ever removed or an `await` were introduced. `DefaultThreadCurrentCulture`
should explicitly **not** be touched (it isn't today).

---

### B-10 · ℹ️ Info · Stale `net8.0-windows` build artifacts

`bin/` and `obj/` under both `src/AssetProvenanceHelper` and `tests/AssetProvenanceHelper.Tests`
still contain `net8.0-windows` output from a previous target framework. Harmless, but it inflates
the tree and can confuse tooling that globs `bin/**`.

**Suggested fix:** `dotnet clean` does not remove old-TFM folders — delete them explicitly, e.g.
`Get-ChildItem -Recurse -Directory -Filter net8.0-windows | Remove-Item -Recurse -Force`.

---

### B-11 · ℹ️ Info · Thread-join timeouts are tight relative to CI headroom

Four tests use `thread.Join(TimeSpan.FromSeconds(10))` (`RegressionTests.cs:1484`, `:1594`,
`:1910`, `:1954`); the rest use 30/45/60/90 s. The slowest observed test is 2.76 s, so the margin
is ~3.6×. That is adequate on this machine but is the first thing that will break on a contended
CI runner — especially once B-03 is fixed and the suite runs serially (~2× slower wall time).

**Suggested fix:** normalise these to 30 s to match the rest of the suite. A generous join timeout
costs nothing on the happy path and only matters when something has genuinely hung.

---

## 6. Explicitly skipped — and why

| Skipped | Reason |
|---|---|
| **Launching `artifacts/publish/AssetProvenanceHelper.exe`** | **Environment, not a defect.** Smart App Control is ON and the self-contained apphost is unsigned (confirmed: `Get-AuthenticodeSignature` → `NotSigned`). SAC blocks the launch, producing a spurious "Process exited prematurely / Main window not created". Per the run constraints the exe was never started. |
| **`scripts/run_smoke_tests.ps1` as-is** | Same reason — its `Start-Process $absExePath` (line 106) is the single SAC-tripping step. Its **structural** assertions (lines 1–100) were replicated manually and **all pass** (§4.4). |
| **GUI startup via the signed host** | Optional under the run rules (`dotnet artifacts/publish/AssetProvenanceHelper.dll`) and not required for any finding here; all UI behaviour is covered in-process by the WinForms tests. Note `scripts/run_smoke_tests_sac_safe.ps1` (untracked) already implements this path if you want it. |
| **GitHub Actions / CI verification** | No credits this month. Everything ran locally; nothing was pushed. **The B-01 CI-red conclusion is inferred** from `ci.yml` invoking `-warnaserror` (lines 41, 55, 137) on a fresh checkout, reproduced locally via `dotnet clean` + build. |
| **Mutation testing (`.github/workflows/mutation.yml`)** | Not part of the requested plan; not run. |
| **Toggling SAC / Defender / any security setting** | Prohibited by the run constraints. Not attempted. |

---

## 7. Suggested fix order

| Order | Item | Why |
|---|---|---|
| 1 | **B-01** (3 × `CS8625`) | One-line fix; unblocks all of CI. Nothing else can be verified in CI until this lands. |
| 2 | **B-04** (`REG_063` disposal order) | Small, deterministic, self-contained; stops ongoing temp pollution. |
| 3 | **B-02** + **B-06** + **B-07** | Same file, same underlying cause. Fix the timer race and the cursor coupling together. |
| 4 | **B-03** (enforce non-parallel) | Do this **after** B-02, or the newly-serial suite will fail on the known flake. Expect ~2× wall-time increase and re-check CI timeouts. |
| 5 | **B-05** (coverage headroom) | Do last — the B-02/B-03 fixes will shift the numbers anyway. |
| 6 | B-08 / B-10 / B-11 | Housekeeping. |

---

## 8. What was healthy

Worth recording, since a bug report reads more alarming than the codebase deserves:

* **991/991 pass** in Debug, in Release, and in all 20 flakiness iterations.
* **161/161** `RecoveryCritical` tests pass — the rollback/recovery core is solid.
* **Zero compiler warnings in the product code.** All three `-warnaserror` failures are in *test* code.
* **No hangs or deadlocks** — slowest test 2.76 s, nothing above 5 s.
* **Determinism is well handled where it counts:** `ImageFinderService.FindLatestImages`
  (`Services/ImageFinderService.cs:42-51`) has a full deterministic tie-break chain
  (`LastWriteTimeUtc` → `CreationTimeUtc` → ordinal-ignore-case name), which is exactly the sort of
  thing that is usually a flake source and here is not.
* **Path confinement is correct.** The download/asset overlap check
  (`Services/ValidationService.cs:119-141`) appends a trailing separator before `StartsWith`,
  avoiding the classic `C:\foo` vs `C:\foobar` false positive.
* **Atomic writes clean up after themselves.** `WriteTextAtomic`
  (`Services/AssetProcessorService.FileOps.cs:62-120`) writes to `.__write_<guid>.tmp`,
  `Flush(true)`, then `File.Move`, and deletes the temp on any failure.
  **No product-side temp leak was found** — the leak in B-04 is entirely test-harness.
* **Culture handling is right:** every provenance date uses `InvariantCulture`, verified passing
  under `ar-SA`, `th-TH`, `en-US`, `de-DE`, `ja-JP` (this run executed under a German-locale host).
* **Packaging is clean:** no mutable runtime state ships, all templates / provider-templates /
  examples are present, and version stamping (`1.3.0+<rev>`) flows through correctly.

---

*No product behaviour was modified during this run. All findings are reported, not fixed.*
