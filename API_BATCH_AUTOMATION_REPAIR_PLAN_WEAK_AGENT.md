# API Batch Automation – Reparatur-, Hardening- und Testplan für einen schwachen KI-Agenten

**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Zielbranch:** `feature/api-batch-automation`  
**Audit-Ausgangspunkt:** `240b8d384332f06fde8c2504b41647974e0873d6`  
**Stabile Altbasis:** `main` / v1.4.1; `_looi1.md` gilt als bereits implementiert und getestet  
**Primäre Spezifikation:** aktuelle `IMPLEMENTIERUNGSKONZEPT_API_BATCH_AUTOMATION.md`  
**Zielgruppe:** schwacher KI-Agent; möglichst wenige eigene Designentscheidungen.

---

# 0. ZUERST LESEN – Spezifikation ist Autorität

Dieses Dokument ist ein Reparatur-/Ausführungsplan. Es ersetzt **nicht** die aktuelle API-Spezifikation.

Die aktuelle angehängte Spezifikation sagt ausdrücklich:

- `gpt-image-2` + `alpha=required` wird **vor HTTP blockiert**;
- kein Fake-Background-Removal;
- `DirectMaxConcurrency = 5`;
- HTTP Timeout ca. 3 Minuten;
- temporäre Netzwerkfehler dürfen nach aktueller Spezifikation retrybar sein;
- MVP-Staging/Normalisierung ist PNG-basiert;
- `MaxBatchRequestsPerSubmission = 500` ist lokaler Kosten-/Fehlklick-Schutz;
- kein verpflichtender `Paused`-Status;
- kein verpflichtender `Stop New API Starts`-Button;
- kein verpflichtendes automatisches Multi-Chunk-Batching oberhalb 500.

## 0.1 Frühere Auditpunkte, die NICHT umgesetzt werden dürfen

| Frühere Auditannahme | Entscheidung für diesen Repair |
|---|---|
| Alpha-required müsse `background=transparent` generieren | **NICHT UMSETZEN** |
| WebP/JPEG müsse automatisch Finalformat werden | **NICHT UMSETZEN** |
| Concurrency müsse 10 sein | **NICHT UMSETZEN** |
| HTTP Timeout müsse 5 Minuten sein | **NICHT UMSETZEN** |
| Netzwerkfehler dürften grundsätzlich nie retryen | **Nicht als Spezifikationsfehler behandeln** |
| Start-Limiter müsse zwingend gleichmäßig ohne Initial-Burst takten | **Nicht aus aktueller Spezifikation ableiten** |
| Queued Jobs müssten `Paused` werden | **Nicht gefordert** |
| Stop-New-Starts müsse ergänzt werden | **Nicht gefordert** |
| >500 Requests müssten automatisch partitioniert werden | **Nicht gefordert** |

Ein Agent darf diese Punkte nicht „reparieren“, nur weil sie in einer älteren Auditfassung auftauchten.

---

# 1. Nicht verhandelbare Regeln

1. Kein Big-Bang-Refactor.
2. Bestehender Main-Commit bleibt einzige `Done`-Autorität.
3. API-/Batch-Erfolg erzeugt nur `Ready`.
4. Manueller Webchat-/Request-Workflow bleibt erhalten.
5. Legacy Direct, Reference, Keep Settings und Variants aus `_looi1.md` nicht neu designen.
6. Kein echter OpenAI-Aufruf in normalen Unit-/Integrationstests.
7. Keine SAC-/Defender-Deaktivierung.
8. Jeder bestätigte Defekt bekommt einen Test, der ohne Fix fehlschlägt.
9. Bei Remote-/Billing-Grenzen sind Failure-Injection-Tests Pflicht.
10. Nach jeder Phase targeted Tests + warning-free Build, erst dann nächste Phase.

---

# 2. Priorisierte echte Fixes

## P0

### R-001 – Manifest-/Run-Kontext kann während async Direct Work wechseln

Worker greifen wiederholt auf `_currentManifest` und `_settings` zu. Ein Manifestwechsel oder Settingswechsel während eines Runs darf laufende Requests nicht unter einem anderen Fingerprint/Model/Quality fortführen.

**Ziel:** Run-Snapshot capturen; Import während lokaler API-Mutation sperren; Worker lesen keine mutable UI-Autorität.

### R-002 – Batch Submission hat nicht die geforderten Durability-Checkpoints

Gefordert:

```text
Preparing speichern
→ /files Upload
→ ProviderInputFileId SOFORT speichern
→ /batches Create
→ ProviderBatchId SOFORT speichern
→ Monitoring
```

Aktuell versteckt eine Provider-Methode Upload + Create zusammen.

### R-003 – Remote Batch kann existieren, obwohl lokaler Batch-ID-Save scheitert

Nach erfolgreichem Remote Create darf nie blind neu submitted werden.

### R-004 – Ready API Candidate kann durch Legacy Direct überschrieben werden

Aktiver API Candidate muss beim Main-Entry-Point Vorrang vor Auto-Select aus Downloads haben.

### R-005 – Raw Provider Output wird erst nach Normalisierung gespeichert

Bezahlt/erhaltenes Provider-Result zuerst atomic + flushed persistieren, dann normalisieren.

## P1

### R-006 – Preflight überspringt echte lokale Fehler still

`catch { continue; }` für ungültige Größen ist nicht akzeptabel. Erwartete Capability-Blocks separat zählen; echte lokale Errors müssen die kostenpflichtige Aktion vor Confirmation stoppen.

### R-007 – Terminaler Batch mit nur `error_file_id` wird nicht vollständig ingestiert

Terminal + Output **oder** Error muss Download/Parsing auslösen.

### R-008 – Batch `custom_id` Mapping muss fail-closed sein

Unbekannte/duplizierte IDs vor lokalen Item-Mutationen erkennen.

### R-009 – Direct `x-request-id` geht verloren

Header bis Candidate → Job → Metadata → Provenance durchreichen.

### R-010 – globaler 401/403/Model-Fehler stoppt neue Direct Starts nicht

Request-spezifische Fehler dürfen andere Items weiterlaufen lassen; globale Auth-/Permission-/Model-Fehler nicht.

### R-011 – Ready Candidate wird nicht vollständig auf Metadata/Hash/Dimension geprüft

Ready darf nicht nur „Datei existiert“ bedeuten.

### R-012 – gespeicherter API-Key wird beim Settings-Öffnen in die Textbox kopiert

Robuster: Textbox leer, `Configured: Yes`; nur explizites Replacement speichern; leere Textbox bei OK löscht vorhandenen Key nicht.

