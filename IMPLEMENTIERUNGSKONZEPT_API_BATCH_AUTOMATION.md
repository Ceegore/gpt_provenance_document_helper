# Implementierungskonzept – Automatische API-Bildgenerierung im AI Asset Provenance Helper

**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Zielsystem:** Windows / .NET 10 / WinForms  
**Planstand:** 2026-09-03  
**Zielgruppe:** schwacher KI-Agent; alle wesentlichen Architektur- und Verhaltensentscheidungen sind vorgegeben.

---

# 0. Ziel

Der bestehende **AI Asset Provenance Helper** besitzt bereits einen Request-/Batch-Workflow für große Asset-Listen. Heute unterstützt dieser Workflow vor allem die manuelle Erzeugung in Webchats: Manifest importieren, Request anklicken, Prompt kopieren, extern generieren, Bild auswählen, bestehenden Provenance-/Commit-Workflow abschließen.

Dieser vorhandene Batch-/Request-Queue-Bereich wird um zwei automatische Wege ergänzt:

1. **Generate Now (API)** – alle geeigneten offenen Requests über die normale OpenAI Image API automatisch abarbeiten. Normaler API-Preis, normales Modell-Rate-Limit, lokale Parallelisierung und Rate-Limit-Steuerung.
2. **Queue Production Batch** – alle geeigneten offenen Requests als echten OpenAI Batch einreichen. Asynchron, derzeit 24h Completion Window, separater Batch-Rate-Limit-Pool und laut OpenAI 50 % geringere Batch-Kosten.

Zusätzlich wird rechts oben im Hauptfenster ein **Settings**-Button ergänzt, über den API-/Automations-Einstellungen und der API-Schlüssel verwaltet werden.

**Wichtig:** Der bestehende manuelle Webchat-Workflow bleibt vollständig erhalten. Ohne API-Konfiguration verhält sich das Tool weiterhin wie bisher.

---

# 1. Nicht verhandelbare Regeln

## 1.1 Bestehender Main-Commit bleibt alleinige Done-Autorität

Automatisch generierte Bilder werden **niemals direkt in den Asset Root committed** und **niemals allein aufgrund eines API-Erfolgs auf Done gesetzt**.

Neue Pipeline:

```text
Request Queue
   ↓
API-/Batch-Generation
   ↓
private Staging-Datei
   ↓
Main Candidate
   ↓
bestehender Main Workflow
   ↓
durabler Commit
   ↓
Done
```

Die vorhandene Semantik hinter `CompleteActiveRequestAfterMainCommit(...)` bleibt erhalten: Erst ein erfolgreicher dauerhafter Main-Commit beendet den Request.

## 1.2 Kein blindes Überschreiben

Jede Generation erhält eine eindeutige Candidate-ID. Bestehende Candidate-/Asset-Dateien werden nicht überschrieben.

## 1.3 API-Key nie im Klartext-State

Der existierende `SettingsService` schreibt normales JSON. Deshalb darf `settings.json` **keinen API-Key** enthalten. Secrets werden separat per Windows DPAPI `CurrentUser` geschützt.

## 1.4 Normale Tests machen keine echten kostenpflichtigen API-Aufrufe

Alle Unit-/Integrationstests verwenden Fake-Provider bzw. Fake-`HttpMessageHandler`. Ein echter OpenAI-Test ist nur explizit opt-in, z. B. `APH_RUN_LIVE_OPENAI_TEST=1`.

## 1.5 SAC/Defender niemals abschalten

Auf den Entwicklungsrechnern werden Smart App Control, Defender und Application-Control-Richtlinien nicht manipuliert. `0x800711C7`/CodeIntegrity 3033/3077 ist ein **Environment Block**, kein Produktdefekt.

---

# 2. Kritische aktuelle OpenAI-Korrektur: GPT-Image-2 und Alpha

Der aktuelle OpenAI-Stand muss korrekt implementiert werden:

> `gpt-image-2` unterstützt derzeit **keine transparenten Hintergründe**. `background: "transparent"` ist für dieses Modell nicht unterstützt.

Das widerspricht einer früheren Annahme aus der Vorbesprechung und ist für dieses Tool wesentlich, weil viele Game-Assets echte Alpha-Transparenz benötigen.

## 2.1 MVP-Regel

Für Release 1 dieser Erweiterung:

- OpenAI `gpt-image-2` unterstützt automatisch nur opake/Alpha-nicht-erforderliche Assets.
- `alpha=required` wird **vor dem kostenpflichtigen Request** blockiert.
- Kein verstecktes Background Removal.
- Kein weißer/schwarzer Hintergrund wird als echte Transparenz ausgegeben.
- Architektur wird providerfähig aufgebaut, damit später ein Alpha-fähiger Provider ergänzt werden kann.

## 2.2 Provider-Abstraktion ist Pflicht

```text
IImageGenerationProvider
    ├── OpenAiImageGenerationProvider       ← Release 1
    ├── FutureOpenRouterProvider
    ├── FutureIdeogramProvider
    └── FutureFalProvider
```

Capabilities müssen pro Provider/Modell ausdrückbar sein:

```text
SupportsTextToImage
SupportsBatch
SupportsTransparentBackground
SupportsReferenceImages
SupportsArbitrarySize
```

Für `gpt-image-2` aktuell:

```text
TextToImage: true
Batch: true
TransparentBackground: false
ReferenceImages: true
ArbitrarySize: true (innerhalb der Modellgrenzen)
```

---

# 3. Relevanter vorhandener Repository-Aufbau

Die Implementierung nutzt den bestehenden Workflow und erfindet keinen zweiten Provenance-Stack.

Relevant:

```text
src/AssetProvenanceHelper/
  MainForm.cs
  MainForm.Layout.cs
  MainForm.RequestQueue.cs
  Models/
    AppSettings.cs
    AssetRequestItem.cs
    AssetRequestManifest.cs
    ProviderRenderContext.cs
  Services/
    AppBootstrap.cs
    SettingsService.cs
    AssetRequestManifestService.cs
    RequestProgressService.cs
  provider_templates/
    ChatGPT.md
    _TEMPLATE.md
  examples/
    asset_request_manifest_template.json
    asset_request_conversion_prompt.txt
```

