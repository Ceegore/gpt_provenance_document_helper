# Feature Plan — Keep Settings + Variants Mode

**File:** `docs/plans/_looi1.md`
**Repository:** `Ceegore/gpt_provenance_document_helper`
**Base version:** v1.3.2
**Target version:** **v1.4.0**
**Audience:** an implementation agent that has *not* read this codebase before.
**Status:** plan only — no code has been written.
**Revision:** v2. Supersedes the first draft, whose D-1 deferred reference-assisted
variants. The user rejected that deferral; variants now work in **both** workflows.
See §2 D-1 for the design that makes this simple rather than complicated.

---

## 0. Read this first

Two features. **Keep Settings** is genuinely small. **Variants Mode** touches the
asset-commit transaction boundary — the most safety-critical code in this repo —
and is not small, even in the simplified form specified here.

Before writing any code, read:

- `AGENTS.md` — the clean-tree rule and the Smart App Control rule.
- `src/AssetProvenanceHelper/Services/AssetProcessorService.Main.cs`
- `src/AssetProvenanceHelper/Services/AssetProcessorService.Reference.cs`
- `src/AssetProvenanceHelper/MainForm.MainWorkflow.cs`
- `src/AssetProvenanceHelper/MainForm.ReferenceWorkflow.cs`

Three rules inherited from the existing design that this work must not weaken:

> 1. **A committed asset is never rolled back.** Once `session.json` is deleted
>    and the outputs exist, the asset is final.
> 2. **Exactly one `session.json` exists at any instant.** It is the sole
>    recovery authority.
> 3. **Nothing is overwritten.** Every promotion is a hash-verified move onto a
>    path proven not to exist.

The entire Variants design below is shaped by rule 2. Read §4.1 before anything
else — it is the load-bearing idea, and it is what keeps this simple.

---

## 1. What the user asked for

Original request:

> * we want one global checkbox "keep settings". While active, the tool does not
>   reset the user input after a provenance actions was done (so e.g. the
>   filename does not have to be reentered).
>
> * we want a variants mode (only for main image, not for reference image): for
>   this, please add a dropdown menu with the option "none, 1-10". […] the tool
>   now picks the newest 3 images in the download folder and performs the whole
>   process for each of it sequentially. The only difference is that the filename
>   the user entered (e.g. "image1") will be added with an "A", "B", "C" etc.

Follow-up corrections (these override the first draft of this plan):

> **D-1:** We dont want your suggestion in the plan. We want this to also work
> with a reference image, but not be complicated. On both modes, the user can set
> a variants numbers for the main image (multiple variants from one reference
> image). If 3 is set, the tool only needs to check if there are 3 image files in
> the download folder and if so, make 3 folder as explained before (`<actual asset
> name>A`... then B, C etc.), and do the process 3 times sequentially for those
> newest 3 images. No deep checks need to be implemented, keep things simple. You
> could use a simple check if the 3 new screenshots considered as new variant
> images have been processed before in the momentary session and warn if this is
> the case.
>
> **D-2:** as suggested please

And, governing the whole job:

> Make sure to not cause side effects. Do paranoid tests on if everything works
> after being done.

"No side effects" is a hard constraint. Concretely it means: **with Variants set
to "none" and Keep Settings off, every existing code path must behave
byte-for-byte as it does in v1.3.2.** Every refactor below is therefore
additive-by-default (optional parameters whose defaults preserve current
behavior), never a rewrite of an existing call.

---

## 2. Decisions

### D-1. Variants work in both workflows — via one simplifying trick

**Decision.** The Variants dropdown is available in **both** No-Reference and
Reference-assisted mode. The simplification that makes reference-assisted
variants cheap is this:

> **When Variants is set, the Reference is committed directly into the *first
> variant's* folder — `image1A` — not into a base folder `image1`. The base
> folder `image1` is never created.**

That single choice collapses the hard part:

- **Variant A needs no new machinery at all.** Its session already exists, its
  reference is already committed, and finishing it is the *existing*
  reference-assisted Main path, unmodified.
- **Variants B..N** each run `CreateReferenceSession` + `ProcessReference`
  against variant A's already-committed, hash-verified reference image, then the
  existing Main path.
- **Rule 2 is never violated.** Sessions are created and deleted strictly
  sequentially; exactly one `session.json` exists at a time, and no orphaned
  reference folder is left behind needing cleanup.

The alternative — committing the reference to `image1`, then replicating into
`image1A/B/C` and cleaning up `image1` afterwards — needs a 4th folder, a
cross-session cleanup step, and leaves an unrecoverable orphan if the app dies
mid-batch. That is the "complicated" version, and it is rejected.

**Consequence the user must know about:** the Variants count has to be chosen
**before** clicking Reference, because that click is what binds the folder name.
`cmbVariants` is therefore locked while a reference session is active (§4.4),
exactly like `chkNoReference` and `chkDirectMode` already are. This is stated in
the help text and is the only UX constraint the feature adds.

**Verified before adopting** (do not re-derive; see Appendix B):
`ValidationService.ValidateSessionPathsForDestructiveOperation` constrains
`ReferenceDestinationPath`, `ReferenceProvenancePath` and `ReferenceFilename` to
the session's own asset folder, but **never constrains `ReferenceSourcePath`**.
Sourcing variant B's reference from inside `image1A/reference/` is therefore
legal under every existing validator.

### D-2. Variant "A" is the **oldest** of the N newest downloads

**Decision.** Take the N newest supported images, order them oldest→newest, and
name them `A`, `B`, `C`… in that order. *(User: "as suggested please".)*

**Why.** A generation AI producing variants 1, 2, 3 has them downloaded in that
order, so variant 1 is the oldest of the three. This matches how Direct mode
already reasons about download order — in `TrySelectDirectReferencePair`
(`MainForm.DirectMode.cs:151`) the *older* of two downloads is the reference,
because it was generated first.

### D-3. "1" is not the same as "none"

`none` = today's behavior exactly (one asset, no suffix). `1` = one asset **with**
the `A` suffix. If `1` behaved like `none` the option would be dead weight.

### D-4. Keep Settings preserves text inputs, never image selections

With Keep Settings on, completion preserves **Asset Name**, **Final Prompt** and
the **Variants count**. It always clears the Main image selection, the Reference
image selection and the "Saved reference:" label.

**Why the exception.** A retained image selection points at a download-folder
file that a committed asset has already consumed. Keeping it selected invites
re-committing the same bytes under a different asset name — a provenance-integrity
failure, which is the exact class of problem this tool exists to prevent. The
user's stated goal ("the filename does not have to be reentered") is fully met by
preserving the text fields.

### D-5. Keep Settings also applies to Cancel

Cancel is where retyping is most annoying — the user cancelled in order to fix
something and retry. This is the one decision that goes slightly beyond the
literal request ("after a provenance action was done"); it is a two-line change
to drop if unwanted.