### R-013 – Connection Test kann Modellzugriff falsch positiv melden

`GET /models/gpt-image-2` muss selbst erfolgreich sein. Ein generisches `/models`-OK beweist nicht den konfigurierten Modellzugriff.

### R-014 – `DirectRetryAttempts` wird persistiert, aber muss den Produktionsclient wirklich steuern

Nicht nur JSON-Roundtrip testen.

### R-015 – Core muss eigenes Coverage-/Mutation-Gate bekommen

Die neue Core-Class-Library darf nicht außerhalb des Qualitätsdenominators stehen.

### R-016 – GenerationJobStore verursacht unnötig viele Full-State Reads/Writes

Für 100–1000 Assets Bulk Read/Upsert ergänzen.

---

# 3. Optionale Hardening-Punkte – erst nach Pflichtfixes

## H-01 – Generation-time Provider Template Snapshot

Ein API Candidate kann optional einen unveränderlichen `OpenAI API.md` Snapshot binden, damit spätere Dropdown-/Template-Änderungen seine Provenance nicht verändern.

## H-02 – Emergency Recovery Marker

Wenn Remote Batch ID bekannt ist, aber der normale JobStore gerade nicht persistieren kann, best-effort Recovery-Datei ohne Secret schreiben.

## H-03 – rein lokaler Re-Normalize aus gespeichertem Raw Output

Lokaler Codec-/Disk-Fehler soll nicht zwingend einen neuen paid Request erfordern.

## H-04 – strengere Netzwerk-Ambiguität

Nur als spätere Spezifikationsänderung. Aktuelle Spezifikation erlaubt temporäre Netzwerk-Retries.

---

# 4. Phase 0 – Baseline einfrieren

Ausführen:

```powershell
git status --short
git branch --show-current
git rev-parse HEAD
git log -1 --oneline

dotnet restore AssetProvenanceHelper.sln

dotnet build tests/AssetProvenanceHelper.Core.Tests/AssetProvenanceHelper.Core.Tests.csproj `
  -c Release -warnaserror

dotnet test tests/AssetProvenanceHelper.Core.Tests/AssetProvenanceHelper.Core.Tests.csproj `
  -c Release --no-build
```

Soweit SAC erlaubt:

```powershell
dotnet build AssetProvenanceHelper.sln -c Release -warnaserror
dotnet test AssetProvenanceHelper.sln -c Release --no-build
```

Baseline dokumentieren, z. B. `docs/audits/api-repair-baseline.md`.

---

# 5. Phase 1 – Source-of-Truth-Regressions zuerst absichern

Diese Tests sollen verhindern, dass der Agent die falsche ältere Spezifikation implementiert.

Pflicht:

```text
gpt-image-2 + alpha required → block before HTTP
alpha not_required → allowed
alpha unknown → allowed
API success → Ready, not Done
Ready → existing durable Main commit → Done
V1 manifest remains accepted
manual webchat workflow unchanged
legacy Direct unchanged when no API candidate is active
Reference unchanged
Keep Settings unchanged
Variants existing behavior unchanged
```

Beispiel Alpha-Test:

```csharp
[Fact]
public async Task GptImage2_AlphaRequired_IsBlockedBeforeHttp()
{
    var handler = new CountingHttpMessageHandler();
    using var http = new HttpClient(handler)
    {
        BaseAddress = OpenAiApiClient.DefaultBaseUri
    };

    using var client = new OpenAiApiClient(http);
    var provider = new OpenAiImageGenerationProvider(client);
    var spec = TestSpecs.Create(alpha: AlphaRequirement.Required);

    await Assert.ThrowsAsync<InvalidOperationException>(
        () => provider.GenerateAsync(spec, "fake-key"));

    Assert.Equal(0, handler.CallCount);
}
```

---

# 6. Phase 2 – Unveränderlichen API Run Snapshot einführen

## 6.1 Neue Datei

`src/AssetProvenanceHelper/Models/ApiGenerationRunSnapshot.cs`

```csharp
namespace AssetProvenanceHelper.Models;

public sealed record ApiGenerationRunSnapshot(
    string ManifestFingerprint,
    string ProviderId,
    string Model,
    string Quality,
    int DirectStartsPerMinute,
    int DirectMaxConcurrency,
    int DirectRetryAttempts,
    DateTimeOffset CreatedAtUtc);
```

## 6.2 Snapshot bei Confirmation erstellen

```csharp
var manifest = _currentManifest
    ?? throw new InvalidOperationException(
        "Manifest disappeared before generation start.");

var run = new ApiGenerationRunSnapshot(
    ManifestFingerprint: manifest.ManifestFingerprint,
    ProviderId: "OpenAI",
    Model: _settings.OpenAiModel,
    Quality: _settings.DirectImageQuality,
    DirectStartsPerMinute: _settings.DirectStartsPerMinute,
    DirectMaxConcurrency: _settings.DirectMaxConcurrency,
    DirectRetryAttempts: _settings.DirectRetryAttempts,
    CreatedAtUtc: DateTimeOffset.UtcNow);

_ = RunDirectGenerationAsync(eligible, apiKey, run);
```

## 6.3 Worker-Signatur

```csharp
private async Task RunDirectGenerationAsync(
    IReadOnlyList<AssetRequestItem> items,
    string apiKey,
    ApiGenerationRunSnapshot run)
```

Im Worker **keine** Reads von `_currentManifest.ManifestFingerprint`, `_settings.OpenAiModel`, `_settings.DirectImageQuality`, Rate/Retry-Settings. Stattdessen nur `run.*`.

## 6.4 Import während lokaler API-Mutation blockieren

In `ApplyRequestQueueState()`:

```csharp
var apiMutationActive =
    _isGeneratingDirect || _isSubmittingBatch;

if (_state != UiState.ReferenceReady)
{
    btnImportRequest.Enabled = !apiMutationActive;

    var canRunApi =
        _currentManifest is not null
        && !apiMutationActive;

    btnGenerateNow.Enabled = canRunApi;
    btnQueueProductionBatch.Enabled = canRunApi;
}
```

Zusätzlich am Anfang von `HandleImportRequest()`:

```csharp
if (_isGeneratingDirect || _isSubmittingBatch)
{
    ShowMessageBox(
        "A generation or batch submission is currently being prepared. "
        + "Wait until the local operation has finished before importing another manifest.",
        "Import blocked",
        MessageBoxButtons.OK,
        MessageBoxIcon.Warning);
    return;
}
```

## 6.5 Tests