Der aktuelle Queue-Workflow importiert ein striktes Manifest V1, zeigt `Pending`/`Done`, lädt beim Aktivieren Asset Name und Prompt und kopiert den Prompt für den manuellen Webchat. `Done` wird erst nach dem bestehenden Main-Commit persistiert. Diese Eigenschaften bleiben erhalten.

---

# 4. Ziel-UX

## 4.1 Header

Rechts oben:

```text
[ Settings ] [ ? ]
```

Kein reines Unicode-Zahnrad als einzige Beschriftung; Text `Settings` ist robuster und zugänglicher.

Controls:

```csharp
btnSettings
btnHelp
```

`btnSettings` unmittelbar links von `btnHelp`, beide Top/Right verankert.

## 4.2 Request Queue – Aktionszeile

Die bisherige einzelne obere Aktion wird zu:

```text
[ Import Request... ] [ Generate Now (API) ] [ Queue Production Batch ]
```

Control-Namen:

```csharp
btnImportRequest
btnGenerateNow
btnQueueProductionBatch
```

## 4.3 Generate Now (API)

Dieser Button verarbeitet **alle geeigneten offenen Requests des aktuell geladenen Manifests** automatisch über die normale Image API. Das ist absichtlich kein Einzelrequest-Button, weil das Hauptziel Parallelisierung von 100–1000 Assets ist.

Vorher zwingend Confirmation, etwa:

```text
Generate 184 pending assets now using the standard API?

Provider: OpenAI
Model: gpt-image-2
Quality: medium
Eligible: 161
Blocked (alpha required): 23

This mode uses normal API pricing and normal model rate limits.
Requests will be rate-limited locally.

[Generate] [Cancel]
```

## 4.4 Queue Production Batch

Zwingende Confirmation, etwa:

```text
Queue 161 assets as an OpenAI Production Batch?

Provider: OpenAI
Model: gpt-image-2
Quality: medium

OpenAI currently documents:
- asynchronous processing
- 50% lower Batch API cost
- separate Batch rate-limit pool
- completion window up to 24 hours

Blocked because alpha is required: 23

[Queue Batch] [Cancel]
```

Keine exakten Dollarpreise im Produkt hardcoden.

## 4.5 Queue-Status erweitern

UI-Texte:

```text
Pending
Queued
Generating
Batch queued
Batch running
Ready
API failed
Blocked: alpha
Uncertain
Done
```

`Done` bleibt finaler Commit-Status und wird nicht für API-Zustand missbraucht.

---

# 5. SAC-freundliche Architektur – wichtigste Entwicklungsentscheidung

## 5.1 Problem

Das aktuelle Produkt ist eine WinForms-`WinExe`-Assembly. Auf den realen Dev-Systemen wurde mehrfach gemessen, dass Smart App Control/Code Integrity frisch gebaute unsigned GUI-Assemblies blockieren kann.

Die aktualisierte Feldnotiz vom **2026-09-02** ist strenger als ältere Repository-Dokumentation: Auch ein Start über Microsoft-signiertes `dotnet.exe` garantiert nicht, dass die unsigned verwaltete Entry Assembly geladen werden darf. `dotnet AssetProvenanceHelper.dll` ist deshalb nur ein diagnostischer/preferierter Startpfad, **kein garantierter SAC-Bypass**.

## 5.2 Strukturelle Lösung für die neue Funktion

Alle neue nicht-UI-bezogene API-/Batch-/State-/Size-Logik kommt in eine **echte Class Library**:

```text
src/
  AssetProvenanceHelper/
  AssetProvenanceHelper.Core/
```

`AssetProvenanceHelper.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>12.0</LangVersion>
  </PropertyGroup>
</Project>
```

Windows-spezifische Dinge wie DPAPI bleiben im App-Projekt hinter Interfaces.

## 5.3 Separates Core-Testprojekt

```text
tests/
  AssetProvenanceHelper.Tests/
  AssetProvenanceHelper.Core.Tests/
```

`AssetProvenanceHelper.Core.Tests` referenziert **nur** Core. So können fast alle neuen Tests laufen, ohne die SAC-exponierte WinForms-`WinExe`-Assembly zu laden.

## 5.4 Kein Big-Bang-Refactor

Nicht das komplette Legacy-Projekt nach Core verschieben. Neue Core-Domain bekommt neutrale DTOs; UI mappt `AssetRequestItem` → `ImageGenerationSpec`. Spätere Legacy-Migration ist ein separates Projekt.

---

# 6. Neue Dateien

## 6.1 Core

```text
src/AssetProvenanceHelper.Core/
  AssetProvenanceHelper.Core.csproj
  Generation/
    GenerationMode.cs
    GenerationItemStatus.cs
    AlphaRequirement.cs
    ImageGenerationSpec.cs
    ImageGenerationCandidate.cs
    ProviderCapabilities.cs
    ImageSizePlan.cs
    ImageSizePlanner.cs
    GenerationItemRecord.cs
    GenerationBatchRecord.cs
    GenerationState.cs
    GenerationJobStore.cs
    GenerationCustomId.cs
    RequestStartRateLimiter.cs
    RetryPolicy.cs
    Providers/
      IImageGenerationProvider.cs
      OpenAi/
        OpenAiImageGenerationProvider.cs
        OpenAiApiClient.cs
        OpenAiDtos.cs
        OpenAiErrorParser.cs
        OpenAiBatchJsonlBuilder.cs
        OpenAiBatchResultParser.cs
```

## 6.2 WinForms