### D-6. Keep Settings never applies to Request Manifest import

`HandleImportRequest` keeps clearing everything. Importing a manifest is a
deliberate context switch that replaces the whole queue — the existing code calls
it an "atomic import" (`MainForm.RequestQueue.cs:50`). Stale fields would create a
binding `CheckActiveRequestBinding` then silently breaks.

### D-7. Keep Settings persists across restarts; Variants count does not

`AppSettings.KeepSettingsEnabled` is persisted exactly like the existing
`DirectModeEnabled`. The Variants count resets to `none` on every app start.

**Why.** A persisted variant count is a footgun: the user launches the app weeks
later, types a name, clicks Main Image, and silently produces five assets. Within
a session the count *is* preserved across a completion when Keep Settings is on
(D-4) — that is the intended convenience.

### D-8. Provenance documents do not mention variants

No provider-template changes, no `<<<VARIANT>>>` tag. Provider templates are
user-authored files in `provider_templates/`; a new required tag would invalidate
every existing user template, including ones this project has never seen. The
`A`/`B`/`C` suffix in the asset name already records which variant a document
describes.

Note this falls out naturally: `RenderReferenceForSession` and
`RenderFinalForSession` both use `session.AssetFolderName` as `AssetName`, so each
variant's documents already name their own asset with no code change.

### D-9. A variants batch is sequential and stops on first failure

If variant B fails, variant A stays committed and C..N are not attempted. Forced
by rule 1: there is no cross-asset transaction, and adding one would mean being
able to delete a fully committed, validated asset. Partial success with an honest
summary is the safe outcome.

### D-10. Minimal preflight — but destination collisions *are* checked

The user asked for no deep checks. Accordingly the batch checks only:

1. enough images are present in the download folder,
2. each of them passes the existing `ValidateImageFile`,
3. **none of the N target asset folders already exists.**

Check 3 is kept despite "keep it simple" because it is ~10 lines and prevents the
single most likely bad outcome: `image1C` already exists from a previous run, so
`image1A` and `image1B` get committed and *then* the batch dies. The underlying
collision is already caught by `CreateNoReferenceMainSession` /
`CreateReferenceSession`; the preflight only moves the detection earlier, before
anything is written.

**Dropped from the first draft** as gold-plating: pairwise source-distinctness
checks (`FindLatestImages` enumerates distinct files and cannot return
duplicates), and per-name re-validation of derived variant names beyond the
single `ValidateAssetName` call.

### D-11. Session reuse warning (the user's suggestion)

Track, in memory for the lifetime of the app process, the source path of every
Main image that has been durably committed. Before starting a **variants batch**,
if any resolved source was already committed this session, show a
`TwoChoiceDialog`: *"N of these images were already processed in this session.
Process them again anyway?"* → Continue / Cancel.

**Why it matters.** With Keep Settings on, clicking Main Image twice without
downloading new images silently re-processes the same bytes into `image2A/B/C`.
This is the one realistic way to produce duplicate provenance for identical
images, and the user specifically asked for the guard.

**Scope: variants path only.** Tracking happens on every commit (so a single
commit followed by a batch is caught), but the *warning* fires only when
Variants > 0. Adding a new dialog to the existing single-asset path would be a
behavior change the user did not ask for — see the "no side effects" rule in §1.

### D-12. The Request Queue item is marked Done only on full batch success

Per-variant completion is suppressed; `CompleteActiveRequestAfterMainCommit` runs
once after the final variant. On partial failure the request stays Pending,
because "produce this asset" is not done if only two of three variants exist.

Document the consequence in the summary dialog: retrying after a partial failure
hits "folder already exists" on the variants that succeeded, so the user must
remove those folders or change the asset name. That is existing, safe refusal
behavior, not a new failure mode.

---

## 3. Feature 1 — Keep Settings

Land this first, as its own PR, before starting Feature 2.

### 3.1 Model

`Models/AppSettings.cs` — one property, mirroring `DirectModeEnabled`:

```csharp
public bool KeepSettingsEnabled { get; set; }
    = false;
```

No `SettingsService` change. It round-trips through
`JsonSerializer.Deserialize<AppSettings>`, and older `settings.json` files
without the key get the default — the same compatibility the existing settings
rely on.

### 3.2 UI

`MainForm.Designer.cs` (near line 35):

```csharp
private CheckBox chkKeepSettings = null!;
```

`MainForm.Layout.cs`, in `BuildCurrentAssetGroup` (line 202) — build it exactly
like `chkDirectMode` and add it to `modeFlow` after it:

```
Name   = "chkKeepSettings"
Text   = "Keep settings"
Margin = new Padding(14, 0, 0, 0)
```

Tooltip via the existing `_toolTip`:

> Keeps Asset Name, Final Prompt and the Variants count after an asset is
> completed or cancelled. Image selections are always cleared.

### 3.3 Wiring

`MainForm.cs`:

- `LoadSettingsIntoUi()` (line 186): `chkKeepSettings.Checked = _settings.KeepSettingsEnabled;`
- `WireEvents()` (line 88): a `CheckedChanged` handler assigning
  `_settings.KeepSettingsEnabled = chkKeepSettings.Checked;`, following the
  `OnDirectModeChanged` pattern (line 176). **Do not** add a
  `_settingsService.Save` call in the handler — the existing `Leave` and
  `FormClosing` handlers already persist, and an extra write per click is
  needless I/O on a path that shows a modal dialog on failure.
- `ReadSettingsFromUi()` (line 209) copies only the two folder paths; the mode
  checkboxes write through their own handlers. Keep that split.
- `chkKeepSettings.Enabled` is never state-dependent. **Do not** add it to
  `ApplyState()`.

### 3.4 Behavior change

One shared helper in `MainForm.cs` so the rule lives in one place:

```csharp
/// <summary>
/// Resets the per-asset input fields after a durable completion or cancellation.
/// Image selections and the saved-reference label are always cleared - a stale
/// selection points at a file a committed asset has already consumed. Text
/// inputs and the Variants count survive when Keep Settings is on.
/// </summary>
private void ResetAssetInputFieldsAfterDurableAction()
{
    if (!chkKeepSettings.Checked)
    {
        txtPrompt.Clear();
        txtAssetFolderName.Clear();
        ResetVariantSelectionToNone();   // added by Feature 2; omit in PR 1
    }

    lblReference.Text = "Saved reference: none";

    SetSelectedImage(ImageSlot.Reference, null);
    SetSelectedImage(ImageSlot.Main, null);
    ClearValidationVisuals();
}
```

Replace the equivalent blocks at:

- `MainForm.MainWorkflow.cs:314-320` (in `CompleteMainUiAfterDurableCommit`)
- `MainForm.ReferenceWorkflow.cs:647-653` (in `CompleteCancelUiAfterDurableCommit`), per D-5