```text
DirectRun_CapturesManifestFingerprintOnce
DirectRun_UsesCapturedModelAndQuality
SettingsChangedMidRun_DoesNotChangeExistingRun
ImportDisabledWhileDirectRunActive
ImportDisabledWhileBatchSubmissionActive
ProgrammaticImportWhileRunActive_IsRejected
ManifestA_RunCannotWriteUnderManifestB
```

Race-Test mit blockierendem Fake Provider: Run A starten, Provider blockieren, Import B versuchen, Provider freigeben, ausschließlich Fingerprint A in Job/Staging erwarten.

---

# 7. Phase 3 – Zentraler Preflight statt `catch { continue; }`

Empfohlene DTOs:

```csharp
public sealed record ApiPreflightIssue(
    string RequestKey,
    string FileName,
    string Code,
    string Message);

public sealed record ApiPreflightResult(
    IReadOnlyList<AssetRequestItem> Eligible,
    IReadOnlyList<AssetRequestItem> BlockedAlpha,
    IReadOnlyList<ApiPreflightIssue> Errors,
    IReadOnlyList<ApiPreflightIssue> Warnings);
```

Regeln:

- `alpha=required` → erwarteter Capability-Block, nicht fataler Manifestfehler;
- ungültige Dimension → Error;
- leerer Prompt → Error;
- Ready/Done/InFlight → nicht eligible;
- Uncertain → nicht automatisch neu submitten;
- `Errors.Count > 0` → keine kostenpflichtige Confirmation/Submission.

Ersetzen:

```csharp
try { ImageSizePlanner.Plan(...); }
catch { continue; }
```

mit:

```csharp
try
{
    _ = ImageSizePlanner.Plan(item.Width, item.Height);
}
catch (Exception ex)
{
    errors.Add(new ApiPreflightIssue(
        item.RequestKey,
        item.FileName,
        "invalid_generation_size",
        ex.Message));
    continue;
}
```

Tests:

```text
Preflight_AlphaRequired_IsBlockedNotError
Preflight_InvalidSize_ProducesError
Preflight_OneInvalidOneValid_StartsNoPaidRequest
Preflight_Ready_NotEligible
Preflight_Done_NotEligible
Preflight_Uncertain_NotEligible
Preflight_Opaque_Eligible
Preflight_UnknownAlpha_Eligible
```

---

# 8. Phase 4 – API Ready Candidate hat Vorrang vor Legacy Direct

Minimalfix in `MainForm.DirectMode.cs`:

```csharp
private void HandleMainImageEntryPoint()
{
    if (_activeApiCandidateMetadata is not null)
    {
        HandleMainImage();
        return;
    }

    if (!chkDirectMode.Checked)
    {
        HandleMainImage();
        return;
    }

    HandleDirectMainImage();
}
```

Tests:

```text
ApiCandidate_LegacyDirectChecked_CommitsApiCandidate
ApiCandidate_NewerDownloadExists_DoesNotReplaceCandidate
NoApiCandidate_LegacyDirectChecked_OldBehaviorUnchanged
```

Hash der tatsächlich committed Source prüfen, nicht nur UI-Label.

---

# 9. Phase 5 – Raw Provider Output VOR Normalisierung persistieren

Aktuell falsch:

```text
provider response → normalize in memory → save raw/final
```

Ziel:

```text
provider response
→ raw basic validation
→ raw atomic write + Flush(true)
→ raw hash
→ job Normalizing + raw path
→ normalize
→ final atomic write
→ metadata atomic write
→ Ready
```

`GenerationItemRecord` optional am Ende erweitern:

```csharp
string? CandidateId = null,
string? ProviderRawPath = null,
```

`GeneratedImageStagingService` teilen:

```csharp
public string SaveRawCandidate(
    string manifestFingerprint,
    string requestKey,
    string candidateId,
    byte[] rawBytes)
```

und:

```csharp
public string CompleteCandidate(
    string manifestFingerprint,
    string requestKey,
    string candidateId,
    byte[] normalizedBytes,
    ApiCandidateMetadata metadata)
```

No-overwrite helper:

```csharp
private static void WriteBytesAtomicNoOverwrite(
    string destinationPath,
    byte[] bytes)
{
    if (File.Exists(destinationPath))
        throw new IOException($"Destination already exists: {destinationPath}");

    var tempPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
    try
    {
        using (var stream = new FileStream(
                   tempPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }

        File.Move(tempPath, destinationPath, overwrite: false);
    }
    finally
    {
        try { if (File.Exists(tempPath)) File.Delete(tempPath); }
        catch { }
    }
}
```

Tests:

```text
RawSave_HappensBeforeNormalization
NormalizationThrows_RawStillExists
NormalizationThrows_ItemNeverReady
CandidateCollision_DoesNotOverwriteRaw
CandidateCollision_DoesNotOverwriteFinal
MetadataWriteFails_ItemNeverReady
```

---

# 10. Phase 6 – Ready Candidate verifizieren

Neue Resulttypen:

```csharp
public sealed record VerifiedApiCandidate(
    string ImagePath,
    ApiCandidateMetadata Metadata);

public sealed record CandidateVerificationResult(
    bool IsValid,
    VerifiedApiCandidate? Candidate,
    string? ErrorCode,
    string? ErrorMessage);
```

`VerifyCandidate(job)` prüft mindestens:

1. Staged File existiert;
2. Metadata existiert und deserialisiert;
3. Candidate ID passt;
4. Job SHA passt zur Metadata;
5. tatsächlicher File SHA passt;
6. PNG dekodierbar;
7. Zielauflösung stimmt;
8. Pfad liegt im erwarteten Staging-Verzeichnis.

SHA helper:

```csharp
private static string ComputeSha256File(string path)
{
    using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read);

    return Convert.ToHexString(
        SHA256.HashData(stream))
        .ToLowerInvariant();
}
```

Bei Invalid:

- nicht Main binden;
- nicht committen;
- Job sichtbar als lokaler Candidate-Fehler markieren;
- niemals automatisch remote regenerieren.

Tests:

```text
Ready_MetadataMissing_FailsClosed
Ready_MetadataCorrupt_FailsClosed
Ready_FileMissing_FailsClosed
Ready_HashMismatch_FailsClosed
Ready_WrongDimensions_FailsClosed
Ready_Valid_LoadsMain
Ready_Invalid_NeverCommits
```

---

# 11. Phase 7 – Batch Submission API an Durability-Grenzen aufteilen

## 11.1 Provider Interface

Bevorzugte Form:

