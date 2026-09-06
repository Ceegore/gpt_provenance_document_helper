# AI Asset Provenance Helper

A robust, fail-safe Windows desktop utility for tracking, organizing, and recording provenance metadata for AI-generated game and application assets.

---

## Key Features

- **Dual Workflow Modes**:
  - **Reference-Assisted Workflow**: Manage assets generated from a visual reference image (e.g. concept art, draft) with multi-stage verification.
  - **No Reference Mode**: Manage direct single-step generation assets without reference image dependencies.
- **Automated API & Production Batch Generation**:
  - **Generate Now (API)**: Rate-limited direct generation via official provider APIs (e.g. OpenAI `gpt-image-2`).
  - **Production Batch Automation**: Asynchronous batch generation with a **50% API cost discount** and separate batch quotas.
  - **Manifest V2 & Alpha Detection**: Automated request manifest processing supporting transparency requirements (`"alpha": "required" | "not_required" | "unknown"`).
  - **Secure DPAPI Secret Storage**: Windows Data Protection API (DPAPI) ensures API keys are encrypted at rest and never saved in plaintext logs, settings, or manifests.
  - **Full Automation Provenance**: End-to-end provenance capturing generation model, mode (direct/batch), target vs provider resolutions, raw and normalized SHA-256 hashes, provider request IDs, and batch IDs.
- **Independent Image Slots**: Select, refresh, choose, or drag-and-drop reference and main image candidates independently with immediate previews.
- **Canonical Ingame Copy**: Automatically creates an `ingame/<assetName>.<ext>` copy with the normalized asset name while retaining the original source filename in the root directory.
- **Non-Destructive File Copying**: Downloaded source files are copied. They are **not** moved or deleted by a normal save operation.
- **Strict SHA-256 Integrity**: Validates hash consistency on disk before any destructive or rollback action.
- **Crash Resilience & Recovery**: Atomic sessions with rollback protection on interrupted transactions and automatic startup recovery.
- **Keyboard Shortcuts**: Streamlined productivity with quick access keybindings.

---

## Directory Structure & Tree Layouts

When committing assets, the tool organizes files under your configured **Asset Root Folder**:

### 1. Reference-Assisted Workflow

```text
<AssetRootFolder>/
└── <AssetName>/
    ├── <sourceMainFilename>.<ext>                      # Main generation (original source filename preserved)
    ├── license.txt — Final AI-Generated Asset.md       # Provenance document linking reference + main metadata
    ├── ingame/
    │   └── <AssetName>.<ext>                          # Canonical ingame copy named after the asset
    └── reference/
        ├── <sourceReferenceFilename>.<ext>              # Reference image (original source filename preserved)
        └── license.txt — AI Reference Asset.md         # Reference metadata and prompt record
```

### 2. No Reference Mode Workflow

```text
<AssetRootFolder>/
└── <AssetName>/
    ├── <sourceMainFilename>.<ext>                      # Main generation (original source filename preserved)
    ├── license.txt — Final AI-Generated Asset.md       # Provenance document for standalone generation
    └── ingame/
        └── <AssetName>.<ext>                          # Canonical ingame copy named after the asset
```

> **Note**: Both the root `<sourceMainFilename>.<ext>` and `ingame/<AssetName>.<ext>` share identical SHA-256 hashes upon creation.

---

## Configuration & Usage

1. **Image Download Folder** *(Optional)*:
   - Path to your browser/ChatGPT downloads folder.
   - If left empty or unset, you can freely browse (`Choose File...`) or drag-and-drop images directly onto the Reference or Main card drop areas / buttons.
2. **Asset Root Folder** *(Required)*:
   - The root destination directory where asset folders are created (e.g. `D:\Projects\GameAssets`).
3. **Asset Name** *(Required)*:
   - Entered **without** file extension (e.g. `potion_health_large` or `character_npc_merchant`).
4. **Prompt** *(Required)*:
   - The text prompt used for generating the image.
5. **No Reference Mode Checkbox**:
   - Check this box if no reference image was used for the asset. When enabled, the Reference card is hidden and the Main card expands to full width.

### API request queue

Import a version 1 or 2 request manifest with **Import Request...**. Selecting a
row copies its prompt and, when an API result is ready, loads the staged candidate
into Main for normal review and commit. **Generate Now (API)** creates direct
requests; **Queue Production Batch** submits eligible requests to OpenAI Batch.
An API result is never marked Done by itself: it becomes Done only after the
existing Main commit has completed durably.

Animation source documents may use the exact filename token `{frame:03d}`. It is
preserved in the manifest request semantics and represented as `_frames` in the
safe Windows asset-folder name; arbitrary invalid filename characters still fail
validation.

The queue, its original order, and completed rows are restored after restarting
the application, even if the imported manifest was moved or deleted. Use
**Clear Queue** to deliberately remove that local queue snapshot and its
completion display. Clearing the queue does not delete locally staged candidates,
generation-job records, or any remote OpenAI batch; those remain available for
paid-output/recovery safety.

### Pixel-Exact browser/download workflow

The request queue also supports a manual, browser-based series workflow. It uses
the existing version-2 manifest schema; the only extra information is preserved
inside the prompt as `FLOWMETA: ...` and `PROZESSMARKER: ...`. Do not turn those
clauses into JSON fields or remove them during conversion. See
[`pixel_exact_manifest_template.json`](src/AssetProvenanceHelper/examples/pixel_exact_manifest_template.json)
and the conversion prompt in `src/AssetProvenanceHelper/examples`.