Both sit inside a `try` whose `catch` closes the form with a "could not be
refreshed" message. Keep them there; the helper adds no error handling of its own.

**Do not touch** `MainForm.RequestQueue.cs:106-107` (D-6) or the `btnClearPrompt`
handler at `MainForm.cs:123` — that is an explicit user action.

### 3.5 Interactions to verify

With Keep Settings on, after completing `image1`:

- `_activeRequest` is already `null` (set in `CompleteActiveRequestAfterMainCommit`,
  `MainForm.RequestQueue.cs:409`) before fields are touched, so retained text does
  not resurrect a stale binding.
- The retained text still matches the now-Done queue row; clicking it is blocked
  by the `item.IsCompleted` guard (`MainForm.RequestQueue.cs:258`).
- Clicking Main Image again with the unchanged name fails safely at
  `CreateNoReferenceMainSession`'s collision checks
  (`AssetProcessorService.Main.cs:102-116`).

---

## 4. Feature 2 — Variants Mode

### 4.1 The model

`Variants = N` means: take the N newest supported images from the download
folder, order them oldest→newest (D-2), and produce N complete, independent
assets named `<base>A` … `<base>` + the Nth letter.

**No-Reference mode**, for each variant i:

```
CreateNoReferenceMainSession(assetName_i, main_i)
  -> _sessionService.Save
  -> ProcessMainImage
  -> _sessionService.Delete
```

**Reference-assisted mode.** The Reference click already committed the reference
into `<base>A` (D-1). Then:

```
variant A (i = 1):
    reuse the existing _currentSession   // reference already committed
      -> PrepareMainCommit -> Save -> ProcessMainImage -> Delete

variants B..N (i >= 2):
    CreateReferenceSession(assetName_i, refReplicationSource, refProcessedAt)
      -> Save -> ProcessReference -> Save
      -> PrepareMainCommit -> Save -> ProcessMainImage -> Delete
```

where `refReplicationSource` is variant A's **committed** reference image
(`session.ReferenceDestinationPath`, e.g. `<root>/image1A/reference/ref.png`) and
`refProcessedAt` is variant A's `ReferenceProcessedAt`.

Both must be **captured before variant A commits**, because variant A's session is
deleted at its durable commit point.

Reusing variant A's reference timestamp for all variants is correct and
deliberate: it is one reference image, generated once, so every variant's
reference provenance carries the same generation date.

Result on disk for `image1`, variants = 3, reference-assisted:

```
<AssetRoot>/image1A/
    <main-download-1>.png
    license.txt — Final AI-Generated Asset.md
    ingame/image1A.png
    reference/ref.png
    reference/license.txt — AI Reference Asset.md
<AssetRoot>/image1B/        (same shape, main-download-2, its own reference copy)
<AssetRoot>/image1C/        (same shape, main-download-3, its own reference copy)
```

Every variant is indistinguishable from an asset produced by hand. **No new file
format, no schema change, no batch journal, no shared state between variants.**

### 4.2 Constants and naming

Constants in `AppConstants.cs`, behavior in `AssetNaming.cs`. Do not mix — 
`AppConstants` is data-only today.

`AppConstants.cs`:

```csharp
public const int MaxVariantCount = 10;
```

`Services/AssetNaming.cs`, next to `BuildIngameFilename`:

```csharp
/// <summary>
/// Suffix for variant N (1-based): 1 -> "A" ... 10 -> "J".
/// Capped at 10 so one character always suffices, and so no suffix can turn a
/// valid asset name into a reserved Windows device name: the reserved names in
/// ValidationService end in N, N, X, L, a digit, a superscript or '$' - none of
/// which are in A..J.
/// </summary>
public static string GetVariantSuffix(int variantNumber)

public static string BuildVariantAssetName(string baseName, int variantNumber)
```

`GetVariantSuffix` is `((char)('A' + variantNumber - 1)).ToString()`, throwing
`ArgumentOutOfRangeException` outside `1..AppConstants.MaxVariantCount`.
`BuildVariantAssetName` trims `baseName`, appends the suffix, returns it. It must
**not** validate — validation stays in `ValidationService`.

### 4.3 UI

`MainForm.Designer.cs`:

```csharp
private ComboBox cmbVariants = null!;
private Label lblVariants = null!;
```

`MainForm.Layout.cs`, `BuildCurrentAssetGroup` — add `lblVariants` ("Variants")
and `cmbVariants` to `modeFlow` after `chkKeepSettings`. Configure
`DropDownStyle = ComboBoxStyle.DropDownList`, items `"none"`, `"1"` … `"10"`,
`SelectedIndex = 0`.

Helpers (put them in the new `MainForm.Variants.cs`, §4.11):

```csharp
/// <summary>0 = "none". Otherwise 1..MaxVariantCount.</summary>
private int GetSelectedVariantCount() => Math.Max(0, cmbVariants.SelectedIndex);

private void ResetVariantSelectionToNone() => cmbVariants.SelectedIndex = 0;
```

The index *is* the count, which is why "none" must be first.

> **Layout check required.** `modeFlow` sits in an `AutoSize` column of a
> 3-column `TableLayoutPanel`, beside the Asset Name textbox
> (`SizeType.Percent, 100`). Adding a checkbox, a label and a combo will squeeze
> that field. `MainForm.MinimumSize` is `(1240, 700)`
> (`MainForm.Designer.cs:99`). **Verify visually at exactly the minimum window
> size**, not at the 1500px default. If Asset Name becomes unusably narrow, move
> `modeFlow` to its own row (`RowCount = 2`) rather than shrinking the textbox.

### 4.4 Enable / disable and locking

In `ApplyState()` (`MainForm.cs:369`), beside the existing `cmbProvider.Enabled`
assignment:

```csharp
// Variants are available in BOTH workflows, but the count binds the asset
// folder name at Reference time (see plan D-1), so it locks once a reference
// session is active - exactly like chkNoReference / chkDirectMode above.
cmbVariants.Enabled = !referenceReady;
```

That is the whole change; `referenceReady` is already computed at line 371. Two
points the implementer must get right:

- **Do not reset the selection when locking.** Unlike `chkNoReference`, which
  `ApplyState` force-unchecks in `ReferenceReady`, the variant count is still
  *needed* while a reference session is live — it drives the batch that finishes
  variant A. Lock the control, keep the value.
- **The recovery case is covered without extra code.** A recovered session's asset
  name is already `image1A`; deriving further suffixes from it would produce
  `image1AA`. But recovery only happens after a restart, where the count is `none`
  (D-7) *and* the dropdown is locked by the line above. §4.11 step 2 adds an
  explicit guard as a third layer, so no reset logic is needed here.

Tooltip on `cmbVariants`:

> Number of Main-image variants to produce from one prompt. Set this **before**
> clicking Reference — the count is locked once a reference session is active.

### 4.5 The Reference click

`HandleReference()` (`MainForm.ReferenceWorkflow.cs:11`), at line 36:

```csharp
var folderName = txtAssetFolderName.Text.Trim();

var variantCount = GetSelectedVariantCount();
if (variantCount > 0)
{
    // Plan D-1: the reference is committed into the FIRST variant's folder, so
    // variant A needs no replication and no base folder is ever created.
    folderName = AssetNaming.BuildVariantAssetName(folderName, 1);
}
```

That is the entire change to the reference workflow. `ValidateReferenceActionUi`
still validates the *base* name from the textbox, which is correct — appending
`A` to a valid name cannot invalidate it (§4.2).

`HandleReplaceReference` needs **no change**: by then the folder name is already
bound, and replacement operates within it.

### 4.6 Recovery — why nothing changes

Variants run strictly sequentially, and each variant creates then deletes its own
`session.json` before the next starts. At every instant at most one session
journal exists (rule 2). Therefore:

- `RecoverSessionOnStartup` (`MainForm.Recovery.cs`) sees exactly what it sees
  today: zero or one in-flight session. **It needs no changes.**
- A crash mid-batch leaves already-committed variants intact (they are complete
  assets) plus one in-flight variant that existing recovery resolves by existing
  rules.
- The *batch* is not resumable. After a crash the user restarts it manually, and
  the §4.8 preflight reports which variant folders already exist.

**Do not add a batch journal, a batch id, or any new persisted state.** That
would create a new recovery path, a new schema version and a new corruption
class, for a feature whose failure mode is already safe.

### 4.7 Resolving the source images

New method in `MainForm.Variants.cs`:

```csharp
/// <summary>
/// Resolves the N newest supported download-folder images for a variants batch,
/// ordered OLDEST FIRST so index 0 becomes variant "A" (plan D-2).
/// Returns null after reporting the problem.
/// </summary>
private IReadOnlyList<string>? TryResolveVariantMainImages(int count)
```

1. `_validationService.ValidateDownloadFolder(txtDownloadFolder.Text)`; on failure
   `HighlightField(pnlDownloadFolderHost, true)` + `ShowValidationError`, return
   `null`. Mirror `TryAutoSelectLatestMain` (`MainForm.DirectMode.cs:65`).
2. `_imageFinderService.FindLatestImages(settings, count)` inside `try/catch`
   (existing code catches broadly here and calls `ShowError`).
3. If fewer than `count` came back, message naming both numbers — *"Variants is
   set to 3 but only 2 supported images were found in the Image Download
   Folder."* — return `null`.
4. Validate each path with `_validationService.ValidateImageFile(path,
   _settings.AcceptedExtensions)`. Any failure aborts the batch.
5. **Reverse** the list (`FindLatestImages` returns newest-first) and return it.

**Direct mode + Reference-assisted** needs `N + 1` images: the oldest is the
reference, the newest N are the mains. Generalize `TrySelectDirectReferencePair`
(`MainForm.DirectMode.cs:151`) rather than duplicating it:

```
latest = FindLatestImages(settings, count + 1)   // newest-first
reference = latest[count]                        // oldest
mains     = latest[0 .. count-1] reversed        // oldest main -> A
```

With `count == 1` this is byte-for-byte today's behavior (`latest[1]` = reference,
`latest[0]` = main), which is exactly the "no side effects" property required.

### 4.8 Preflight (D-10)

```csharp
/// <summary>
/// Derives every variant asset name and confirms no variant destination exists
/// yet. Returns the names in variant order, or null after reporting the first
/// problem, having touched nothing.
/// </summary>
private IReadOnlyList<string>? TryResolveVariantAssetNames(string baseName, int count)
```

- Validate `baseName` once with `_validationService.ValidateAssetName(baseName,
  _settings.AcceptedExtensions)`.
- For each `1..count`: derive via `AssetNaming.BuildVariantAssetName`, then
  `Directory.Exists(Path.Combine(txtAssetRoot.Text, name))` → abort naming that
  variant if it exists.
- **Reference-assisted exception:** variant A's folder *must* already exist — the
  Reference click created it. Skip the existence check for `i == 1` in
  reference-assisted mode. Getting this backwards makes every reference-assisted
  batch abort immediately; it is the most likely single bug in this feature.

> **Deliberate divergence.** The single-asset path offers "Use Existing / Cancel"
> when the folder exists (`MainForm.MainWorkflow.cs:64-77`). The variants path
> aborts instead — prompting up to ten times mid-batch would be hostile, and
> "use existing" for a variant is almost always a mistake. Say so in the abort
> message so it is not mistaken for a bug.

### 4.9 Session reuse warning (D-11)

In `MainForm.cs`:

```csharp
/// <summary>
/// Source paths of Main images durably committed during this app session.
/// In-memory only and intentionally not persisted - "momentary session" per the
/// feature request. Used to warn before reprocessing the same downloads.
/// </summary>
private readonly HashSet<string> _committedMainSourcesThisSession =
    new(StringComparer.OrdinalIgnoreCase);
```

Add `ValidationService.NormalizePath(sourceImage)` after **every** durable Main
commit (single and variant). Before a variants batch, if any resolved main is
already present:

```csharp
var reused = mains.Where(m => _committedMainSourcesThisSession.Contains(
    ValidationService.NormalizePath(m))).ToList();

if (reused.Count > 0)
{
    var proceed = TwoChoiceDialog.ShowChoice(
        this,
        "Images already processed",
        $"{reused.Count} of these {mains.Count} images were already processed "
        + "in this session:" + Environment.NewLine + Environment.NewLine
        + string.Join(Environment.NewLine, reused.Select(Path.GetFileName))
        + Environment.NewLine + Environment.NewLine
        + "Process them again anyway?",
        "Process Again",
        "Cancel");

    if (!proceed) { return; }
}
```

Warning fires on the variants path only (D-11); tracking happens everywhere.

### 4.10 Refactors for reuse

**Reuse the existing commit code. Do not duplicate it.** `ExecuteMainCommit` has
five distinct critical-failure arms, three of which close the form; a second copy
of that logic is the worst thing that could be done to this codebase.

All three refactors are optional-parameter additions whose defaults preserve
current behavior exactly.

**(a) `ValidateMainActionUi`** (`MainForm.ValidationUi.cs:153`):

```csharp
internal bool ValidateMainActionUi(bool requireSelectedMainImage = true)
```

Guard only the `GetSelectedImage(ImageSlot.Main)` block (lines 183-199). For a
batch the single slot is meaningless — sources come from §4.7 — but prompt, asset
name, asset root and template validation still matter. Existing callers are
unchanged.