```text
src/AssetProvenanceHelper/
  Dialogs/SettingsDialog.cs
  Models/ApiAutomationSettings.cs
  Models/ApiCandidateMetadata.cs
  Services/ISecretStore.cs
  Services/DpapiSecretStore.cs
  Services/GeneratedImageStagingService.cs
  Services/ImageNormalizationService.cs
  MainForm.ApiGeneration.cs
  MainForm.ApiBatch.cs
  MainForm.ApiGenerationUi.cs
  provider_templates/OpenAI API.md
```

## 6.3 Tests

```text
tests/AssetProvenanceHelper.Core.Tests/
  ImageSizePlannerTests.cs
  GenerationCustomIdTests.cs
  OpenAiBatchJsonlBuilderTests.cs
  OpenAiBatchResultParserTests.cs
  OpenAiApiClientTests.cs
  RetryPolicyTests.cs
  GenerationJobStoreTests.cs
  GenerationRecoveryTests.cs
  GenerationCapabilityTests.cs
  RequestStartRateLimiterTests.cs

tests/AssetProvenanceHelper.Tests/
  ApiSettingsUiTests.cs
  ApiRequestQueueUiTests.cs
  ApiCandidateCommitIntegrationTests.cs
  ApiProvenanceTests.cs
  ApiManifestV2Tests.cs
```

---

# 7. Provider-Interface

Startform:

```csharp
public interface IImageGenerationProvider
{
    string ProviderId { get; }

    ProviderCapabilities GetCapabilities(string model);

    Task<ImageGenerationCandidate> GenerateAsync(
        ImageGenerationSpec spec,
        CancellationToken cancellationToken);

    Task<BatchSubmissionResult> SubmitBatchAsync(
        IReadOnlyList<ImageGenerationSpec> specs,
        CancellationToken cancellationToken);

    Task<BatchStatusResult> GetBatchStatusAsync(
        string providerBatchId,
        CancellationToken cancellationToken);

    Task<BatchDownloadResult> DownloadBatchResultsAsync(
        BatchStatusResult completedBatch,
        CancellationToken cancellationToken);
}
```

Capabilities:

```csharp
public sealed record ProviderCapabilities(
    bool SupportsTextToImage,
    bool SupportsBatch,
    bool SupportsTransparentBackground,
    bool SupportsReferenceImages,
    bool SupportsArbitrarySize);
```

---

# 8. Generation Spec

```csharp
public sealed record ImageGenerationSpec(
    string ManifestFingerprint,
    string RequestKey,
    string AssetName,
    string FileName,
    string Prompt,
    int TargetWidth,
    int TargetHeight,
    AlphaRequirement AlphaRequirement,
    string ProviderId,
    string Model,
    string Quality,
    int GenerationWidth,
    int GenerationHeight,
    string CustomId);
```

Keine Secrets in diesem DTO.

---

# 9. Manifest V2 für Alpha-Anforderung

Der aktuelle Parser akzeptiert strikt V1 und nur `filename`, `resolution`, `prompt`. Für Automation braucht die Anforderung nach Transparenz ein explizites Feld. Dateiendung ist dafür nicht zuverlässig.

## 9.1 Neues Format

```json
{
  "manifestVersion": 2,
  "assets": [
    {
      "filename": "background_castle.webp",
      "resolution": "1920x1080",
      "alpha": "not_required",
      "prompt": "Complete prompt..."
    },
    {
      "filename": "enemy_armored.png",
      "resolution": "512x512",
      "alpha": "required",
      "prompt": "Complete prompt..."
    }
  ]
}
```

Erlaubt:

```text
required
not_required
unknown
```

## 9.2 V1 bleibt exakt kompatibel

V1 → `AlphaRequirement.Unknown`.

Für OpenAI MVP:

```text
required      → blockieren
not_required  → erlauben
unknown       → erlauben + Preflight-Warnung
```

## 9.3 RequestKey

V1-Hashalgorithmus **nicht ändern**. Für V2 Alpha in das Hashmaterial aufnehmen. So kollidieren unterschiedliche Generation-Semantiken nicht.

## 9.4 Conversion Prompt

`asset_request_conversion_prompt.txt` auf V2 erweitern:

- Quelle fordert ausdrücklich transparent/true alpha → `required`
- Quelle fordert ausdrücklich opaken Fullscreen-/Backdrop-Hintergrund → `not_required`
- sonst → `unknown`
- nicht raten

---

# 10. GPT-Image-2 Größenplanung

Die Manifestauflösung ist die gewünschte **Finalauflösung**, nicht zwingend die direkte Providerauflösung.

Aktuelle GPT-Image-2-Regeln:

```text
max edge <= 3840 px
beide Kanten Vielfache von 16
long/short ratio <= 3:1
Gesamtpixel >= 655360
Gesamtpixel <= 8294400
```

Daher sind vorhandene reale Größen teilweise ungültig:

```text
512x512       → zu wenig Pixel
1920x1080     → 1080 ist nicht durch 16 teilbar
```

## 10.1 Beispiele

`512x512`:

```text
Provider: 816x816
Final:    512x512
```

`816² = 665856` und erfüllt Mindestpixel/16er-Raster.

`1920x1080`:

```text
Provider: 1920x1088
Final:    1920x1080
```

Danach deterministischer Center-Crop um 8 px Höhe.

## 10.2 Kein Stretching

Nie anisotrop verzerren. Pipeline: Provider-Canvas → proportionaler Crop aufs Zielverhältnis → proportionaler Resize → exakt Zielbreite/-höhe.

## 10.3 SizePlanner-Reihenfolge

```text
1. target width/height > 0
2. ratio <= 3:1, sonst block
3. falls Pixel < 655360 proportional hochskalieren
4. beide Kanten nach oben auf Vielfaches 16 runden
5. Verhältnisfehler minimieren
6. max edge/max pixels prüfen
7. Providergröße + Normalisierungsplan zurückgeben
```

Grenzwerte zentral in einem Constraint-/Capability-Objekt halten, nicht an mehreren Stellen hardcoden.

---

# 11. Private Staging-Struktur

Nicht Browser Downloads und nicht Asset Root verwenden.