1. Import the manifest and select its **Einzeln** seed row. The helper selects
   **Pixel-exact mode**, **No reference mode**, and `Pixel phases: none`.
   Generate the one approved master image in the external browser, select it in
   the helper, and click **Main Image**. The seed becomes Done.
2. The matching **RefN** row is loaded automatically. Generate exactly N
   separate images with the approved master attached as the external tool's
   reference, then save all N into the configured download folder. The earliest
   phase must be the oldest of those N files; the last phase must be the newest.
3. Click **Main Image** once. The helper freezes those N files in its local
   journal before writing anything, then commits them oldest-to-newest to the
   RefN row and the following AusRefN rows. Each output has its own asset folder
   and is marked Done only after its own durable commit. Every final provenance
   file records the actual RefN collection prompt, never an AusRef mapping text.

Before the collection is frozen, a confirmation lists each detected source file
and its exact target asset in oldest-to-newest order. Canceling this dialog makes
no filesystem change. The queue's **Show: Open Pixel series** filter keeps every
row of an incomplete canonical series visible (including an already completed
master for context); the status below the queue reports both the overall and the
currently selected series progress.

The Pixel-Exact selector and phase drop-down are mutually exclusive with
Variants and Direct mode. Pixel-Exact multi-image sequences intentionally do not
run through the API controls: API generation must have an API key and produces
one independently staged candidate per queue row.

For a deliberately manual legacy queue without `FLOWMETA`/`PROZESSMARKER`, select
the intended collection row, enable **Pixel-exact mode**, and choose `Pixel phases`
explicitly. The helper then uses that row plus the immediately following N−1 rows
as the ordered targets. It refuses any conflicting recognized workflow metadata;
use the canonical metadata format whenever it is available.

Use the small **×** at the right of a green queue row to delete that direct
asset folder after confirmation and return only that row to Pending. The helper
rejects junctions/reparse points before deletion. A reset Pixel-Exact output can
be resumed from its already frozen batch by selecting its RefN row again.

### Optional flat collection folder

Enable **Collect copies** to copy every successfully committed image into one
flat review folder (default: Windows Pictures). Normal asset folders and all
provenance files are still written exactly as before; the collection folder
contains image copies only. The deterministic copies are removed again when the
matching completed queue row is deleted with **×**.

### Installed templates

Keep both `templates` and `provider_templates` in an installed build.
`templates` contains the required base Reference/Final provenance renderers;
`provider_templates` is the separately selected catalog for generation providers.
Neither directory replaces the other.

---

## Keyboard Shortcuts

| Shortcut | Action |
| :--- | :--- |
| `Ctrl + R` | Process Reference (Idle mode) / Replace Reference (ReferenceReady mode) |
| `Ctrl + M` | Process Main Image (both Reference-assisted and No-reference modes) |
| `Ctrl + O` | Open Destination Asset Folder |
| `F1` / `?` | Toggle Help Overlay |
| `Esc` | Close Help Overlay |

---

## Building and Running

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows); this repository pins SDK `10.0.301`

### Build Solution
```powershell
dotnet build AssetProvenanceHelper.sln -c Release
```

### Run Tests
```powershell
dotnet test AssetProvenanceHelper.sln -c Release
```

To reproduce exactly what CI checks (clean build with `-warnaserror`, Debug and
Release test runs, `RecoveryCritical`) from a clean working tree in one step:
```powershell
powershell -File scripts/verify_like_ci.ps1
```
A pass from a warm/incremental build or a dirty working tree is not equivalent
to this and should not be reported as a CI-green result — see `AGENTS.md`.

Coverage is measured with `dotnet test --collect:"XPlat Code Coverage"` and
checked by `scripts/verify_coverage.ps1` (exact covered/total line, branch,
and method counts against `code-coverage-baseline.json`, plus an enumerated
allowlist for the small number of methods that cannot run unattended — see
`code-coverage-exclusions.json`).

### Publish release package
```powershell
$publishDirectory = Join-Path $PWD "artifacts/publish-sacsafe"
$sourceRevisionId = (git rev-parse HEAD).Trim()
if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
dotnet publish src/AssetProvenanceHelper/AssetProvenanceHelper.csproj -c Release --no-self-contained -p:UseAppHost=false -p:SourceRevisionId=$sourceRevisionId -o $publishDirectory
powershell -File scripts/run_smoke_tests_sac_safe.ps1 -AppDir $publishDirectory
```

### Local Smoke Test (Smart App Control–safe)

On a machine with **Windows Smart App Control (SAC)** enabled, never launch an
unsigned native apphost. Use the SAC-aware test wrapper and the framework-dependent,
apphost-free package path instead:

```powershell
dotnet build AssetProvenanceHelper.sln -c Release --no-restore -warnaserror
powershell -File scripts/run_smoke_tests_sac_safe.ps1
# defaults to src/AssetProvenanceHelper/bin/Release/net10.0-windows
# (PowerShell 7 also works if installed: pwsh scripts/run_smoke_tests_sac_safe.ps1)
```

> A signed `dotnet` host does not guarantee that SAC will accept a freshly built
> unsigned GUI DLL. If output contains `0x800711C7` or Code Integrity records a
> 3033/3077 event, it is an environment block, not a product failure. Do not retry
> blindly or alter SAC/Defender settings; use `scripts/run_tests_sac_safe.ps1`, which
> reports that condition distinctly. See `AGENTS.md` and `C:\Projects\SACsolutions.md`.

---

## Legal & Disclaimer

- Made by CeeGore.
- This application provides local file organization and structured provenance records for AI-generated assets.
- Users remain solely responsible for ensuring compliance with copyright laws, third-party terms of service, and applicable licenses for all prompts, reference material, and generated imagery.