**(b) `ExecuteMainCommit`** (`MainForm.MainWorkflow.cs:182`) → return `bool`
(`true` = durably committed) and take `bool suppressUiCompletion = false`:

- every existing `return;` becomes `return false;`
- the success tail becomes:

```csharp
// DURABLE COMMIT POINT: complete outputs exist and active session.json is deleted.
_committedMainSourcesThisSession.Add(ValidationService.NormalizePath(sourceImage));

if (!suppressUiCompletion)
{
    CompleteMainUiAfterDurableCommit(session, committedFilename, processedAt);
}

return true;
```

Existing callers ignore the return value and pass no flag → identical behavior.

**(c) `HandleNoReferenceMainImage`** — extract its core so the batch can pass an
explicit asset name and skip the existing-folder dialog:

```csharp
private bool CommitNoReferenceAsset(
    AppSettings settings, string assetName, string sourceImage,
    string prompt, DateTimeOffset processedAt, bool suppressUiCompletion)
```

`HandleNoReferenceMainImage` then becomes: existing-folder dialog, then
`CommitNoReferenceAsset(..., suppressUiCompletion: false)`.

### 4.11 Batch execution

`HandleMainImage()` (`MainForm.MainWorkflow.cs:13`) — minimal dispatch:

```
variantCount = GetSelectedVariantCount()
if variantCount == 0  -> existing behavior, completely unchanged
else                  -> HandleVariantBatch(variantCount)
```

`HandleVariantBatch(int count)`, in the new `MainForm.Variants.cs`:

1. `if (!ValidateMainActionUi(requireSelectedMainImage: false)) return;`

2. **Mode + consistency guards.**
   - No-Reference (`chkNoReference.Checked` or no session): nothing extra.
   - Reference-assisted: require `_currentSession is not null` and
     `_state == UiState.ReferenceReady`, else show the existing "No active
     reference session exists." message and return.
   - Then assert the session really is variant A of this base name:

     ```csharp
     var baseName = txtAssetFolderName.Text.Trim();
     var expectedA = AssetNaming.BuildVariantAssetName(baseName, 1);

     if (!string.Equals(_currentSession.AssetFolderName, expectedA, StringComparison.Ordinal))
     {
         // Recovered session, or a reference created while Variants was "none".
         // Refuse rather than derive image1AA / write into the wrong folder.
         ShowMessageBox(
             "The active reference session was not created as a variant "
             + $"('{_currentSession.AssetFolderName}' instead of '{expectedA}').\n\n"
             + "Finish or cancel it, then start a new asset with Variants set "
             + "before clicking Reference.",
             "Variants unavailable",
             MessageBoxButtons.OK, MessageBoxIcon.Warning);
         return;
     }
     ```

     `txtAssetFolderName` is disabled in `ReferenceReady` (`ApplyState`, line 377),
     so the base name cannot drift between the two clicks.

3. `mains = TryResolveVariantMainImages(count)` — abort if `null`. (Direct +
   reference-assisted uses the `count + 1` form in §4.7 and also yields the
   reference.)

4. `names = TryResolveVariantAssetNames(baseName, count)` — abort if `null`.

5. Reuse warning (§4.9) — abort if the user cancels.

6. **Capture the reference replication authority** *(reference-assisted only,
   before anything commits)*:

   ```csharp
   var refSource       = _currentSession.ReferenceDestinationPath;
   var refProcessedAt  = _currentSession.ReferenceProcessedAt;
   var providerSnapshot = _currentSession.ProviderTemplate?.Clone();
   var requestKey      = _currentSession.SourceRequestKey;
   ```

   Variant A's session is deleted at its own commit point, so reading these
   afterwards is a use-after-free in all but name.

7. Capture `prompt` and **one** `DateTimeOffset processedAt` for the whole batch.

   > Not cosmetic. `ProcessMainImage` compares `session.MainProcessedAt` against
   > the passed `processedAt` with `EqualsExact`
   > (`AssetProcessorService.Main.cs:314`) and throws if they differ. A previous
   > round of this project lost real time to two separate `DateTimeOffset.Now`
   > calls producing an unrelated "processedAt does not match" failure. Capture
   > once.

8. **Loop** `i = 1..count`:

   ```
   if (IsDisposed) break;                      // see the warning below
   SetSelectedImage(ImageSlot.Main, mains[i-1]);

   if reference-assisted:
       if i == 1:
           session = _currentSession           // reference already committed
       else:
           session = CreateReferenceSession(
               settings, names[i-1], refSource, refProcessedAt,
               providerSnapshot, requestKey)
           _sessionService.Save(session)
           session = ProcessReference(session, settings, refSource, refProcessedAt)
           _sessionService.Save(session)
           _currentSession = session
           RecordRecentDocument(Reference, session.ReferenceProvenancePath,
                                session.AssetFolderName, refProcessedAt)

       PrepareMainCommit(session, settings.AcceptedExtensions,
                         mains[i-1], prompt, processedAt)
       _sessionService.Save(session)
       ok = ExecuteMainCommit(session, mains[i-1], prompt, processedAt,
                              suppressUiCompletion: true)
   else:
       ok = CommitNoReferenceAsset(settings, names[i-1], mains[i-1],
                                   prompt, processedAt,
                                   suppressUiCompletion: true)

   if (!ok) break;

   RecordRecentDocument(Final,
       Path.Combine(assetFolder_i, AppConstants.FinalProvenanceFileName),
       names[i-1], processedAt);

   _lastCompletedAssetFolderPath = assetFolder_i;
   AddStatus($"Variant {suffix} completed: {names[i-1]}");
   OnVariantCommittedHook?.Invoke(i, names[i-1]);
   ```

   Wrap the reference-replication and `PrepareMainCommit` calls in `try/catch`
   that reports via `ShowError` and breaks — mirroring how
   `HandleReferenceAssistedMainImage` already handles those same two calls
   (`MainForm.MainWorkflow.cs:151-177`), including its
   `session.ResetMainCommitMetadata()` on save failure.

   > **Check `IsDisposed` at the top of every iteration.** Several of
   > `ExecuteMainCommit`'s critical arms call `Close()`
   > (`MainForm.MainWorkflow.cs:218, 247, 283`, plus inside
   > `TryReconcileFailedMainCommit`). If variant B trips one, the form is closing
   > and the loop must not start variant C. `HandleDirectMainImage` already guards
   > this way (`MainForm.DirectMode.cs:54`). A batch that keeps committing assets
   > into a closing form is the worst realistic failure mode of this feature.

9. Post-batch UI **once** (§4.12), only if `!IsDisposed`.

### 4.12 Post-batch UI

`CompleteMainUiAfterDurableCommit` (`MainForm.MainWorkflow.cs:300`) mixes
per-asset and per-batch work:

| Work | Per variant | Once per batch |
|---|---|---|
| `RecordRecentDocument` (Final, and Reference for B..N) | ✅ | |
| `_currentSession = null` / `_state = Idle` | ✅ | |
| `_lastCompletedAssetFolderPath` | | ✅ last successful variant |
| `CompleteActiveRequestAfterMainCommit` | | ✅ full success only (D-12) |
| `ResetAssetInputFieldsAfterDurableAction` | | ✅ |
| `ReloadProviderCatalog` | | ✅ |
| Completion `MessageBox` | | ✅ one summary |
| `ApplyState` | | ✅ |

Summary dialog:

- Full success — `3 of 3 variants completed: image1A, image1B, image1C.`
- Partial — `1 of 3 variants completed.` plus which succeeded, which failed and
  why, that the Request stays Pending, and that the existing variant folders must
  be removed or the asset renamed before retrying (D-12).

Keep the existing outer `try/catch` shape — a UI failure after a durable commit
must still never roll anything back (`// Never roll back a committed asset.`,
`MainForm.MainWorkflow.cs:351`).

Put `HandleVariantBatch` and its helpers in a new
`src/AssetProvenanceHelper/MainForm.Variants.cs` partial, matching the existing
one-concern-per-partial layout.

### 4.13 Direct mode

`HandleDirectMainImage` (`MainForm.DirectMode.cs:21`) with variants active:

- **No-Reference + variants:** skip `TryAutoSelectLatestMain()`; the batch does
  its own N-image resolution.
- **Reference-assisted, Idle + variants:** resolve `N + 1` images (§4.7), set the
  reference selection, call `HandleReference()` (which now names the folder
  `<base>A`), then — if a session exists and `!IsDisposed` — run the batch with
  the already-resolved mains.
- **Reference-assisted, `ReferenceReady` + variants** (a retry after the mains
  failed): skip `TryAutoSelectLatestMain()`, resolve N mains, run the batch.

With variants = "none" every branch is unchanged.

### 4.14 Refresh

`RefreshImageSelection(ImageSlot.Main)` (`MainForm.ImageSelection.cs:82`) picks
the single newest image. With variants active that is misleading — the user sees
one filename but N files will be used.

With variants active, Main Refresh resolves the N images and shows a batch label,
e.g. `Selected: 3 variants (a.png → c.png)`, full list in the tooltip. Reference
Refresh is untouched.

Keep the single-path branch of `UpdateImageSlotUi`
(`MainForm.ImageSelection.cs:40`) exactly as-is and add the batch label as a
separate, clearly-named method.

### 4.15 Help overlay

`Ui/HelpOverlayControl.cs` — add `KEEP SETTINGS` and `VARIANTS` sections after
the `DIRECT MODE` block (lines 210-214). Follow the file's existing
`"...\r\n\r\n" +` concatenation style; it uses explicit `\r\n`, not
`Environment.NewLine`.

`VARIANTS` must state:

- Works in both No-reference and Reference-assisted mode.
- N variants = the N newest supported downloads (N + 1 in Direct +
  Reference-assisted: the oldest is the reference).
- **The oldest of those N becomes "A"** — the non-obvious part (D-2).
- **Set the Variants count before clicking Reference** — it locks afterwards (D-1).
- In Reference-assisted mode every variant folder gets its own copy of the same
  reference image and its own reference provenance.
- If one variant fails, earlier ones stay completed.

---

## 5. Testing

The suite is ~1026 tests, serial, and is the only place bug-finding happens
(`AGENTS.md`). New tests go in
`tests/AssetProvenanceHelper.Tests/FeatureV14VariantsAndKeepSettingsTests.cs`,
following the `UpgradeV13*` naming precedent.

### 5.1 Non-negotiable test hygiene

This project has repeatedly shipped tests that reached **real Windows OS state**
because a seam was not installed — real message boxes, a real
`Process.Start(explorer.exe)`, real clipboard writes. Three separate audit rounds
found instances. Any new `MainForm` test **must** install every seam it could hit:

```csharp
MainForm.MessageBoxProvider          = (_, _, _, _, _) => { };  // ALWAYS
MainForm.OpenFolderProvider          = _ => { };                // if Open* reachable
form.ClipboardWriter                 = _ => { };                // if a queue row activates
TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true; // folder-exists + reuse warning
MainForm.OpenFileDialogProvider / FolderBrowserDialogProvider   // if reachable
```

`TwoChoiceDialog.ShowChoice` already has a seam —
`TwoChoiceDialog.CustomChoiceProvider` (`Dialogs/TwoChoiceDialog.cs:179`),
returning `true` for the primary choice. **No new dialog seam is needed.** The
reuse warning (§4.9) uses this same seam, so every reuse-warning test controls it
through `CustomChoiceProvider`.

Use `TestWorkspace` for isolation. Reset every static seam in a `finally`.

### 5.2 Keep Settings

| # | Test |
|---|---|
| KS-1 | Off (default): completion clears prompt + asset name — existing behavior preserved |
| KS-2 | On: completion preserves prompt + asset name |
| KS-3 | On: completion still clears both image selections and resets the reference label (D-4) |
| KS-4 | On: cancel preserves prompt + asset name (D-5) |
| KS-5 | On: Request Manifest import still clears everything (D-6) |
| KS-6 | On: `btnClearPrompt` still clears the prompt |
| KS-7 | `KeepSettingsEnabled` round-trips through `SettingsService.Save`/`Load` |
| KS-8 | A pre-feature `settings.json` (no key) loads with `false` |
| KS-9 | On: after completing a queue-bound request, `_activeRequest` is null and the Done row cannot be reactivated |
| KS-10 | On: clicking Main Image again with the unchanged name fails safely and does not corrupt the completed asset |
| KS-11 | On: completion preserves the variants count; off: it resets to "none" (D-7) |

### 5.3 Variants — naming and selection

| # | Test |
|---|---|
| VN-1 | `GetVariantSuffix` maps 1→A … 10→J |
| VN-2 | `GetVariantSuffix` throws for 0, 11, negative |
| VN-3 | `BuildVariantAssetName("image1", 1)` → `"image1A"` |
| VN-4 | Base name with surrounding whitespace is trimmed before suffixing |
| VN-5 | All 10 derived names for a valid base pass `ValidateAssetName` |
| VN-6 | **Ordering: the oldest of the N becomes A** (D-2). Write files with explicit, well-separated `LastWriteTimeUtc` values. See §5.9 |
| VN-7 | `FindLatestImages` tie-breaking (equal timestamps) still yields a deterministic, distinct ordering |

### 5.4 Variants — preflight