```text
%LOCALAPPDATA%\Ceegore\AssetProvenanceHelper\generated\
  <manifestFingerprint>\
    <requestKey>\
      <candidateId>.raw.png
      <candidateId>.png
      <candidateId>.metadata.json
```

Metadata ohne Secret:

```json
{
  "schemaVersion": 1,
  "candidateId": "...",
  "provider": "openai",
  "model": "gpt-image-2",
  "mode": "direct",
  "providerRequestId": "...",
  "batchId": null,
  "customId": "...",
  "targetResolution": "512x512",
  "providerResolution": "816x816",
  "rawSha256": "...",
  "normalizedSha256": "...",
  "createdAtUtc": "..."
}
```

---

# 12. Generation Job Store

Neue persistente Datei:

```text
%LOCALAPPDATA%\Ceegore\AssetProvenanceHelper\generation-jobs.json
```

Schreibweise wie der robuste bestehende Settings-State:

```text
serialize → temp → flush → Flush(true) → atomic promote
```

Kein direktes Überschreiben der realen State-Datei.

## 12.1 Item Record

Mindestens:

```text
SchemaVersion
ManifestFingerprint
RequestKey
AssetName
FileName
Mode
ProviderId
Model
Quality
TargetWidth/Height
GenerationWidth/Height
CustomId
ProviderRequestId
ProviderBatchId
Status
StagedOutputPath
RawSha256
NormalizedSha256
SubmittedAtUtc
UpdatedAtUtc
ErrorCode
ErrorMessage
```

## 12.2 Batch Record

Mindestens:

```text
SchemaVersion
LocalBatchId
ManifestFingerprint
ProviderId
Model
Quality
ProviderInputFileId
ProviderBatchId
ProviderOutputFileId
ProviderErrorFileId
RequestKeys[]
Status
CreatedAtUtc
UpdatedAtUtc
```

Keine API-Keys.

---

# 13. Zustandsmodell

```csharp
public enum GenerationItemStatus
{
    Pending,
    Preparing,
    QueuedDirect,
    DirectRateLimited,
    DirectInFlight,
    BatchPreparing,
    BatchSubmitted,
    BatchRunning,
    Downloading,
    Normalizing,
    Ready,
    FailedRetryable,
    FailedPermanent,
    BlockedCapability,
    UncertainAfterInterruption,
    Committed
}
```

`Committed` nur nach vorhandenem Main-Commit.

## 13.1 Direct Crash-Sicherheit

Wenn App/Prozess während `DirectInFlight` stirbt, kann nicht sicher gewusst werden, ob OpenAI den Request verarbeitet/abgerechnet hat. Nach Neustart daher:

```text
DirectInFlight → UncertainAfterInterruption
```

**Nicht automatisch erneut senden.** Nutzer muss bewussten Retry auslösen, weil Doppelabrechnung möglich ist.

---

# 14. Generate Now – Standard API Flow

Eligible = nicht Done, kein Ready Candidate, nicht InFlight, nicht in laufendem Batch, Capability erlaubt Request.

```text
Click Generate Now
↓
vollständiger Preflight
↓
Confirmation
↓
Records atomisch speichern
↓
lokale rate-limited Worker Queue
↓
pro Item:
  Rate Limiter
  POST /v1/images/generations
  Base64 dekodieren
  raw temp schreiben + flush
  PNG validieren
  raw SHA-256
  normalisieren
  final SHA-256
  final Candidate atomisch promoten
  Status Ready
```

Request Body:

```json
{
  "model": "gpt-image-2",
  "prompt": "...",
  "size": "816x816",
  "quality": "medium",
  "n": 1,
  "output_format": "png",
  "background": "opaque"
}
```

`n=1` im MVP, obwohl API mehrere Outputs unterstützt. Das hält Zuordnung, Provenance und Retry eindeutig.

---

# 15. Standard-API Parallelisierung und Rate Limit

Das Tool kennt das tatsächliche OpenAI-Tier des Users nicht zuverlässig. Settings:

```text
Direct API starts/minute: 5
Max concurrent direct requests: 5
```

Defaults entsprechen konservativ Tier 1. Nutzer kann später passend zu seinem Account erhöhen.

Es werden **zwei** Grenzen benötigt:

- Starts pro Minute
- gleichzeitig laufende Requests

Nicht `Task.WhenAll(500)`.

Bei `429`: `Retry-After` beachten, begrenzt retryen, keinen Request verlieren.

---

# 16. Retry Policy

Automatisch retrybar:

```text
408
429
500
502
503
504
temporäre Netzwerkfehler
```

Nicht automatisch:

```text
400/User Error
401
403
404 falsches Modell/Endpoint
image_generation_user_error
moderations-/promptbedingte Fehler
```

Default `MaxAttempts=3`, z. B. 2s/5s/10s plus Jitter; `Retry-After` hat Vorrang.

---

# 17. Production Batch Flow

Batch nutzt `/v1/images/generations`.

## 17.1 JSONL-Zeile

```json
{"custom_id":"aph-abc123-def456","method":"POST","url":"/v1/images/generations","body":{"model":"gpt-image-2","prompt":"...","size":"816x816","quality":"medium","n":1,"output_format":"png","background":"opaque"}}
```

## 17.2 Custom ID

Deterministisch/eindeutig, z. B.:

```text
aph-<12 chars manifest fingerprint>-<16 chars request key>
```

Nicht AssetName allein.

## 17.3 Submission-Reihenfolge

```text
Preflight
↓
GenerationBatchRecord=Preparing atomisch speichern
↓
JSONL temp erzeugen und jede Zeile lokal validieren
↓
POST /v1/files purpose=batch
↓
ProviderInputFileId sofort speichern
↓
POST /v1/batches
 endpoint=/v1/images/generations
 completion_window=24h
↓
ProviderBatchId SOFORT dauerhaft speichern
↓
Polling
```

## 17.4 Unsichere Submit-Grenze

