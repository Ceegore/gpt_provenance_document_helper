# AI Asset Provenance Helper

A robust, fail-safe Windows desktop utility for tracking, organizing, and recording provenance metadata for AI-generated game and application assets.

---

## Key Features

- **Dual Workflow Modes**:
  - **Reference-Assisted Workflow**: Manage assets generated from a visual reference image (e.g. concept art, draft) with multi-stage verification.
  - **No Reference Mode**: Manage direct single-step generation assets without reference image dependencies.
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
    ├── <sourceMainFilename>.<ext>         # Main generation (original source filename preserved)
    ├── final.md                          # Provenance document linking reference + main metadata
    ├── ingame/
    │   └── <AssetName>.<ext>             # Canonical ingame copy named after the asset
    └── reference/
        ├── <sourceReferenceFilename>.<ext> # Reference image (original source filename preserved)
        └── reference.md                  # Reference metadata and prompt record
```

### 2. No Reference Mode Workflow

```text
<AssetRootFolder>/
└── <AssetName>/
    ├── <sourceMainFilename>.<ext>         # Main generation (original source filename preserved)
    ├── final_no_reference.md             # Provenance document for standalone generation
    └── ingame/
        └── <AssetName>.<ext>             # Canonical ingame copy named after the asset
```

> **Note**: Both the root `<sourceMainFilename>.<ext>` and `ingame/<AssetName>.<ext>` share identical SHA-256 hashes upon creation.

---

## Configuration & Usage

1. **Image Download Folder** *(Optional)*:
   - Path to your browser/ChatGPT downloads folder.
   - If left empty or unset, you can freely browse (`Choose...`) or drag-and-drop images directly onto the Reference or Main card drop areas.
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
| `Ctrl + R` | Commit Reference Image (Reference Workflow) |
| `Ctrl + M` | Commit Main Image (both Reference and No Reference modes) |
| `Ctrl + O` | Open Destination Asset Folder |
| `Ctrl + Q` / `Alt + F4` | Exit Application |
| `F1` / `?` | Toggle Help Overlay |
| `Esc` | Close Help Overlay |

---

## Building and Running

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows)

### Build Solution
```powershell
dotnet build AssetProvenanceHelper.sln -c Release
```

### Run Tests
```powershell
dotnet test AssetProvenanceHelper.sln -c Release
```

### Publish Self-Contained Executable
```powershell
dotnet publish src/AssetProvenanceHelper/AssetProvenanceHelper.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish
```

---

## Legal & Disclaimer

- Made by CeeGore.
- This application provides local file organization and structured provenance records for AI-generated assets.
- Users remain solely responsible for ensuring compliance with copyright laws, third-party terms of service, and applicable licenses for all prompts, reference material, and generated imagery.