| # | Test |
|---|---|
| VP-1 | N=3, only 2 images → aborts, message names both counts, **nothing written to the asset root** |
| VP-2 | One of the N images invalid/unsupported → whole batch aborts, nothing written |
| VP-3 | No-Reference, `image1B` folder exists → batch aborts before `image1A` is created. Assert `image1A` does **not** exist afterwards — this proves preflight runs first |
| VP-4 | **Reference-assisted: `image1A` existing does NOT abort** — it is variant A's own folder (§4.8 exception). This is the highest-risk single bug in the feature |
| VP-5 | Download folder invalid → aborts with the field highlighted |
| VP-6 | `FindLatestImages` throws `IOException` → `ShowError`, no partial state |

### 5.5 Variants — No-Reference execution

| # | Test |
|---|---|
| VE-1 | N=3 happy path: three complete asset folders, each with root main + final provenance + `ingame/<name>.<ext>`, all hashes valid |
| VE-2 | Each variant's provenance names its own asset (`image1A` in A's document, `image1B` in B's) |
| VE-3 | All three variants share the identical prompt |
| VE-4 | All three final documents carry the same generation date (single `processedAt`) |
| VE-5 | No `session.json` remains after a successful batch |
| VE-6 | At most one `session.json` exists at any instant — assert via `OnVariantCommittedHook` |
| VE-7 | N=1 produces exactly one asset named `<base>A` (D-3) |
| VE-8 | **"none" produces byte-for-byte today's behavior**: one asset named `<base>`, no suffix, no new dialog |
| VE-9 | N=10 works end to end |

### 5.6 Variants — Reference-assisted execution

The new surface. Test it hardest.