Falls der Remote-Batch erstellt wurde, aber lokales Speichern der `batch_id` scheitert/der Prozess stirbt, darf nicht blind resubmitted werden. State als `Uncertain` markieren und Benutzer warnen; automatische Doppelabrechnung vermeiden.

---

# 18. Batch Polling und Ergebnisimport

Default Poll-Intervall: **30 Sekunden**.

Beim Start der App unfinished Batch IDs aus dem Job Store laden und Monitoring fortsetzen.

Terminale Stati: `completed`, `failed`, `expired`, `cancelled`.

OpenAI kann Resultate in `output_file_id` und Fehler in `error_file_id` liefern. Die Reihenfolge der Outputzeilen ist **nicht garantiert**; ausschließlich `custom_id` für Mapping verwenden.

Auch bei `expired` bereits fertige Resultate importieren; nur unfertige/fehlgeschlagene Items bleiben Fehlerstatus. Kein „ein Fehler = ganzen Batch verwerfen“.

Provider-Outputdateien nach Verfügbarkeit sofort lokal ingestieren und nicht als Langzeitarchiv betrachten.

---

# 19. API-Key-Speicherung

Interface:

```csharp
public interface ISecretStore
{
    string? LoadSecret(string name);
    void SaveSecret(string name, string secret);
    void DeleteSecret(string name);
}
```

Windows-Implementierung per DPAPI `DataProtectionScope.CurrentUser`.

Empfohlener Package-Stand für .NET 10 zum Planzeitpunkt:

```xml
<PackageReference Include="System.Security.Cryptography.ProtectedData" Version="10.0.11" />
```

Pfad:

```text
%LOCALAPPDATA%\Ceegore\AssetProvenanceHelper\secrets.dat
```

Der Schlüssel darf niemals in `settings.json`, `generation-jobs.json`, Candidate-Metadaten, Logs, Statuslabels, Exceptions, Provenance, Clipboard oder Batch-JSONL landen.

---

# 20. Settings Dialog

Modal `Settings`, Tabs:

```text
API
Generation
Batch & Advanced
```

## 20.1 API

```text
Provider: OpenAI
API Key: ************
[Replace key] [Delete key] [Test connection]
Connection status: Not tested / OK / Failed
Model: gpt-image-2
```

Release 1 lässt nur OpenAI produktiv zu, aber Domain bleibt providerneutral.

Base URL im MVP **nicht frei editierbar**, sondern fest `https://api.openai.com/v1`. Sonst könnte ein falscher Host Secrets abgreifen. Spätere Provider erhalten eigene feste Clients/Hosts.

`Test connection` erzeugt **kein** kostenpflichtiges Testbild, sondern prüft Auth/Modelzugriff.

## 20.2 Generation

```text
Direct quality: low | medium | high        default medium
Batch quality:  low | medium | high        default medium
Direct starts/minute: 5
Max concurrent direct requests: 5
Normalize to manifest resolution: checked/locked for MVP
```

## 20.3 Batch & Advanced

```text
Batch poll interval: 30 s
Max requests per submission: 500
Direct retry attempts: 3
HTTP timeout: 3 min
Resume batch monitoring on startup: yes
```

`Max requests per submission` ist ein lokaler Kosten-/Fehlklick-Schutz, kein Providerlimit.

---

# 21. AppSettings – nur nicht-sensitive Daten

Ergänzungen etwa:

```csharp
public bool ApiGenerationEnabled { get; set; }
public string OpenAiModel { get; set; } = "gpt-image-2";
public string DirectImageQuality { get; set; } = "medium";
public string BatchImageQuality { get; set; } = "medium";
public int DirectStartsPerMinute { get; set; } = 5;
public int DirectMaxConcurrency { get; set; } = 5;
public int BatchPollSeconds { get; set; } = 30;
public int MaxBatchRequestsPerSubmission { get; set; } = 500;
public int DirectRetryAttempts { get; set; } = 3;
```

`SettingsService.Normalize` clampen/validieren. API-Key bewusst nicht vorhanden.

---

# 22. HTTP-Client

Eigener dünner `HttpClient`-Wrapper statt großer zusätzlicher SDK-Abhängigkeit ist für dieses Projekt vorzuziehen: transparent, Batch-JSONL einfach, Fake-Handler leicht testbar.

- `HttpClient` nicht pro Request neu erzeugen.
- `Authorization: Bearer ...` requestbezogen setzen.
- `x-request-id` speichern, wenn vorhanden.
- Error Body in sanitized Exception/DTO überführen, nie Secret loggen.
- Timeout ~3 Minuten, weil Bildgenerierungen laut OpenAI lange dauern können.

Benötigte Endpoints:

```text
POST /v1/images/generations
POST /v1/files
POST /v1/batches
GET  /v1/batches/{id}
GET  /v1/files/{id}/content
```

Optional später:

```text
POST /v1/batches/{id}/cancel
GET  /v1/batches
```

---

# 23. Batch JSONL Builder

Regeln:

- UTF-8 ohne BOM
- exakt eine JSON-Struktur pro Zeile
- keine Markdown-Fences
- kein trailing comma
- eindeutiges `custom_id`
- eine Model-ID pro Datei
- URL exakt `/v1/images/generations`
- `method=POST`
- kein Secret

Vor Upload jede Zeile lokal wieder deserialisieren. Kaputte JSONL darf nie kostenpflichtig hochgeladen werden.

---

# 24. Bildvalidierung und Normalisierung

Providerbytes nie ungeprüft als Candidate akzeptieren.

Raw prüfen:

```text
nicht leer
PNG dekodierbar
Dimension = angeforderte Providerdimension
Dateigröße innerhalb vernünftiger Grenze
```

Dann:

```text
decode
→ Crop Rectangle berechnen
→ high-quality crop
→ proportional resize falls nötig
→ PNG encode
→ final PNG neu laden
→ exakte Target-Dimension prüfen
→ SHA-256
→ atomic promote
```