```csharp
public sealed record BatchInputUploadResult(
    string ProviderInputFileId,
    DateTimeOffset CreatedAtUtc);

public interface IImageGenerationProvider
{
    string ProviderId { get; }
    ProviderCapabilities GetCapabilities(string model);

    Task<ImageGenerationCandidate> GenerateAsync(
        ImageGenerationSpec spec,
        string apiKey,
        CancellationToken cancellationToken = default);

    Task<BatchInputUploadResult> UploadBatchInputAsync(
        IReadOnlyList<ImageGenerationSpec> specs,
        string apiKey,
        CancellationToken cancellationToken = default);

    Task<BatchSubmissionResult> CreateBatchAsync(
        string providerInputFileId,
        int submittedCount,
        string apiKey,
        CancellationToken cancellationToken = default);

    Task<BatchStatusResult> GetBatchStatusAsync(
        string providerBatchId,
        string apiKey,
        CancellationToken cancellationToken = default);

    Task<BatchDownloadResult> DownloadBatchResultsAsync(
        BatchStatusResult completedBatch,
        string apiKey,
        CancellationToken cancellationToken = default);
}
```

Altes kombiniertes `SubmitBatchAsync` möglichst entfernen, damit niemand die Recovery-Grenze erneut umgeht.

## 11.2 OpenAI Provider

```csharp
public async Task<BatchInputUploadResult> UploadBatchInputAsync(
    IReadOnlyList<ImageGenerationSpec> specs,
    string apiKey,
    CancellationToken cancellationToken = default)
{
    var jsonl = OpenAiBatchJsonlBuilder.Build(specs);
    var file = await _client.UploadBatchFileAsync(
        jsonl,
        "batch.jsonl",
        apiKey,
        cancellationToken).ConfigureAwait(false);

    return new BatchInputUploadResult(
        file.Id,
        DateTimeOffset.UtcNow);
}
```

```csharp
public async Task<BatchSubmissionResult> CreateBatchAsync(
    string providerInputFileId,
    int submittedCount,
    string apiKey,
    CancellationToken cancellationToken = default)
{
    var batch = await _client.CreateBatchAsync(
        providerInputFileId,
        apiKey,
        cancellationToken).ConfigureAwait(false);

    return new BatchSubmissionResult(
        ProviderInputFileId: providerInputFileId,
        ProviderBatchId: batch.Id,
        SubmittedCount: submittedCount,
        CreatedAtUtc: DateTimeOffset.UtcNow);
}
```

## 11.3 Controller exakt in dieser Reihenfolge

```csharp
_generationJobStore.UpsertBatch(batchRecord with { Status = "preparing" });

var upload = await _imageGenerationProvider.UploadBatchInputAsync(
    specs, apiKey).ConfigureAwait(false);

batchRecord = batchRecord with
{
    ProviderInputFileId = upload.ProviderInputFileId,
    UpdatedAtUtc = DateTimeOffset.UtcNow
};

// Critical checkpoint #1
_generationJobStore.UpsertBatch(batchRecord);

var remote = await _imageGenerationProvider.CreateBatchAsync(
    upload.ProviderInputFileId,
    specs.Count,
    apiKey).ConfigureAwait(false);

batchRecord = batchRecord with
{
    ProviderBatchId = remote.ProviderBatchId,
    Status = "submitted",
    UpdatedAtUtc = DateTimeOffset.UtcNow
};

// Critical checkpoint #2
_generationJobStore.UpsertBatch(batchRecord);
```

## 11.4 Save nach Remote Create schlägt fehl

Dann:

- **kein Resubmit**;
- Remote Batch ID im Fehlerdialog anzeigen;
- best-effort Emergency Recovery Marker;
- Run fail-closed.

Beispiel Warnung:

```text
CRITICAL: OpenAI returned remote batch ID batch_123,
but local recovery state could not be saved.
DO NOT submit these requests again until the remote batch has been checked.
```

## 11.5 Failure-Injection-Tests

```text
PreparingSaveFails_NoUpload
UploadSucceeds_InputIdSaveFails_NoCreateBatch
InputIdSaved_CreateBatchFails_NoSubmittedState
CreateBatchSucceeds_BatchIdSavedImmediately
CreateBatchSucceeds_BatchIdSaveFails_NoResubmit
BatchIdSaveFails_WarningContainsRemoteBatchId
```

---

# 12. Phase 8 – Batch Results vollständig und fail-closed

Terminal result files:

```csharp
var hasAnyResultFile =
    !string.IsNullOrWhiteSpace(status.OutputFileId)
    || !string.IsNullOrWhiteSpace(status.ErrorFileId);

if (isTerminal && hasAnyResultFile)
{
    // ingest output and/or error
}
```

Vor Download/Import IDs am Batch Record speichern:

```csharp
_generationJobStore.UpsertBatch(batch with
{
    ProviderOutputFileId = status.OutputFileId,
    ProviderErrorFileId = status.ErrorFileId,
    CompletedCount = status.CompletedCount,
    FailedCount = status.FailedCount,
    UpdatedAtUtc = DateTimeOffset.UtcNow
});
```

Mapping **vor** Item-Mutation validieren:

```csharp
var expected = batchItems.ToDictionary(
    item => item.CustomId,
    StringComparer.Ordinal);

var seen = new HashSet<string>(StringComparer.Ordinal);

foreach (var output in results.Items)
{
    if (!seen.Add(output.CustomId))
        throw new InvalidDataException(
            $"Duplicate custom_id: {output.CustomId}");

    if (!expected.ContainsKey(output.CustomId))
        throw new InvalidDataException(
            $"Unknown custom_id: {output.CustomId}");
}
```

Dann erst Items verarbeiten.

Bei `expired`:

- vorhandene erfolgreiche Resultate → Ready;
- explizite Fehler → Failed;
- nicht erschienene Items → Fehlerstatus;
- erfolgreiche bereits ingestierte Items nie wieder überschreiben.

Tests:

```text
OutOfOrder_MapsByCustomId
DuplicateCustomId_FailsBeforeMutation
UnknownCustomId_FailsBeforeMutation
Terminal_ErrorFileOnly_Downloads
Terminal_OutputAndError_DownloadsBoth
Expired_PartialSuccessReady
Expired_MissingItemsFailed
MixedSuccessError_PreservesSuccess
MalformedBase64_OnlyAffectedItemFails
```

---

# 13. Phase 9 – `x-request-id` Direct Ende-zu-Ende

Neue Envelope:

```csharp
public sealed record OpenAiImageGenerationHttpResult(
    OpenAiImageGenerationResponse Response,
    string? RequestId);
```

Client bei Erfolg:

```csharp
return new OpenAiImageGenerationHttpResult(
    result,
    requestId);
```

Provider:

```csharp
var httpResult = await _client.GenerateImageAsync(...);

return new ImageGenerationCandidate(
    CandidateId: candidateId,
    CustomId: spec.CustomId,
    RawBytes: rawBytes,
    RawSha256: rawSha256,
    ProviderWidth: spec.GenerationWidth,
    ProviderHeight: spec.GenerationHeight,
    ProviderRequestId: httpResult.RequestId);
```

Tests:

```text
HTTP x-request-id → client result
→ provider candidate
→ staging metadata
→ final provenance
```

---

# 14. Phase 10 – globale Direct-Fehler stoppen neue Starts

Helper:

```csharp
private static bool IsGlobalGenerationFailure(Exception ex)
{
    if (ex is not OpenAiApiException api)
        return false;

    return api.StatusCode is
        HttpStatusCode.Unauthorized
        or HttpStatusCode.Forbidden
        or HttpStatusCode.NotFound;
}
```

Bei globalem Fehler:

- keine neuen Starts;
- bereits laufende Tasks sauber auslaufen lassen, soweit möglich;
- UI eine globale Meldung;
- noch nicht gestartete Items nicht als billable uncertain markieren.

Tests:

```text
401_FirstRequest_StopsNewStarts
403_FirstRequest_StopsNewStarts
404Model_FirstRequest_StopsNewStarts
PromptUserError_DoesNotStopOtherItems
```

---

# 15. Phase 11 – `DirectRetryAttempts` produktiv verdrahten

Bevorzugt per per-run Execution Options, nicht mutable global state.

```csharp
public sealed record ImageGenerationExecutionOptions(
    int MaxAttempts);
```

Interface:

```csharp
Task<ImageGenerationCandidate> GenerateAsync(
    ImageGenerationSpec spec,
    ImageGenerationExecutionOptions options,
    string apiKey,
    CancellationToken cancellationToken = default);
```

MainForm:

```csharp
var options = new ImageGenerationExecutionOptions(
    run.DirectRetryAttempts);
```

Client erhält `maxAttempts` für genau diesen Call.

Tests:

```text
RetryAttempts1_OneHttpAttempt
RetryAttempts3_AtMostThreeAttempts
SettingChangedBetweenRuns_NewRunUsesNewValue
SettingChangedMidRun_CurrentRunKeepsSnapshot
```

Wichtig: aktuelle Retry-Klassifikation der Primärspezifikation beibehalten.

---

# 16. Phase 12 – API-Key Settings UX härten

Ziel:

```text
API Key: [empty password box]
Configured: Yes
[Save/Replace] [Delete] [Test Connection]
```

Nicht gespeicherten Key in `txtApiKey.Text` materialisieren.

Felder:

```csharp
private bool _hasStoredApiKey;
private Label lblApiKeyState = null!;
```

Load:

```csharp
_hasStoredApiKey =
    !string.IsNullOrEmpty(
        _secretStore.LoadSecret(OpenAiApiKeySecretName));

txtApiKey.Clear();
lblApiKeyState.Text =
    _hasStoredApiKey ? "Configured: Yes" : "Configured: No";
```

**Kritisch:** `ApplySettings()` darf bei leerer Textbox nicht mehr `DeleteSecret()` aufrufen.

```csharp
var replacement = txtApiKey.Text.Trim();
if (!string.IsNullOrEmpty(replacement))
{
    _secretStore.SaveSecret(OpenAiApiKeySecretName, replacement);
    _hasStoredApiKey = true;
    txtApiKey.Clear();
}
```

Delete nur über Delete-Button.

Connection Test Key:

```csharp
var typed = txtApiKey.Text.Trim();
var key = !string.IsNullOrEmpty(typed)
    ? typed
    : _secretStore.LoadSecret(OpenAiApiKeySecretName);
```

Tests:

```text
ExistingSecret_TextBoxEmpty
ExistingSecret_ConfiguredYes
OKBlank_DoesNotDeleteExisting
Replace_SavesAndClearsTextbox
Delete_Deletes
ConnectionTest_UsesStoredKeyWhenTextboxEmpty
settings.json_NoKey
generation-jobs.json_NoKey
metadata_NoKey
```

---

# 17. Phase 13 – Connection Test muss ausgewähltes Modell prüfen

Kein Fallback:

```text
GET /models/gpt-image-2 fails
→ GET /models succeeds
→ false OK
```

Stattdessen nur ausgewählten Model Endpoint prüfen.

```csharp
public async Task<bool> TestConnectionAsync(
    string apiKey,
    string model = "gpt-image-2",
    CancellationToken cancellationToken = default)
{
    using var request = new HttpRequestMessage(
        HttpMethod.Get,
        $"models/{Uri.EscapeDataString(model)}");

    request.Headers.Authorization =
        new AuthenticationHeaderValue("Bearer", apiKey);

    using var response = await _httpClient.SendAsync(
        request,
        cancellationToken).ConfigureAwait(false);

    if (response.IsSuccessStatusCode)
        return true;

    var body = await response.Content.ReadAsStringAsync(
        cancellationToken).ConfigureAwait(false);

    throw OpenAiErrorParser.Parse(
        response.StatusCode,
        body,
        GetRequestId(response));
}
```

UI-Hinweis:

```text
Connection test checks authentication and access to the selected model.
It does not perform a billable image generation.
```

Tests:

```text
SelectedModel200_True
SelectedModel404_GenericListWould200_StillFails
401_Fails
NoImagesGenerationPost
```

---

# 18. Phase 14 – GenerationJobStore Bulk Read/Write

Queue Rendering darf nicht pro Row `generation-jobs.json` laden.

Neue API:

```csharp
public IReadOnlyDictionary<string, GenerationItemRecord> GetItemsForManifest(
    string manifestFingerprint)
{
    lock (_lock)
    {
        var state = LoadCore();
        return state.Items
            .Where(i => string.Equals(
                i.ManifestFingerprint,
                manifestFingerprint,
                StringComparison.Ordinal))
            .ToDictionary(i => i.RequestKey, StringComparer.Ordinal);
    }
}
```

Bulk Upsert:

```csharp
public void UpsertItems(IEnumerable<GenerationItemRecord> items)
{
    ArgumentNullException.ThrowIfNull(items);

    lock (_lock)
    {
        var state = LoadCore();

        foreach (var item in items)
        {
            var index = state.Items.FindIndex(existing =>
                string.Equals(existing.ManifestFingerprint,
                    item.ManifestFingerprint,
                    StringComparison.Ordinal)
                && string.Equals(existing.RequestKey,
                    item.RequestKey,
                    StringComparison.Ordinal));

            if (index >= 0) state.Items[index] = item;
            else state.Items.Add(item);
        }

        SaveCore(state);
    }
}
```