| # | Test |
|---|---|
| VR-1 | Reference click with variants=3 creates `image1A/reference/…` and **no `image1` folder anywhere** (D-1) |
| VR-2 | Reference click with variants="none" still creates `image1` — no side effect on existing behavior |
| VR-3 | N=3 happy path: three folders, each with its own `reference/` copy **and** its own reference provenance, plus main + ingame + final provenance |
| VR-4 | All three reference image copies are **byte-identical** to the original (compare SHA-256) |
| VR-5 | Each variant's *reference* provenance names its own asset (`image1B` in B's reference document) |
| VR-6 | All three reference documents carry the **same** generation date (variant A's `ReferenceProcessedAt` is reused) |
| VR-7 | Variant A reuses the existing session — assert `CreateReferenceSession` is **not** called for i=1 (e.g. no second `reference/` copy operation, and A's reference file mtime/hash is untouched) |
| VR-8 | `ValidateExactReferenceOutput` passes for every variant's committed reference |
| VR-9 | `cmbVariants` is disabled once `_state == UiState.ReferenceReady` and its selection is **not** reset (§4.4) |
| VR-10 | Guard: a session whose `AssetFolderName` is not `<base>A` (recovered, or created while variants was "none") refuses the batch with the §4.11 step-2 message and commits nothing |
| VR-11 | Variants=3 with **no** active reference session → existing "No active reference session exists." path, nothing written |

### 5.7 Variants — failure and reuse warning

| # | Test |
|---|---|
| VF-1 | Variant B fails mid-commit → A remains complete and valid, C is never attempted, summary names both (D-9) |
| VF-2 | After VF-1: no orphaned `session.json`, no temp files (`.main-*`, `.__new_*`) anywhere under the asset root |
| VF-3 | After VF-1: the queue request stays **Pending** (D-12) |
| VF-4 | Full batch success → the queue request is marked Done exactly once |
| VF-5 | Variant B fails with `AssetProcessingException` (`RollbackComplete == false`) → the existing critical path runs and the form closes; A stays committed; **C's folder is never created** — this is the test for the `IsDisposed` guard |
| VF-6 | Reference-assisted: replication of variant B's reference fails → A stays complete, B's folder is fully rolled back by the existing `ProcessReference` rollback, C never attempted |
| VF-7 | Reuse warning appears when a batch re-uses an already-committed source; choosing Cancel commits nothing (D-11) |
| VF-8 | Choosing "Process Again" proceeds and commits normally |
| VF-9 | **No reuse warning on the single-asset path** even with a repeated source — no side effect (D-11 scope) |

### 5.8 Variants — UI state

| # | Test |
|---|---|
| VU-1 | `cmbVariants` is enabled in Idle in **both** No-Reference and Reference-assisted mode (D-1) |
| VU-2 | `cmbVariants` is disabled in `UiState.ReferenceReady` |
| VU-3 | Direct + No-Reference + N: `TryAutoSelectLatestMain` is bypassed |
| VU-4 | Direct + Reference-assisted + N: resolves `N + 1` images, oldest as reference (§4.7) |
| VU-5 | Direct + Reference-assisted + "none": byte-for-byte today's 2-image behavior |
| VU-6 | Main Refresh with N active shows the batch label; Reference Refresh unaffected |
| VU-7 | `GetSelectedVariantCount()` returns 0 for "none" and i for index i |

### 5.9 Empirical verification of the two highest-risk rules

Two behaviors can pass a badly-written test for the wrong reason. Verify each the
way this project verified the atomic-save fix in v1.3.2 — by breaking the
implementation and confirming the test notices:

**Ordering (VN-6).** Implement the ordering → temporarily remove the `.Reverse()`
→ confirm VN-6 **fails** → restore → confirm it passes. If VN-6 passes in both
states it is not testing ordering and must be rewritten.

**Variant A reuse (VR-7).** Temporarily make the loop call
`CreateReferenceSession` for `i == 1` as well → confirm VR-7 fails (it should
detect the duplicate reference work or the resulting collision) → restore.

### 5.10 Regression sweep — the "no side effects" requirement

Beyond the full suite passing, explicitly confirm with variants = "none" and Keep
Settings off:

- No-Reference single asset: unchanged.
- Reference-assisted single asset: unchanged, folder named `<base>` (VR-2).
- Direct mode both workflows: unchanged (VU-5).
- Replace Reference: unchanged.
- Cancel: unchanged.
- Recovery of every existing session phase: unchanged (§4.6 — recovery code is
  not modified at all).

---

## 6. Coverage gate

`scripts/verify_coverage.ps1` is a **required CI check**.

- It walks `src/AssetProvenanceHelper/**/*.cs` dynamically, so
  `MainForm.Variants.cs` is picked up automatically — every method in it must be
  covered or the ratchet regresses.
- The ratchet compares **uncovered** counts; new uncovered lines fail even if the
  percentage rises. Method coverage is currently **100% (463/463)** and cannot
  regress from 100% — every new method needs at least one test.
- Any new `[ExcludeFromCodeCoverage]` must be added to
  `code-coverage-exclusions.json`; the gate validates that list
  **bidirectionally**. Only code wrapping a single blocking-dialog call, the
  message loop, or the DPI initializer qualifies. Variants logic does not.
- After the feature is green: `pwsh scripts/verify_coverage.ps1 -UpdateBaseline`,
  and only because the inventory legitimately grew.
- Also run `pwsh scripts/verify_coverage.ratchet.tests.ps1` (wired into CI as of
  v1.3.2).

---

## 7. Implementation order

Two PRs. Feature 2 is large enough that mixing it with Feature 1 hurts review and
bisection.

**PR 1 — Keep Settings**

1. `AppSettings.KeepSettingsEnabled` + round-trip tests (KS-7, KS-8)
2. Designer field, layout, wiring, tooltip
3. `ResetAssetInputFieldsAfterDurableAction` + both call sites
4. KS-1 … KS-10 (KS-11 lands with PR 2)
5. `verify_like_ci.ps1` on a clean tree → PR → both CI checks green

**PR 2 — Variants Mode**

6. `AppConstants.MaxVariantCount` + `AssetNaming` helpers + VN-1 … VN-5
7. Designer/layout/`ApplyState` for `cmbVariants` + VU-1, VU-2, VU-7
   → **do the minimum-window-size layout check here** (§4.3)
8. `TryResolveVariantMainImages` + VN-6, VN-7 (with the §5.9 empirical check)
9. `TryResolveVariantAssetNames` + VP-1 … VP-6
10. **The three refactors (§4.10), no behavior change.** Full suite green before
    continuing — this is the riskiest step in the plan. If it is not green here,
    stop and fix before adding batch logic on top
11. `HandleReference` variant naming (§4.5) + VR-1, VR-2
12. `HandleVariantBatch` No-Reference path + `OnVariantCommittedHook` + VE-1 … VE-9
13. `HandleVariantBatch` Reference-assisted path + VR-3 … VR-11 (§5.9 check for VR-7)
14. Failure paths + VF-1 … VF-6
15. Reuse warning + VF-7 … VF-9
16. Direct mode + Refresh + VU-3 … VU-6, KS-11
17. Help overlay text
18. §5.10 regression sweep
19. Version → `1.4.0` in `src/AssetProvenanceHelper/AssetProvenanceHelper.csproj`
20. Coverage baseline update (§6)
21. `verify_like_ci.ps1` clean-tree run → PR → CI green → release notes → tag

---

## 8. Explicitly out of scope

- Any change to provider templates or provenance content (D-8)
- Batch resume after a crash (§4.6)
- Cross-variant atomicity (D-9)
- Suffix schemes beyond A–J, or user-configurable suffixes
- Parallel variant processing — the suite is serial by design and the transaction
  model assumes one session at a time
- Changing the Variants count after a reference session exists (D-1)
- Persisting the reuse-warning set across restarts (D-11: "momentary session")

---

## Appendix A — Files touched

**Modified**

| File | Change |
|---|---|
| `Models/AppSettings.cs` | `+KeepSettingsEnabled` |
| `AppConstants.cs` | `+MaxVariantCount` |
| `Services/AssetNaming.cs` | `+GetVariantSuffix`, `+BuildVariantAssetName` |
| `MainForm.Designer.cs` | `+chkKeepSettings`, `+cmbVariants`, `+lblVariants` |
| `MainForm.Layout.cs` | `BuildCurrentAssetGroup` — three controls, possible row split |
| `MainForm.cs` | `LoadSettingsIntoUi`, `WireEvents`, `ApplyState`, `+ResetAssetInputFieldsAfterDurableAction`, `+_committedMainSourcesThisSession`, `+OnVariantCommittedHook` |
| `MainForm.ValidationUi.cs` | `ValidateMainActionUi` — optional `requireSelectedMainImage` |
| `MainForm.MainWorkflow.cs` | `HandleMainImage` dispatch; `ExecuteMainCommit` → `bool` + `suppressUiCompletion`; `+CommitNoReferenceAsset` extraction; field reset |
| `MainForm.ReferenceWorkflow.cs` | `HandleReference` variant-A naming; cancel field reset |
| `MainForm.DirectMode.cs` | variant-aware source resolution (§4.13) |
| `MainForm.ImageSelection.cs` | batch-aware Main slot label |
| `MainForm.RequestQueue.cs` | suppress per-variant completion; complete once per batch |
| `Ui/HelpOverlayControl.cs` | two new help sections |
| `AssetProvenanceHelper.csproj` | version → 1.4.0 |
| `code-coverage-baseline.json` | regenerated |

**Added**

| File | Purpose |
|---|---|
| `MainForm.Variants.cs` | count helpers, source resolution, preflight, reuse warning, batch execution, post-batch UI |
| `tests/.../FeatureV14VariantsAndKeepSettingsTests.cs` | all new tests |
| `docs/release-notes-v1.4.0.md` | release notes |

**Deliberately untouched:** `MainForm.Recovery.cs`, `SessionService.cs`,
`AssetProcessorService.*`, `ValidationService.*`. If a change to any of these
seems necessary, **stop and re-plan** — it means the sequential,
one-session-at-a-time design in §4.1/§4.6 has broken down, and the consequences
reach recovery and provenance integrity.

---

## Appendix B — Assumptions verified against the code

Verified while writing this plan. Do not re-derive; do not assume they still hold
if the relevant files change.

1. **`ReferenceSourcePath` is never confined to the asset folder.**
   `ValidateSessionPathsForDestructiveOperation`
   (`ValidationService.Paths.cs:64-274`) validates `ReferenceFilename`,
   `ReferenceDestinationPath` and `ReferenceProvenancePath` against the session's
   own `reference/` folder, and never inspects `ReferenceSourcePath`. The only
   rule anywhere is that it must be **empty in NoReference mode**
   (`ValidationService.Session.cs:206-209`). Sourcing variant B's reference from
   `image1A/reference/` is therefore legal. **This is what makes D-1 cheap.**

2. **Reference provenance is asset-scoped.** `RenderReferenceForSession`
   (`TemplateService.cs:190`) passes `session.AssetFolderName` as `AssetName`, so
   each variant's reference document names its own asset with no code change (D-8).

3. **Reference documents are already recorded in Recent Documents**
   (`MainForm.ReferenceWorkflow.cs:154`), so variants B..N must record theirs too
   (§4.11 step 8).

4. **`TwoChoiceDialog` already has a test seam** — `CustomChoiceProvider`
   (`Dialogs/TwoChoiceDialog.cs:179`). No new dialog seam is needed for either the
   existing-folder prompt or the new reuse warning.

5. **A suffix can never create a reserved Windows device name.** The reserved set
   (`ValidationService.cs:10-48`) is CON/PRN/AUX/NUL, COM1-9/¹²³, LPT1-9/¹²³,
   CONIN$/CONOUT$ — ending in N, N, X, L, a digit, a superscript or `$`. None of
   A..J can complete one.

6. **`txtAssetFolderName` is disabled in `ReferenceReady`** (`ApplyState`,
   `MainForm.cs:377`), so the base name cannot drift between the Reference click
   and the Main click. §4.11 step 2's guard covers the recovery case regardless.