Normalisierung darf im Windows-Layer bleiben, da das bestehende Projekt bereits `System.Drawing` nutzt. Core liefert nur `ImageSizePlan`/Crop-Entscheidung.

---

# 25. Request Queue Integration

`AssetRequestItem.IsCompleted` bleibt finaler Commit-Status. API-State kommt separat aus dem `GenerationJobStore`.

Display-Priorität:

```text
Done
Ready
Generating/Batch running
Failed
Blocked
Pending
```

Bei Klick auf `Ready`:

1. Asset Name/Prompt wie bisher laden.
2. Staged Candidate in Main-Slot setzen.
3. API-Candidate-Metadaten binden.
4. **Keinen** Commit automatisch auslösen.

Wenn danach manuell eine andere Main-Datei gewählt wird, API-Candidate-Metadaten sofort löschen, damit kein manuelles Bild falsche API-Provenance bekommt.

---

# 26. Provenance-Erweiterung

Neues Template `provider_templates/OpenAI API.md`.

Zusätzliche wahrheitsgemäße Felder:

```text
Generation channel: OpenAI API
Provider: OpenAI
Model
Generation mode: direct / batch
Provider request ID
Batch ID
Provider resolution
Final normalized resolution
Post-processing: deterministic crop/resize by helper
Raw output SHA-256
```

Keine unbewiesenen Rechts-/Review-Behauptungen.

`ProviderRenderContext` mit optionalen Feldern erweitern; alte Templates bleiben kompatibel.

---

# 27. Reference-Assisted Workflow – bewusst nicht im MVP automatisieren

Obwohl OpenAI `/v1/images/edits` und Batch-Edits unterstützt, wird Reference-Automation nicht in denselben ersten Patch gepresst. Sie erhöht Multipart-/Upload-/Hash-/Recovery-/Provenance-Komplexität stark.

Release 1:

```text
Idle/text-to-image → API Buttons verfügbar
ReferenceReady      → API Buttons disabled
```

Tooltip erklärt, dass automatisierte Reference-Assisted Generation später kommt.

Phase 2 kann `/v1/images/edits` sauber ergänzen.

---

# 28. UI-Threading und Closing

Keine HTTP-Arbeit auf dem WinForms UI Thread blockieren. Async Controller/Services, Reentrancy Guard, UI-Updates auf UI-Thread.

Bei aktivem Direct Run und Form Closing warnen:

```text
Closing may lose responses to requests that OpenAI has already started and those requests may still be billable.
```

Bei Remote-Batch darf die App geschlossen werden; der Batch läuft remote weiter und Monitoring wird beim nächsten Start fortgesetzt. App-Schließen canceln **nicht** automatisch den Remote-Batch.

---

# 29. Preflight vor jeder kostenpflichtigen Aktion

Vollständig prüfen, bevor Confirmation/Submit beginnt:

```text
Request nicht Done?
kein bestehender Ready Candidate?
nicht InFlight?
nicht in aktivem Batch?
Prompt gültig?
Targetdimension gültig?
Provider/Model konfiguriert?
Secret vorhanden?
Alpha kompatibel?
Providergröße berechenbar?
Submission-Limit eingehalten?
```

Keine halbe Submission nach erstem lokalen Validierungsfehler.

---

# 30. Fehlerverhalten

Direct: requestbezogener Fehler darf andere Queue-Items weiterlaufen lassen. Globaler Auth-/Permission-/Model-Fehler stoppt den Run.

Batch: erfolgreiche Resultate trotz einzelner Fehler importieren; Fehler separat markieren.

Kritisches lokales State-Save scheitert: fail closed und keine weitere Remote-Mutation starten, wenn dadurch Remote-Identifier verloren gehen könnten.

---

# 31. Cost Safety

Aktuelle Preise nicht hardcoden. UI zeigt nur:

```text
Standard API = normal provider pricing
Batch API = provider currently documents 50% Batch discount
```

Vor Submission Count/Provider/Model/Quality/Mode anzeigen. Lokaler Standard `MaxBatchRequestsPerSubmission=500`; bei >100 deutliche Confirmation.

---

# 32. Core-Testmatrix

Mindestens:

## SizePlanner

```text
512x512 → gültige >=min-pixel Providergröße, 16er Raster
1920x1080 → gültiger Provider-Canvas + exakter Normalisierungsplan
1024x1024 → unverändert
ratio >3:1 → block
max edge/pixels → korrekt blocken
jede zurückgegebene Providerkante %16 ==0
```

## Capability

```text
gpt-image-2 + alpha required → block before HTTP
alpha not_required → allowed
alpha unknown → allowed + warning
```

## Batch Builder

```text
one line/request
valid JSON each line
unique custom_id
correct endpoint/model/size
n=1
no secret
single model enforced
```

## Batch Parser

```text
out-of-order maps correctly
success/error/expired parsed
partial results retained
unknown or duplicate custom_id fail closed
```

## Retry

```text
429/5xx retry
400/401 no retry
user-error no retry
Retry-After respected
max attempts respected
```

## Job Store / Recovery

```text
roundtrip
atomic save
old state survives promotion failure
corrupt state not silently treated as success
no API key serialized
BatchSubmitted+id resumes polling
DirectInFlight restart → Uncertain
Ready survives restart
```

---

# 33. WinForms-Testmatrix

Netzwerk immer fake.

```text
Settings button right/top visible
Help button remains usable
secret textbox masked
Save/Delete calls ISecretStore
settings.json never contains key
Generate disabled without manifest/key
API buttons disabled ReferenceReady
confirmation has eligible/blocked counts
API success → Ready, never Done
Ready row loads staged Main candidate
manual Main replacement clears API metadata
existing durable Main commit → Done
failed API never → Done
new manifest import remains atomic
manual clipboard/webchat workflow unchanged
manifest v1 remains accepted
```

---

# 34. SAC-spezifischer Testworkflow

Die aktuelle `SACsolutions.md`-Korrektur hat Vorrang vor älteren Annahmen.

## Tägliche Core-Schleife

