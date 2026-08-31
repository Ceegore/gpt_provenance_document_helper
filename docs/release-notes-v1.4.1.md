## Install

**1. Download** `AssetProvenanceHelper-v1.4.1.zip` and extract it anywhere you can write,
e.g. `C:\Tools\AssetProvenanceHelper`

**2. Run this one line in PowerShell** (adjust the path if you extracted elsewhere):

```powershell
Get-ChildItem "C:\Tools\AssetProvenanceHelper" -Recurse | Unblock-File
```

**3. Double-click** `Start AI Asset Provenance Helper.cmd`

One-time requirement: the free [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0)
— pick *".NET Desktop Runtime"*, not the SDK.

### Don't want to run a command? Start the app directly

Works with no unblocking at all:

```powershell
dotnet "C:\Tools\AssetProvenanceHelper\AssetProvenanceHelper.dll"
```

That is literally all the launcher does.

### Why step 2 is needed

Windows tags every downloaded file as internet-sourced, and Smart App Control refuses to run
`.cmd` files carrying that tag — whatever is inside them. You get no window and no error, or:

> *Eine Anwendungssteuerungsrichtlinie hat diese Datei blockiert. Gefährliche Dateierweiterung aus dem Web.*

`Unblock-File` removes the tag. Only the `.cmd` launcher needs it — the app's `.dll` does not.

Via the GUI instead: right-click the ZIP **before extracting** → Properties → bottom of the first
tab, next to *Security:* / *Sicherheit:* → tick the checkbox → OK. It is **Unblock** on English
Windows and **Zulassen** on German Windows, and only appears while the file still carries the tag.

### Why there's no .exe

This app isn't code-signed (hobby project). Smart App Control refuses to launch unsigned `.exe`
files outright — no error, no window. So the package contains no `.exe`; it runs the app inside
Microsoft's signed `dotnet` host instead. The application is identical either way.

**Don't turn off Smart App Control** — on Windows 11 it can only be re-enabled by reinstalling Windows.

Settings live in `%LOCALAPPDATA%\Ceegore\AssetProvenanceHelper`, never in the program folder.

---

## What's new since v1.3.2

### Layout fixes (v1.4.1)

- Removed the large empty grey blocks under the folder, provider and asset-name fields. They were
  border panels stretched to fill their whole table row.
- The Reference/Main cards get that space back: **198 px → 311 px** at the minimum window size.
- The **Final Prompt** box no longer collapses to zero height — it was rendering at 0 px and was
  invisible even in fullscreen.
- Mode checkboxes and card buttons stay on one line instead of wrapping into stacks.
- Default window 1500×880, minimum 1240×780, so the whole workflow is usable without resizing.
- New `LayoutV140Tests` assert these geometry rules so they cannot silently regress.

### Keep Settings (v1.4.0)

A **Keep settings** checkbox. While on, finishing or cancelling an asset no longer clears
**Asset Name**, **Final Prompt** or the **Variants** count.

Image selections and the "Saved reference" label are **always** cleared even so — a kept selection
would still point at a download-folder file an already-finished asset consumed.

### Variants Mode (v1.4.0)

A **Variants** dropdown (`none`, `1`–`10`), in **both** No-reference and Reference-assisted mode.
Set it to `3` and the tool takes the 3 newest downloads and produces `myassetA`, `myassetB`,
`myassetC` from one asset name and one prompt.

- The **oldest** of the selected images becomes `A` — matching generator output order.
- In Reference-assisted mode each variant folder gets its own byte-identical copy of the reference
  image and its own reference provenance document.
- **Set the count before clicking Reference** — that click fixes the folder name.
- In Direct mode with a reference, N variants use **N + 1** downloads.
- Sequential; if one fails, earlier ones stay complete and later ones are not attempted.

### Compatibility

Existing settings load unchanged (**Keep settings** defaults to off). With Variants `none` and
Keep settings off, behaviour is identical to v1.3.2.

---

## For developers

Smart App Control investigation and the `scripts/run_tests_sac_safe.ps1` test runner:
[`docs/sac-test-execution.md`](https://github.com/Ceegore/gpt_provenance_document_helper/blob/main/docs/sac-test-execution.md)

Full suite **1096 tests** (1095 passed, 1 intentionally skipped); Debug and Release build with
0 warnings / 0 errors under `-warnaserror`; coverage gate passing (method coverage 100%).
Release packaging is automated by `.github/workflows/release.yml` and its launch is verified in CI.
