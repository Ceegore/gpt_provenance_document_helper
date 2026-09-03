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

### Publish Self-Contained Executable
```powershell
$publishDirectory = Join-Path $PWD "artifacts/publish"
$sourceRevisionId = (git rev-parse HEAD).Trim()
if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
dotnet publish src/AssetProvenanceHelper/AssetProvenanceHelper.csproj -c Release -r win-x64 --self-contained true -p:SourceRevisionId=$sourceRevisionId -o artifacts/publish
powershell -File scripts/run_smoke_tests.ps1 -PublishDir artifacts/publish -LogOutputDir artifacts
```

### Local Smoke Test (Smart App Control–safe)

The smoke test above launches the **self-contained, unsigned** `AssetProvenanceHelper.exe`.
On a machine with **Windows Smart App Control (SAC)** enabled, SAC blocks that unsigned
native executable, so the launch step fails as an *environment* condition — not a product
defect. For local startup verification on a SAC-enabled box, use the SAC-safe variant, which
launches the app through the **signed `dotnet` host** (`dotnet AssetProvenanceHelper.dll`)
instead of the native apphost:

```powershell
dotnet build AssetProvenanceHelper.sln -c Release --no-restore -warnaserror
powershell -File scripts/run_smoke_tests_sac_safe.ps1
# defaults to src/AssetProvenanceHelper/bin/Release/net10.0-windows
# (PowerShell 7 also works if installed: pwsh scripts/run_smoke_tests_sac_safe.ps1)
```

> See `AGENTS.md` for the full rationale: `dotnet build`/`test`/`run` all run through
> Microsoft-signed hosts, so the unit/UI test suite is unaffected by SAC — only the
> self-contained published exe trips it.

---

## Legal & Disclaimer

- Made by CeeGore.
- This application provides local file organization and structured provenance records for AI-generated assets.
- Users remain solely responsible for ensuring compliance with copyright laws, third-party terms of service, and applicable licenses for all prompts, reference material, and generated imagery.