```powershell
dotnet build tests/AssetProvenanceHelper.Core.Tests/AssetProvenanceHelper.Core.Tests.csproj -c Release -warnaserror
dotnet test tests/AssetProvenanceHelper.Core.Tests/AssetProvenanceHelper.Core.Tests.csproj -c Release --no-build
```

## WinForms nur gezielt

```powershell
powershell -File scripts/run_tests_sac_safe.ps1 -Filter "FullyQualifiedName~ApiGeneration"
```

Exit `42` = Environment Block. Nicht debuggen/reverten.

## Wenn GUI-Assembly dauerhaft geblockt ist

1. Core vollständig testen.
2. warning-free Build.
3. statische Strukturchecks.
4. UI/Integration in CI bzw. vertrauenswürdigem/non-enforcing Environment.
5. ehrlich dokumentieren: `local dynamic UI verification blocked by SAC`.

`dotnet <app>.dll` nie als garantierten SAC-Workaround dokumentieren.

`AGENTS.md` im Zuge dieses Features entsprechend aktualisieren.

---

# 35. Konstruktor-/Dependency-Kompatibilität

`MainForm` wird in vielen Tests direkt gebaut. Neue Dependencies zunächst **am Ende optional** ergänzen, z. B.:

```csharp
public MainForm(
    AppSettings settings,
    SettingsService settingsService,
    ImageFinderService imageFinderService,
    TemplateService templateService,
    ValidationService validationService,
    AssetProcessorService assetProcessorService,
    SessionService sessionService,
    ProviderTemplateCatalogService? providerTemplateCatalogService = null,
    RecentDocumentHistoryService? recentDocumentHistoryService = null,
    RequestProgressService? requestProgressService = null,
    IImageGenerationProvider? imageGenerationProvider = null,
    ISecretStore? secretStore = null,
    GenerationJobStore? generationJobStore = null)
```

Production liefert echte Services über `AppBootstrap`, Tests Fakes.

---

# 36. Implementierungsphasen – exakt diese Reihenfolge

## Phase 0 – Baseline

- `AGENTS.md` und `C:\Projects\SACsolutions.md` vollständig lesen.
- Branch/Status und relevante bestehende Tests dokumentieren.
- keine User-Änderungen überschreiben.

## Phase 1 – Core-Projekt

- echte Class Library + Core Tests.
- App → Core ProjectReference.
- noch kein UI-Verhalten.
- Acceptance: Core Tests laufen ohne WinForms-Assembly.

## Phase 2 – Domain/Size/Capabilities

- `AlphaRequirement`, `ProviderCapabilities`, `ImageGenerationSpec`, `ImageSizePlanner`, Custom IDs, Retry.

## Phase 3 – OpenAI HTTP Client

- Direct generation, error parsing, request ID, Fake-Handler-Tests.

## Phase 4 – Generation Job Store

- atomare Persistence, Crash-/Restart-Semantik.

## Phase 5 – Settings/Secret Store

- DPAPI, neue nicht-sensitive Settings, Header Settings Button, Dialog, Connection Test.

## Phase 6 – Manifest V2

- V1 unverändert, V2 `alpha`, V2 Hash, Example/Conversion Prompt.

## Phase 7 – Generate Now

- Preflight, Confirmation, RateLimiter, Worker Queue, Staging/Normalization, Ready, Main Candidate, Tests.

## Phase 8 – Production Batch

- JSONL, Upload, Create, durable IDs, Poll, Output/Error ingest, Custom-ID mapping, Restart Resume, Tests.

## Phase 9 – Provenance

- OpenAI API Template, Candidate-Metadata, Renderer-Context, manuelle Replacement-Clearing, Tests.

## Phase 10 – SAC/Docs/Final Gate

- `AGENTS.md` korrigieren, README, Package-Secret-Prüfung, targeted Tests, clean build, final CI/trusted validation, Release Notes.

Keine Phasen in einen Big-Bang-Commit zusammenziehen.

---

# 37. Voraussichtlich geänderte bestehende Dateien

```text
AssetProvenanceHelper.sln
src/AssetProvenanceHelper/AssetProvenanceHelper.csproj
src/AssetProvenanceHelper/MainForm.cs
src/AssetProvenanceHelper/MainForm.Layout.cs
src/AssetProvenanceHelper/MainForm.RequestQueue.cs
src/AssetProvenanceHelper/Models/AppSettings.cs
src/AssetProvenanceHelper/Models/AssetRequestItem.cs
src/AssetProvenanceHelper/Models/ProviderRenderContext.cs
src/AssetProvenanceHelper/Services/AppBootstrap.cs
src/AssetProvenanceHelper/Services/SettingsService.cs
src/AssetProvenanceHelper/Services/AssetRequestManifestService.cs
src/AssetProvenanceHelper/examples/asset_request_manifest_template.json
src/AssetProvenanceHelper/examples/asset_request_conversion_prompt.txt
src/AssetProvenanceHelper/provider_templates/OpenAI API.md
AGENTS.md
README.md
coverage baselines erst nach fertiger Implementierung/Tests
```

---

# 38. Definition of Done

