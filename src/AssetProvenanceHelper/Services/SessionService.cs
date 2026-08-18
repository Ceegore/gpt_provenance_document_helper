using System.Text;
using System.Text.Json;
using AssetProvenanceHelper;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed class SessionService
{
    [ThreadStatic]
    internal static Action<AssetSession>? OnCancelProvenanceMovedHook;

    [ThreadStatic]
    internal static Action<CancelPhase, AssetSession>? OnCancelPhaseSavingHook;

    [ThreadStatic]
    internal static Action? OnBeforeFolderCleanupHook;

    private readonly string _sessionPath;
    private readonly TemplateService? _templateService;
    private readonly ValidationService? _validationService;

    public string SessionFilePath => _sessionPath;

    private readonly JsonSerializerOptions _jsonOptions =
        new()
        {
            WriteIndented = true
        };

    public SessionService(
        string sessionPath,
        TemplateService? templateService = null,
        ValidationService? validationService = null)
    {
        _sessionPath =
            sessionPath;

        _templateService =
            templateService;

        _validationService =
            validationService;
    }

    public bool Exists()
    {
        return File.Exists(
            _sessionPath);
    }

    public AssetSession? Load()
    {
        if (!File.Exists(
                _sessionPath))
        {
            return null;
        }

        try
        {
            var json =
                File.ReadAllText(
                    _sessionPath,
                    Encoding.UTF8);

            var session =
                JsonSerializer.Deserialize<AssetSession>(
                    json,
                    _jsonOptions);

            if (session is null)
            {
                throw new InvalidDataException(
                    "session.json could not be deserialized.");
            }

            return session;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Could not parse session file '{_sessionPath}'.",
                ex);
        }
    }

    public void Save(
        AssetSession session)
    {
        ArgumentNullException.ThrowIfNull(
            session);

        var directory =
            Path.GetDirectoryName(
                _sessionPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        var json =
            JsonSerializer.Serialize(
                session,
                _jsonOptions);

        var tempPath =
            _sessionPath
            + $".{Guid.NewGuid():N}.tmp";

        try
        {
            using (
                var stream =
                    new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None))
            using (
                var writer =
                    new StreamWriter(
                        stream,
                        new UTF8Encoding(false)))
            {
                writer.Write(
                    json);

                writer.Flush();

                stream.Flush(
                    true);
            }

            File.Move(
                tempPath,
                _sessionPath,
                overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(
                        tempPath);
                }
            }
            catch
            {
                // Preserve the original exception.
            }

            throw;
        }
    }

    public void Delete()
    {
        if (File.Exists(
                _sessionPath))
        {
            File.Delete(
                _sessionPath);
        }
    }

    public void Cancel(
        AssetSession session)
    {
        ArgumentNullException.ThrowIfNull(
            session);

        if (_templateService is null)
        {
            throw new InvalidOperationException(
                "Cancellation requires TemplateService to verify exact provenance ownership.");
        }

        EnsureCancelPathsAreSafe(
            session);

        var referenceFolder =
            Path.Combine(
                session.AssetFolder,
                AppConstants.ReferenceFolderName);

        // Phase 1: Prepared
        if (session.CancelPhase == CancelPhase.None)
        {
            session.CancellationId = Guid.NewGuid().ToString("N");
            session.CancelPhase = CancelPhase.Prepared;
            try
            {
                OnCancelPhaseSavingHook?.Invoke(CancelPhase.Prepared, session);
                Save(session);
            }
            catch
            {
                session.CancelPhase = CancelPhase.None;
                session.CancellationId = null;
                throw;
            }
        }

        // Phase 2: Rename & Reconcile
        if (session.CancelPhase == CancelPhase.Prepared)
        {
            if (string.IsNullOrWhiteSpace(session.CancellationId) ||
                session.CancellationId.Length != 32 ||
                session.CancellationId.Any(c => !Uri.IsHexDigit(c)))
            {
                throw new InvalidDataException("Invalid CancellationId in session.");
            }

            var tempProvenancePath = session.GetCancelTempProvenancePath();
            var tempDestinationPath = session.GetCancelTempReferencePath();

            var origProvExists = File.Exists(session.ReferenceProvenancePath);
            var tempProvExists = File.Exists(tempProvenancePath);

            var origRefExists = File.Exists(session.ReferenceDestinationPath);
            var tempRefExists = File.Exists(tempDestinationPath);

            var provenanceJustMoved = false;

            if (origProvExists && !tempProvExists)
            {
                // BUG-R19-002: Re-verify exact provenance ownership immediately before moving
                var validator = _validationService ?? new ValidationService();
                var provValidation = validator.ValidateExactReferenceProvenanceOwnership(session, session.ReferenceProvenancePath, _templateService);
                if (!provValidation.IsValid)
                {
                    if (provValidation.Errors.Any(e => e.StartsWith("Could not read", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new IOException($"Could not verify reference provenance ownership before rename: {string.Join("; ", provValidation.Errors)}");
                    }

                    throw new InvalidDataException($"Reference provenance on disk does not match expected session provenance: {string.Join("; ", provValidation.Errors)}");
                }

                File.Move(session.ReferenceProvenancePath, tempProvenancePath, overwrite: false);
                provenanceJustMoved = true;
                OnCancelProvenanceMovedHook?.Invoke(session);
            }
            else if (!origProvExists && tempProvExists)
            {
                // Already moved in a previous attempt
            }
            else if (origProvExists && tempProvExists)
            {
                throw new IOException("Ambiguous provenance file state during cancellation: both original and temp file exist.");
            }
            else
            {
                throw new IOException("Missing provenance file during cancellation: neither original nor temp file exists.");
            }

            if (origRefExists && !tempRefExists)
            {
                // BUG-R19-002: Re-verify reference ownership immediately before moving (after OnCancelProvenanceMovedHook)
                try
                {
                    var hash = ValidationService.ComputeSha256(session.ReferenceDestinationPath);
                    if (!string.Equals(hash, session.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Reference image on disk does not match session ReferenceHash.");
                    }
                }
                catch (InvalidDataException) { throw; }
                catch (Exception ex)
                {
                    throw new IOException($"Could not verify reference image ownership before rename: {ex.Message}", ex);
                }

                try
                {
                    File.Move(session.ReferenceDestinationPath, tempDestinationPath, overwrite: false);
                }
                catch (Exception moveEx)
                {
                    if (provenanceJustMoved && File.Exists(tempProvenancePath))
                    {
                        // BUG-R19-002: Re-verify exact provenance ownership before restoring to canonical slot
                        var validator = _validationService ?? new ValidationService();
                        var tempProvValidation = validator.ValidateExactReferenceProvenanceOwnership(session, tempProvenancePath, _templateService);
                        if (!tempProvValidation.IsValid)
                        {
                            // Tampered/unverified temp provenance MUST NOT be restored to canonical path!
                            // Keep session in Prepared phase with CancellationId intact to remain recoverable.
                            throw new IOException(
                                $"Cancel failed during reference image rename ({moveEx.Message}), and moved reference provenance at '{tempProvenancePath}' no longer matches tool-written provenance. Temp file preserved and not restored.",
                                moveEx);
                        }

                        try
                        {
                            File.Move(tempProvenancePath, session.ReferenceProvenancePath, overwrite: false);

                            session.CancelPhase = CancelPhase.None;
                            session.CancellationId = null;
                            try { Save(session); }
                            catch (Exception saveEx)
                            {
                                throw new IOException(
                                    $"Cancel failed during reference rename ({moveEx.Message}), provenance was restored, but resetting session failed ({saveEx.Message}).",
                                    new AggregateException(moveEx, saveEx));
                            }
                        }
                        catch (Exception restoreEx) when (restoreEx is not IOException io || io.InnerException is not AggregateException)
                        {
                            throw new IOException(
                                $"Cancel failed during reference image rename ({moveEx.Message}), and restoring reference provenance also failed ({restoreEx.Message}).",
                                new AggregateException(moveEx, restoreEx));
                        }
                    }

                    throw;
                }
            }
            else if (!origRefExists && tempRefExists)
            {
                // Already moved in a previous attempt
            }
            else if (origRefExists && tempRefExists)
            {
                throw new IOException("Ambiguous reference file state during cancellation: both original and temp file exist.");
            }
            else
            {
                throw new IOException("Missing reference file during cancellation: neither original nor temp file exists.");
            }

            session.CancelPhase = CancelPhase.FilesRenamed;
            try
            {
                OnCancelPhaseSavingHook?.Invoke(CancelPhase.FilesRenamed, session);
                Save(session);
            }
            catch
            {
                session.CancelPhase = CancelPhase.Prepared;
                throw;
            }
        }

        // Phase 3: FilesRenamed -> Permanent deletion of exact tool-owned temp files
        if (session.CancelPhase == CancelPhase.FilesRenamed)
        {
            var tempProvenancePath = session.GetCancelTempProvenancePath();
            var tempDestinationPath = session.GetCancelTempReferencePath();

            // BUG-R18-003: Re-verify exact ownership immediately before deletion in Phase 3
            if (File.Exists(tempDestinationPath))
            {
                try
                {
                    var hash = ValidationService.ComputeSha256(tempDestinationPath);
                    if (!string.Equals(hash, session.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"Cancel-temp reference image '{tempDestinationPath}' hash no longer matches session ReferenceHash. File preserved.");
                    }
                }
                catch (InvalidDataException) { throw; }
                catch (Exception ex)
                {
                    throw new IOException(
                        $"Could not verify temporary canceling reference image '{tempDestinationPath}': {ex.Message}", ex);
                }
            }

            if (File.Exists(tempProvenancePath) && _templateService != null)
            {
                var validator = _validationService ?? new ValidationService();
                var provValidation = validator.ValidateExactReferenceProvenanceOwnership(session, tempProvenancePath, _templateService);
                if (!provValidation.IsValid)
                {
                    if (provValidation.Errors.Any(e => e.StartsWith("Could not read", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new IOException(
                            $"Could not verify temporary canceling reference provenance '{tempProvenancePath}': {string.Join("; ", provValidation.Errors)}");
                    }

                    throw new InvalidDataException(
                        $"Cancel-temp reference provenance '{tempProvenancePath}' content no longer matches expected session provenance: {string.Join("; ", provValidation.Errors)}. File preserved.");
                }
            }

            var deletionErrors = new List<string>();

            if (File.Exists(tempProvenancePath))
            {
                try
                {
                    File.Delete(tempProvenancePath);
                }
                catch (Exception ex)
                {
                    deletionErrors.Add($"Could not delete temporary canceling provenance '{tempProvenancePath}': {ex.Message}");
                }
            }

            if (File.Exists(tempDestinationPath))
            {
                try
                {
                    File.Delete(tempDestinationPath);
                }
                catch (Exception ex)
                {
                    deletionErrors.Add($"Could not delete temporary canceling reference image '{tempDestinationPath}': {ex.Message}");
                }
            }

            if (deletionErrors.Count > 0)
            {
                throw new IOException(
                    "Cancel partially failed: temporary canceling files could not be removed."
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, deletionErrors));
            }

            OnBeforeFolderCleanupHook?.Invoke();

            if (session.WasReferenceFolderCreatedByTool &&
                Directory.Exists(referenceFolder))
            {
                try
                {
                    Directory.Delete(referenceFolder, recursive: false);
                }
                catch (DirectoryNotFoundException) { }
                catch (IOException) { }
            }

            if (session.WasAssetFolderCreatedByTool &&
                Directory.Exists(session.AssetFolder))
            {
                try
                {
                    Directory.Delete(session.AssetFolder, recursive: false);
                }
                catch (DirectoryNotFoundException) { }
                catch (IOException) { }
            }

            Delete();
        }
    }

    private void EnsureCancelPathsAreSafe(
        AssetSession session)
    {
        if (_templateService is null)
        {
            throw new InvalidOperationException(
                "Cancellation requires TemplateService to verify exact provenance ownership.");
        }

        var pathValidation =
            ValidationService.ValidateSessionPathsForDestructiveOperation(session);

        if (!pathValidation.IsValid)
        {
            throw new InvalidDataException(
                string.Join(Environment.NewLine, pathValidation.Errors));
        }

        if (!string.Equals(
                Path.GetFileName(
                    session.ReferenceFilename),
                session.ReferenceFilename,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "ReferenceFilename contains an unsafe path.");
        }

        var normalizedRoot =
            ValidationService.NormalizePath(
                session.AssetRootFolder);

        var expectedAssetFolder =
            ValidationService.NormalizePath(
                Path.Combine(
                    session.AssetRootFolder,
                    session.AssetFolderName));

        var actualAssetFolder =
            ValidationService.NormalizePath(
                session.AssetFolder);

        if (!string.Equals(
                expectedAssetFolder,
                actualAssetFolder,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Session AssetFolder does not match AssetRootFolder + AssetFolderName.");
        }

        var actualAssetParent =
            Path.GetDirectoryName(
                actualAssetFolder);

        if (actualAssetParent is null ||
            !ValidationService.PathsEqual(
                actualAssetParent,
                normalizedRoot))
        {
            throw new InvalidDataException(
                "Session AssetFolder is not a direct child of AssetRootFolder.");
        }

        var referenceFolder =
            ValidationService.NormalizePath(
                Path.Combine(
                    session.AssetFolder,
                    AppConstants.ReferenceFolderName));

        var expectedReference =
            ValidationService.NormalizePath(
                Path.Combine(
                    referenceFolder,
                    session.ReferenceFilename));

        var referenceParent =
            Path.GetDirectoryName(
                expectedReference);

        if (referenceParent is null ||
            !ValidationService.PathsEqual(
                referenceParent,
                referenceFolder))
        {
            throw new InvalidDataException(
                "ReferenceDestinationPath escapes the expected reference folder.");
        }

        var expectedProvenance =
            ValidationService.NormalizePath(
                Path.Combine(
                    referenceFolder,
                    AppConstants.ReferenceProvenanceFileName));

        if (!ValidationService.PathsEqual(
                expectedReference,
                session.ReferenceDestinationPath))
        {
            throw new InvalidDataException(
                "ReferenceDestinationPath is unsafe or inconsistent.");
        }

        if (!ValidationService.PathsEqual(
                expectedProvenance,
                session.ReferenceProvenancePath))
        {
            throw new InvalidDataException(
                "ReferenceProvenancePath is unsafe or inconsistent.");
        }

        if (!Enum.IsDefined(typeof(CancelPhase), session.CancelPhase))
        {
            throw new InvalidDataException("Invalid CancelPhase in session.");
        }

        if (session.CancelPhase == CancelPhase.None)
        {
            if (!string.IsNullOrWhiteSpace(session.CancellationId))
            {
                throw new InvalidDataException("CancellationId must be null when CancelPhase is None.");
            }

            // BUG-R13-003 & BUG-R14-001: Verify exact ownership before allowing cancellation - FAIL CLOSED
            if (File.Exists(session.ReferenceDestinationPath))
            {
                try
                {
                    var hash = ValidationService.ComputeSha256(session.ReferenceDestinationPath);
                    if (!string.Equals(hash, session.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Reference image on disk does not match session ReferenceHash.");
                    }
                }
                catch (InvalidDataException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new IOException($"Could not verify reference image ownership before cancellation: {ex.Message}", ex);
                }
            }

            if (File.Exists(session.ReferenceProvenancePath))
            {
                var validator = _validationService ?? new ValidationService();
                var provValidation = validator.ValidateExactReferenceProvenanceOwnership(session, session.ReferenceProvenancePath, _templateService);
                if (!provValidation.IsValid)
                {
                    if (provValidation.Errors.Any(e => e.StartsWith("Could not read", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new IOException($"Could not verify reference provenance ownership before cancellation: {string.Join("; ", provValidation.Errors)}");
                    }

                    throw new InvalidDataException($"Reference provenance on disk does not match expected session provenance: {string.Join("; ", provValidation.Errors)}");
                }
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(session.CancellationId) ||
                session.CancellationId.Length != 32 ||
                session.CancellationId.Any(c => !Uri.IsHexDigit(c)))
            {
                throw new InvalidDataException("CancellationId is missing or is not a valid 32-character hexadecimal string.");
            }

            var tempRef = session.GetCancelTempReferencePath();
            var tempProv = session.GetCancelTempProvenancePath();

            var normTempRef = ValidationService.NormalizePath(tempRef);
            var normTempProv = ValidationService.NormalizePath(tempProv);

            if (!ValidationService.PathsEqual(Path.GetDirectoryName(normTempRef) ?? "", referenceFolder) ||
                !ValidationService.PathsEqual(Path.GetDirectoryName(normTempProv) ?? "", referenceFolder))
            {
                throw new InvalidDataException("Derived cancellation temp paths escape the expected reference folder.");
            }

            // BUG-R13-003 & BUG-R14-001: Verify exact ownership in recovery phases - FAIL CLOSED
            var origRefExists = File.Exists(session.ReferenceDestinationPath);
            var tempRefExists = File.Exists(tempRef);
            var origProvExists = File.Exists(session.ReferenceProvenancePath);
            var tempProvExists = File.Exists(tempProv);

            if (session.CancelPhase == CancelPhase.Prepared)
            {
                // In Prepared phase, if exactly one file state is present, verify ownership before moving.
                // If ambiguous (both) or missing (neither), allow Phase 2 to throw the specific IOException.
                if (origRefExists && !tempRefExists)
                {
                    try
                    {
                        var hash = ValidationService.ComputeSha256(session.ReferenceDestinationPath);
                        if (!string.Equals(hash, session.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("Reference image on disk does not match session ReferenceHash.");
                        }
                    }
                    catch (InvalidDataException) { throw; }
                    catch (Exception ex)
                    {
                        throw new IOException($"Could not verify reference image ownership in recovery phase '{session.CancelPhase}': {ex.Message}", ex);
                    }
                }
                else if (!origRefExists && tempRefExists)
                {
                    try
                    {
                        var hash = ValidationService.ComputeSha256(tempRef);
                        if (!string.Equals(hash, session.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("Cancel-temp reference image on disk does not match session ReferenceHash.");
                        }
                    }
                    catch (InvalidDataException) { throw; }
                    catch (Exception ex)
                    {
                        throw new IOException($"Could not verify cancel-temp reference image ownership in recovery phase '{session.CancelPhase}': {ex.Message}", ex);
                    }
                }

                var validator = _validationService ?? new ValidationService();
                if (origProvExists && !tempProvExists)
                {
                    var provValidation = validator.ValidateExactReferenceProvenanceOwnership(session, session.ReferenceProvenancePath, _templateService);
                    if (!provValidation.IsValid)
                    {
                        if (provValidation.Errors.Any(e => e.StartsWith("Could not read", StringComparison.OrdinalIgnoreCase)))
                        {
                            throw new IOException($"Could not verify reference provenance ownership in recovery phase '{session.CancelPhase}': {string.Join("; ", provValidation.Errors)}");
                        }

                        throw new InvalidDataException($"Reference provenance on disk does not match expected session provenance: {string.Join("; ", provValidation.Errors)}");
                    }
                }
                else if (!origProvExists && tempProvExists)
                {
                    var provValidation = validator.ValidateExactReferenceProvenanceOwnership(session, tempProv, _templateService);
                    if (!provValidation.IsValid)
                    {
                        if (provValidation.Errors.Any(e => e.StartsWith("Could not read", StringComparison.OrdinalIgnoreCase)))
                        {
                            throw new IOException($"Could not verify cancel-temp reference provenance ownership in recovery phase '{session.CancelPhase}': {string.Join("; ", provValidation.Errors)}");
                        }

                        throw new InvalidDataException($"Cancel-temp reference provenance on disk does not match expected session provenance: {string.Join("; ", provValidation.Errors)}");
                    }
                }
            }
            else if (session.CancelPhase == CancelPhase.FilesRenamed)
            {
                // In FilesRenamed phase, verify temp files before final deletion if present
                if (tempRefExists)
                {
                    try
                    {
                        var hash = ValidationService.ComputeSha256(tempRef);
                        if (!string.Equals(hash, session.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("Cancel-temp reference image on disk does not match session ReferenceHash.");
                        }
                    }
                    catch (InvalidDataException) { throw; }
                    catch (Exception ex)
                    {
                        throw new IOException($"Could not verify temporary canceling reference image '{tempRef}': {ex.Message}", ex);
                    }
                }

                if (tempProvExists)
                {
                    var validator = _validationService ?? new ValidationService();
                    var provValidation = validator.ValidateExactReferenceProvenanceOwnership(session, tempProv, _templateService);
                    if (!provValidation.IsValid)
                    {
                        if (provValidation.Errors.Any(e => e.StartsWith("Could not read", StringComparison.OrdinalIgnoreCase)))
                        {
                            throw new IOException($"Could not verify temporary canceling reference provenance '{tempProv}': {string.Join("; ", provValidation.Errors)}");
                        }

                        throw new InvalidDataException($"Cancel-temp reference provenance on disk does not match expected session provenance: {string.Join("; ", provValidation.Errors)}");
                    }
                }
            }
        }
    }
}
