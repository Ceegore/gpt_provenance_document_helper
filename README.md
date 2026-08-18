# AI Asset Provenance Helper

AI-generated images can be useful in commercial products, but retaining a clear record of how those images were made often creates avoidable manual work. **AI Asset Provenance Helper** is a Windows desktop tool that helps organize AI image assets and create accompanying provenance records for a project.

It is designed around images generated in web chats such as ChatGPT and downloaded to the Windows Downloads folder. The supplied templates are prepared for an OpenAI ChatGPT workflow, and can be adapted for other services by editing the plain-text template files.

## What it does

- Watches a configured Downloads folder for supported image files.
- Moves a selected image into the configured project asset folder.
- Creates a provenance record beside the saved asset.
- Supports a two-stage reference-assisted workflow: save a generated reference first, then save the final image and its prompt while retaining the relationship between both assets.
- Keeps the routine file naming, folder placement, and record creation steps out of the manual workflow.

The tool reduces repetitive administration; it does not determine ownership, licensing, or compliance for you. Review the generated record and ensure it reflects the service terms, inputs, prompts, references, and legal requirements that apply to your project.

## Main workflow

1. Generate an image in a service such as [ChatGPT](https://chatgpt.com/) and download it to your configured Downloads folder.
2. In the app, configure the project and destination asset folder.
3. Select the downloaded image and click the save action.
4. The app moves the image to the project asset folder and creates its provenance file from the configured template.

For a reference-assisted / image-to-image workflow:

1. Save the generated reference image first. The app stores it and creates its reference provenance record.
2. Create the final image using that reference in the generation service.
3. Download the final image, enter the prompt used for the final request, and save it through the app.
4. The app places the final asset and record in the configured folders and links the record to its saved reference asset.

## Templates

Runtime templates live in:

- `src/AssetProvenanceHelper/templates/reference.md`
- `src/AssetProvenanceHelper/templates/final.md`

They contain the default wording for the ChatGPT-oriented provenance records. Copy and edit them to reflect another generation service or your organisation's required fields. Keep records factually accurate—particularly where third-party input, reference material, or generation history is involved.

## Requirements

- Windows 10 or Windows 11
- .NET SDK version specified by `global.json` for development

## Build and test

```powershell
dotnet tool restore
dotnet restore AssetProvenanceHelper.sln
dotnet build AssetProvenanceHelper.sln -c Release -warnaserror
dotnet test AssetProvenanceHelper.sln -c Release --no-build
```

To produce a self-contained Windows release build:

```powershell
dotnet publish src/AssetProvenanceHelper/AssetProvenanceHelper.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o artifacts/publish
```

## Disclaimer

This project helps create internal provenance documentation. It is not legal advice and does not guarantee that an asset is eligible for any particular commercial use. Consult appropriate legal, platform, and customer requirements for your use case.