Queue:

```csharp
var jobsByKey = _currentManifest is null
    ? new Dictionary<string, GenerationItemRecord>()
    : _generationJobStore.GetItemsForManifest(
        _currentManifest.ManifestFingerprint);
```

Dann Status-Funktion bekommt Dictionary, keine File-I/O.

Tests nicht per Millisekunden, sondern per Save-Hook/Counter:

```text
UpsertItems_500_PerformsOneSave
GetItemsForManifest_OnlyRequestedManifest
QueueRefresh_UsesOneManifestSnapshot
BulkUpsert_Roundtrip
```

---

# 19. Phase 15 – ProtectedData Build absichern

Die Primärspezifikation nennt:

```xml
<PackageReference Include="System.Security.Cryptography.ProtectedData" Version="10.0.11" />
```

Falls noch nicht vorhanden, in das Windows-App-Projekt aufnehmen und real bauen.

```powershell
dotnet restore AssetProvenanceHelper.sln
dotnet build AssetProvenanceHelper.sln -c Release -warnaserror
```

Keine Preview-Package-Version.

---

# 20. Phase 16 – separates Core Coverage Gate

Bestehenden App-Coverage-Gate nicht unnötig destabilisieren.

Neue Dateien:

```text
scripts/verify_core_coverage.ps1
code-coverage-core-baseline.json
```

CI:

```yaml
- name: Test Core with Coverage
  run: >
    dotnet test
    tests/AssetProvenanceHelper.Core.Tests/AssetProvenanceHelper.Core.Tests.csproj
    -c Release
    --no-build
    --collect:"XPlat Code Coverage"
    --results-directory artifacts/core-coverage

- name: Verify Core Coverage
  shell: pwsh
  run: pwsh scripts/verify_core_coverage.ps1
```

Inventory Root:

```powershell
$srcRoot = Join-Path $repoRoot "src/AssetProvenanceHelper.Core"
```

Baseline **erst nach echtem Run** mit echten Counts aktualisieren.

---

# 21. Phase 17 – Core Mutation Testing

Neue Stryker-Konfiguration für Core statt nur App-Projekt.

Beispiel:

```json
{
  "stryker-config": {
    "project": "AssetProvenanceHelper.Core.csproj",
    "solution": "../../AssetProvenanceHelper.sln",
    "reporters": ["progress", "html", "json"],
    "thresholds": {
      "high": 90,
      "low": 80,
      "break": 80
    },
    "mutate": ["**/*.cs"]
  }
}
```

Gezielt wichtig:

- RetryPolicy;
- GenerationJobStore;
- GenerationCustomId;
- OpenAiBatchJsonlBuilder;
- OpenAiBatchResultParser;
- ImageSizePlanner;
- Fehlerklassifikation;
- Preflight.

---

# 22. Failure-Injection-Testmatrix

| Grenze | injizierter Fehler | Erwartung |
|---|---|---|
| Initial Generation State Save | Disk/Hook fail | kein HTTP |
| Direct HTTP Success → Raw Write | write fail | nicht Ready |
| Raw Write → Normalize | normalize fail | Raw bleibt |
| Normalize → Final Staging | promote fail | nicht Ready |
| Metadata Save | fail | nicht Ready |
| Batch Preparing Save | fail | kein Upload |
| Upload → InputId Save | fail | kein Create Batch |
| InputId Save → Create Batch | HTTP fail | nicht submitted |
| Create Batch → BatchId Save | fail | kein Auto-Resubmit, Remote ID warnen |
| Poll → Download | fail | Batch weiter monitorbar |
| Result Validation | unknown ID | keine Item-Mutation |
| Result Validation | duplicate ID | keine Item-Mutation |
| Partial Import | ein Normalize fail | andere valide Resultate behalten |
| Secret Save | fail | kein falsches Configured |
| generation-jobs JSON corrupt | load | expliziter Fehler, nicht still leer |

---

# 23. Vollständige Testmatrix

## Core Capability

```text
alpha required block before HTTP
alpha not_required allowed
alpha unknown allowed
```

## Size Planner

```text
512x512 valid
1920x1080 valid canvas/crop
1024x1024 valid
ratio >3 reject
max edge reject
max pixels reject
all provider edges divisible by 16
```

## Retry

```text
408 retry
429 retry
500/502/503/504 retry
current-spec temporary network retry behavior
400 no retry
401 no retry
403 no retry
404 model/endpoint no retry
Retry-After
MaxAttempts
```

## Batch Builder

```text
one JSON/line
UTF8 no BOM
POST /v1/images/generations
n=1
background opaque
single model
unique custom_id
alpha required rejected
no secret
roundtrip every line
```

## Batch Parser/Integration

```text
out-of-order
success/error
partial
expired
malformed base64
duplicate custom_id fail closed
unknown custom_id fail closed
error-only file
output+error
```

## Job Store

```text
roundtrip
atomic save
promotion failure keeps old state
corrupt state throws
no key
bulk upsert
manifest snapshot
DirectInFlight restart → Uncertain
BatchSubmitted + remote id resumes
Ready survives restart
```

## WinForms

```text
Settings visible
Help usable
Generate requires manifest/key
API success Ready not Done
Ready loads Main
legacy Direct cannot replace API candidate
manual Main replacement clears API metadata
Main durable commit Done
failed API never Done
manifest import atomic
manifest import blocked during local API mutation
V1 manual workflow unchanged
Reference unchanged
Keep Settings unchanged
Variants unchanged
```

---

# 24. Test-Seams, die ausdrücklich erlaubt sind

JobStore:

```csharp
internal static Action<GenerationState>? OnBeforeSaveCoreForTests;
```

Staging:

```csharp
internal static Action<string>? OnBeforeCandidatePromoteForTests;
```

Blockierender Fake Provider:

```csharp
private sealed class BlockingFakeProvider : IImageGenerationProvider
{
    public TaskCompletionSource<bool> AllowResponse { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int GenerateCalls;

    // GenerateAsync increments counter, awaits AllowResponse, returns fake PNG.
}
```

Jeder statische Hook im Test-`finally` zurücksetzen.

---

# 25. Optional H-01 – Generation-time Provider Snapshot

Erst nach grünem Pflichtumfang.

```csharp
public sealed record ApiProviderSnapshot(
    string FileName,
    string DisplayName,
    string Content,
    string ContentSha256);
```

`ApiCandidateMetadata` optional erweitern und Snapshot im Candidate-Metadata-JSON speichern. Beim Commit API-Snapshot statt aktuelle Dropdown-Auswahl nutzen.