- [ ] manueller Webchat-/Request-Queue-Workflow unverändert nutzbar
- [ ] Settings Button rechts oben
- [ ] API-Key nie plaintext in normalen State-/Log-/Provenance-Dateien
- [ ] DPAPI CurrentUser Secret Store
- [ ] Generate Now verarbeitet alle geeigneten Pending Requests mit lokaler Rate-Limitierung
- [ ] `alpha=required` wird bei GPT-Image-2 vor HTTP blockiert
- [ ] Production Batch nutzt echte `/v1/images/generations` Batch Requests
- [ ] Batch-Resultate immer per `custom_id` gemappt
- [ ] Output und Error File verarbeitet
- [ ] Partial Results erhalten
- [ ] laufende Remote-Batches überleben App-Neustart
- [ ] unsichere Direct-InFlight-Requests werden nach Crash nicht automatisch doppelt gesendet
- [ ] API-Ergebnis wird `Ready`, nicht `Done`
- [ ] `Done` weiterhin nur nach bestehendem durable Main Commit
- [ ] vor Commit nur privates Staging, keine Asset-Root-Mutation
- [ ] Finalauflösung exakt Manifestauflösung
- [ ] 512x512 und 1920x1080 korrekt über SizePlanner unterstützt
- [ ] OpenAI API Provenance enthält Provider/Model/Request/Batch/Resolutions/Postprocess
- [ ] manuelle Main-Auswahl löscht alte API-Metadaten
- [ ] Core ist echte Class Library
- [ ] Core Tests referenzieren WinForms nicht
- [ ] SAC Exit 42 nie als Produktdefekt behandelt
- [ ] `dotnet <app>.dll` nicht als garantierter SAC-Bypass dokumentiert
- [ ] keine SAC-/Defender-Deaktivierung
- [ ] warning-free clean build
- [ ] finale dynamische Validierung in geeignetem Environment

---

# 39. Spätere Phase: Alpha-fähiger Provider

Nicht Teil des ersten Patches, aber Domain bereits dafür auslegen:

```text
alpha=required
   ↓
ProviderCapabilities
   ↓
native alpha-capable provider
   ↓
transparent PNG candidate
```

Wenn später Background Removal angeboten wird, muss die Provenance klar unterscheiden:

```text
native transparent generation
```

vs.

```text
opaque generation + background-removal post-process
```

Für Rauch, Glow, Nebel und semitransparente VFX darf Background Removal nicht still als gleichwertig zu nativer Alpha-Generation behandelt werden.

---

# 40. Technische Referenzen

OpenAI:
- https://developers.openai.com/api/docs/models/gpt-image-2
- https://developers.openai.com/api/docs/guides/image-generation
- https://developers.openai.com/api/docs/guides/batch

DPAPI:
- https://learn.microsoft.com/dotnet/api/system.security.cryptography.dataprotectionscope
- https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData

Repository zuerst lesen:
- `AGENTS.md`
- `src/AssetProvenanceHelper/MainForm.RequestQueue.cs`
- `src/AssetProvenanceHelper/MainForm.Layout.cs`
- `src/AssetProvenanceHelper/Models/AppSettings.cs`
- `src/AssetProvenanceHelper/Services/SettingsService.cs`
- `src/AssetProvenanceHelper/Services/AppBootstrap.cs`
- `src/AssetProvenanceHelper/Services/AssetRequestManifestService.cs`
- `src/AssetProvenanceHelper/examples/asset_request_conversion_prompt.txt`
- `C:\Projects\SACsolutions.md`

---

# 41. Kopierbarer Implementierungsauftrag für den KI-Agenten

```text
Implementiere exakt IMPLEMENTIERUNGSKONZEPT_API_BATCH_AUTOMATION.md.

VERBINDLICH:

1. Lies zuerst vollständig AGENTS.md, C:\Projects\SACsolutions.md und dieses Konzept.
2. Implementiere die Phasen in der festgelegten Reihenfolge. Kein Big-Bang-Refactor.
3. Bestehender manueller Webchat-/Request-Queue-Workflow darf nicht regressieren.
4. Automatisch generierte Bilder dürfen niemals automatisch in Asset Root committed
   oder als Done markiert werden. Nur staged Main Candidate; Done ausschließlich über
   den bestehenden durable Main Commit.
5. API-Key niemals plaintext speichern/loggen/ins Clipboard oder Provenance schreiben.
6. GPT-Image-2 unterstützt aktuell keine transparenten Hintergründe. alpha=required
   vor OpenAI HTTP blockieren; keine Fake-Transparenz.
7. Sämtliche neue Provider-/HTTP-/Batch-/Size-/Job-State-Logik in
   AssetProvenanceHelper.Core als echte Class Library. Core Tests referenzieren WinForms nicht.
8. SAC: niemals SAC/Defender deaktivieren; 0x800711C7 oder CodeIntegrity 3033/3077
   ist Environment Block; Exit 42 nicht als Produktfehler debuggen/reverten;
   dotnet <app>.dll ist KEIN garantierter Bypass.
9. Normale Tests niemals gegen echte kostenpflichtige OpenAI-Endpunkte ausführen.
10. Nach jeder Phase nur die relevanten targeted Tests und warning-free Builds;
    Full Suite nicht in engen Wiederholungsschleifen.
11. Erst am Ende verify_like_ci.ps1 auf sauberem Tree, soweit SAC erlaubt; andernfalls
    lokalen Environment Block korrekt dokumentieren und finale dynamische Prüfung auf
    CI/trusted non-enforcing environment durchführen.
12. Wenn eine technische Annahme dieses Plans inzwischen von aktueller offizieller
    OpenAI-/Microsoft-Dokumentation widerlegt ist, nur den minimal betroffenen Teil
    anhand der aktuellen Primärquelle korrigieren und die Abweichung dokumentieren.
```

---

# 42. Zielarchitektur in einem Bild

```text
                    EXISTING WINFORMS / PROVENANCE
                              │
             ┌────────────────┴────────────────┐
             │                                 │
     Manual Webchat                    New API UI bridge
       unchanged                              │
                                             ▼
                              AssetProvenanceHelper.Core
                                      │
                           ┌──────────┴──────────┐
                           │                     │
                   Standard Image API      Production Batch
                           │                     │
                           └──────────┬──────────┘
                                      ▼
                              Private Staging
                                      │
                                      ▼
                              Existing Main Slot
                                      │
                                      ▼
                          Existing Durable Commit
                                      │
                                      ▼
                                     Done
```

Diese Struktur erfüllt gleichzeitig Massengenerierung, günstigen Batch-Betrieb, den bestehenden Provenance-Sicherheitsrahmen, Crash-/Restart-Fähigkeit, spätere Multi-Provider-Erweiterbarkeit und eine deutlich SAC-freundlichere tägliche Entwicklung.