Test:

```text
Ready candidate
→ provider dropdown ändern
→ OpenAI API.md auf Disk ändern
→ commit
→ provenance bleibt generation-time snapshot
```

---

# 26. Was NICHT nebenbei geändert werden darf

- kein kompletter UI-Redesign;
- kein OpenRouter/FAL/Ideogram jetzt;
- keine Reference-Assisted API Generation;
- kein `n>1`;
- kein Background Removal;
- kein transparent GPT-Image-2 gegen aktuelle Spezifikation;
- kein WebP/JPEG-Transcode gegen PNG-MVP;
- keine Datenbankmigration;
- kein Gesamt-MVVM-Refactor;
- kein SDK-Wechsel ohne zwingenden Grund;
- keine SAC-Workarounds;
- kein Versions-/Release-Bump vor finaler Abnahme.

---

# 27. Empfohlene kleine Commits

```text
fix/api-run-context-and-preflight
fix/api-ready-candidate-direct-guard
fix/api-staging-durability
fix/batch-submission-checkpoints
fix/batch-result-validation
fix/api-request-id-and-global-errors
fix/api-settings-secret-and-retry-wiring
perf/generation-job-store-bulk-operations
test/core-coverage-and-mutation-gates
test/api-regression-matrix
```

---

# 28. Test-Commands pro Phase

Core:

```powershell
dotnet build tests/AssetProvenanceHelper.Core.Tests/AssetProvenanceHelper.Core.Tests.csproj `
  -c Release -warnaserror

dotnet test tests/AssetProvenanceHelper.Core.Tests/AssetProvenanceHelper.Core.Tests.csproj `
  -c Release --no-build
```

WinForms targeted:

```powershell
powershell -File scripts/run_tests_sac_safe.ps1 -Filter "FullyQualifiedName~Api"
```

Exit 42 = Environment Block dokumentieren, nicht Produktcode zurückrollen.

Nach größeren Blöcken:

```powershell
dotnet build AssetProvenanceHelper.sln -c Release -warnaserror
```

---

# 29. Finale lokale Abnahme

```powershell
git status --short
git diff --check

dotnet restore AssetProvenanceHelper.sln

dotnet build AssetProvenanceHelper.sln -c Debug --no-restore -warnaserror
dotnet test AssetProvenanceHelper.sln -c Debug --no-build

dotnet build AssetProvenanceHelper.sln -c Release --no-restore -warnaserror
dotnet test AssetProvenanceHelper.sln -c Release --no-build

dotnet test tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release --no-build --filter "Category=RecoveryCritical"
```

Core Coverage:

```powershell
dotnet test tests/AssetProvenanceHelper.Core.Tests/AssetProvenanceHelper.Core.Tests.csproj `
  -c Release --no-build `
  --collect:"XPlat Code Coverage" `
  --results-directory artifacts/core-coverage

pwsh scripts/verify_core_coverage.ps1
```

App Coverage: bestehenden Gate weiter ausführen.

20x Flakiness erst ganz am Ende:

```powershell
for ($i = 1; $i -le 20; $i++) {
    dotnet test AssetProvenanceHelper.sln `
      -c Release --no-build `
      --logger "console;verbosity=minimal"

    if ($LASTEXITCODE -ne 0) {
        throw "Flakiness run $i failed"
    }
}
```

---

# 30. Manuelle Smoke Cases

## A – Manual Legacy

1. Ohne API-Key starten.
2. V1 Manifest importieren.
3. Request anklicken.
4. Prompt kopiert.
5. externes Bild auswählen.
6. Main commit.
7. Done.

Muss wie vorher funktionieren.

## B – API Queue

V2 mit:

- opaque;
- unknown;
- alpha required.

Erwartung:

- opaque/unknown eligible;
- alpha blocked;
- Successes Ready;
- nichts automatisch Done;
- Ready anklicken → Staged Main;
- Main commit → Done.

## C – Batch

1. Submit.
2. Prüfen, dass Input File ID vor Create dauerhaft wird.
3. Remote Batch ID direkt nach Create dauerhaft.
4. App restart.
5. Monitoring resumed.
6. Output+Error ingest.
7. Ready commitbar.

## D – Legacy Direct Konflikt

1. API Candidate Ready.
2. Legacy Direct checked.
3. neueres unrelated Download-Bild vorhanden.
4. Main commit.
5. API Candidate muss gewinnen.

---

# 31. Definition of Done

- [ ] aktuelle Primärspezifikation nicht durch ältere Auditannahmen überschrieben
- [ ] `alpha=required` bleibt vor OpenAI HTTP geblockt
- [ ] V1/manueller Webchat unverändert
- [ ] `_looi1.md` Features nicht regressiert
- [ ] API success = Ready, nicht Done
- [ ] Manifest kann während lokaler API-Mutation nicht wechseln
- [ ] Worker verwenden Run Snapshot
- [ ] lokale Preflight Errors verhindern paid Partial Submission
- [ ] Ready API Candidate gewinnt gegen Legacy Direct
- [ ] Raw Output vor Normalisierung persistent
- [ ] Ready Candidate hash/metadata/dimension validiert
- [ ] ProviderInputFileId vor Create Batch durable
- [ ] ProviderBatchId sofort nach Create durable
- [ ] Save-Fehler nach Remote Create erzeugt keinen Auto-Resubmit
- [ ] error_file_id verarbeitet
- [ ] output+error verarbeitet
- [ ] unknown/duplicate custom_id fail closed
- [ ] Partial Results erhalten
- [ ] Direct x-request-id in Metadata/Provenance
- [ ] global 401/403/model error stoppt neue Starts
- [ ] DirectRetryAttempts wirkt wirklich
- [ ] gespeicherter Key nicht in Textbox auto-populiert
- [ ] blank OK löscht Key nicht
- [ ] Connection Test prüft selected model
- [ ] Bulk JobStore I/O
- [ ] ProtectedData Build sauber
- [ ] Core Coverage Gate
- [ ] Core Mutation Gate
- [ ] Debug/Release Tests grün
- [ ] RecoveryCritical grün
- [ ] Flakiness grün
- [ ] keine billable Tests
- [ ] keine Secret-Leaks
- [ ] keine SAC-/Defender-Deaktivierung

---

# 32. Kopierbarer Implementierungsauftrag für einen schwachen Agenten

```text
Du arbeitest im Repository Ceegore/gpt_provenance_document_helper auf dem aktuellen
feature/api-batch-automation Branch.

Lies ZUERST vollständig:
1. AGENTS.md
2. die aktuell gültige IMPLEMENTIERUNGSKONZEPT_API_BATCH_AUTOMATION.md
3. API_BATCH_AUTOMATION_REPAIR_PLAN_WEAK_AGENT.md
4. relevante aktuelle API-Dateien und Tests
5. SACsolutions.md, falls lokal vorhanden

AUTORITÄT:
Die aktuelle API-Spezifikation bestimmt das Produktverhalten.
Der Repair-Plan markiert ältere Auditannahmen, die NICHT umgesetzt werden dürfen.

NICHT EIGENMÄCHTIG:
- transparent GPT-Image-2 implementieren;
- alpha=required generieren statt blockieren;
- Concurrency 5 → 10 ändern;
- Timeout 3 → 5 Minuten ändern;
- WebP/JPEG Transcoding ergänzen;
- Paused/Stop-New-Starts/Auto-Chunking ergänzen.

ARBEITE PHASENWEISE.
Pro Phase:
1. Produktionsdateien lesen.
2. relevante Tests lesen.
3. fehlenden Regressionstest zuerst hinzufügen.
4. Test rot bestätigen.
5. minimal fixen.
6. targeted Tests.
7. warning-free Build.
8. diff prüfen.
9. dann nächste Phase.

PRIORITÄT:
A Run Snapshot + Manifest Race
B Preflight
C Ready Candidate vs Legacy Direct
D Raw-before-normalize Staging
E Batch Durability Checkpoints
F Batch Result Mapping/output/error/partial
G x-request-id
H global auth/model stop
I Retry Setting Wiring
J Secret Settings UX
K JobStore Bulk I/O
L ProtectedData Build
M Core Coverage/Mutation
N volle Regression/CI

REMOTE-SAFETY:
Remote Identifier immer an der frühest möglichen Grenze durable speichern.
Wenn ein Remote Batch möglicherweise existiert, niemals automatisch denselben Inhalt
neu submitten, nur weil lokales Persistieren scheiterte.

DONE:
API/Batch success darf nur Ready erzeugen.
Done ausschließlich durch bestehenden durable Main Commit.

LEGACY:
Manual Webchat, V1, Direct, Reference, Keep Settings und Variants nicht neu designen.

TESTS:
Keine echten OpenAI Requests in normalen Tests.
SAC/Defender niemals deaktivieren.

Nach jeder Phase ausgeben:
PHASE:
FILES CHANGED:
TESTS ADDED:
TESTS RUN:
RESULT:
KNOWN REMAINING:
SPEC DEVIATIONS:

Wenn SPEC DEVIATIONS nicht leer ist: STOP und begründen, nicht still weiterbauen.
```

---

# 33. Kopierbarer finaler Abnahme-Prompt

```text
Prüfe den aktuellen feature/api-batch-automation Branch aggressiv gegen:
1. die aktuell gültige IMPLEMENTIERUNGSKONZEPT_API_BATCH_AUTOMATION.md
2. API_BATCH_AUTOMATION_REPAIR_PLAN_WEAK_AGENT.md

Die aktuelle API-Spezifikation ist Autorität. Keine veralteten Auditannahmen übernehmen.

Prüfe:

SPEZIFIKATION
- alpha required block before HTTP
- Ready != Done
- V1 kompatibel
- Manual Webchat unverändert
- Direct/Reference/Keep Settings/Variants ohne Regression

RUN-INTEGRITÄT
- immutable ManifestFingerprint/Model/Quality snapshot
- kein Manifestwechsel während local API mutation
- Mid-run Settings ändern laufenden Run nicht

REMOTE/BILLING
- Preparing durable vor Upload
- InputFileId durable vor CreateBatch
- BatchId sofort durable nach Create
- Save fail nach Remote Create → kein Auto-Resubmit

STAGING
- Raw vor Normalize atomic+flushed
- kein Candidate overwrite
- Ready erst nach final + metadata + state
- Hash/Dimension/Metadata vor Activation/Commit

BATCH RESULTS
- output_file_id
- error_file_id
- output+error
- expired partial
- unknown custom_id fail closed
- duplicate custom_id fail closed
- mapping nur per custom_id

HTTP
- x-request-id end-to-end
- RetryAttempts Setting wirkt
- globale 401/403/model errors stoppen neue Starts
- keine Secrets in Errors

SETTINGS
- saved key nicht plaintext in TextBox
- blank OK löscht Key nicht
- Delete löscht
- connection test prüft selected model und erzeugt kein Bild

PERFORMANCE
- kein full generation-jobs load pro Queue Row
- kein initialer full save pro Item
- Bulk APIs getestet

TESTINFRA
- Core Tests referenzieren nur Core
- Core eigene Coverage
- Core Mutation target
- keine echten OpenAI Calls

FINAL
- Debug/Release warning-free
- Full tests
- RecoveryCritical
- Coverage
- Mutation
- Flakiness
- git diff --check
- Secret scan

Bei jedem Fund:
1. reproduzierbaren Test hinzufügen;
2. minimal fixen;
3. relevante Tests erneut ausführen;
4. gesamte Abnahme erneut starten.

Erst ohne bestätigte Probleme:
PASS_ZERO_DEFECT_API_REPAIR

Zusätzlich alle tatsächlich ausgeführten Commands + Resultate ausgeben.
```

---

# 34. Mini-Checkliste vor jeder Remote Mutation

```text
[ ] Welche Remote Mutation kommt als nächstes?
[ ] Welche lokale Recovery-ID muss VORHER durable sein?
[ ] Kann ein Crash nach der nächsten Zeile zu Doppel-Submission führen?
[ ] Ist Manifest/Model/Quality aus immutable Run Snapshot?
[ ] Ist Secret nur in Memory und nicht State/Log?
[ ] Ist der User-Preflight vollständig?
[ ] Ist dies wirklich eligible und nicht Ready/Done/InFlight/Uncertain?
```

---

# 35. Abschlusspriorität

Wenn Zeit/Agentenqualität knapp ist, exakt in dieser Reihenfolge fertigstellen:

```text
1. Run Snapshot + Manifest Race
2. Batch Input-ID/Batch-ID Durability
3. Ready Candidate vs Legacy Direct
4. Raw-before-normalize
5. Candidate Integrity
6. Batch Result Mapping + output/error
7. Preflight
8. x-request-id + global errors + Retry setting
9. Secret/Connection UX
10. JobStore Bulk I/O
11. Coverage + Mutation + Full Regression
```

Die ersten sechs Punkte schützen vor den teuersten bzw. schwersten State-/Provenance-Fehlern. Erst danach Komfort- und Qualitätsgates abschließen.
