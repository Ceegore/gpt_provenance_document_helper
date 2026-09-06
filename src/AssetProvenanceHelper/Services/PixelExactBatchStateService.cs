using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

/// <summary>
/// The durable outer journal for an ordered Pixel-Exact collection. It freezes
/// download bytes and target bindings before the existing asset transactions
/// begin, so a retry never remaps newer download files to later phases.
/// </summary>
public sealed class PixelExactBatchStateService
{
    private static readonly Regex SeriesIdRegex = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BatchIdRegex = new(@"^[a-fA-F0-9]{32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HashRegex = new(@"^[a-fA-F0-9]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public PixelExactBatchStateService(string statePath, string stagingRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        StatePath = Path.GetFullPath(statePath);
        StagingRoot = Path.GetFullPath(stagingRoot);
    }

    public string StatePath { get; }
    public string StagingRoot { get; }
    public bool HasPendingState => File.Exists(StatePath);

    public PixelExactBatchState? Load()
    {
        if (!File.Exists(StatePath)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<PixelExactBatchState>(File.ReadAllText(StatePath, Encoding.UTF8), JsonOptions)
                ?? throw new InvalidDataException("pixel-exact-batch-state.json could not be deserialized.");
            ValidateStateStructure(state);
            return state;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Could not parse Pixel-Exact batch state.", ex);
        }
    }

    public void Save(PixelExactBatchState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        ValidateStateStructure(state);
        var directory = Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(directory);
        var temp = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(JsonSerializer.Serialize(state, JsonOptions));
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(temp, StatePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public PixelExactBatchState CreateSeedReceiptState(QueuePromptWorkflowMetadata metadata, AssetRequestManifest manifest, AssetRequestItem seedRequest, AssetSession expectedCommitSession)
    {
        if (metadata.Kind != QueuePromptWorkflowKind.PixelExactSeed || !metadata.HasCanonicalMetadata || metadata.SeriesId is null || metadata.PixelOutputCount is null || metadata.TotalPhases is null)
            throw new InvalidDataException("The seed does not have valid canonical Pixel-Exact metadata.");
        return new PixelExactBatchState
        {
            SeriesId = metadata.SeriesId,
            HasCanonicalSeriesIdentity = true,
            TotalPhases = metadata.TotalPhases.Value,
            BundleCount = metadata.PixelOutputCount.Value,
            CollectionOrigin = metadata.CollectionOrigin,
            SeedManifestFingerprint = manifest.ManifestFingerprint,
            SeedRequestKey = seedRequest.RequestKey,
            SeedExpectedSession = CloneSessionReceipt(expectedCommitSession)
        };
    }

    public PixelExactBatchState CreateCollectionState(QueuePromptWorkflowMetadata metadata, AssetRequestManifest manifest, AssetRequestItem activeRefRequest)
    {
        if (metadata.Kind != QueuePromptWorkflowKind.PixelExactRef || !metadata.HasCanonicalMetadata || metadata.SeriesId is null || metadata.PixelOutputCount is null)
            throw new InvalidDataException("The collection request does not have valid canonical Pixel-Exact metadata.");
        return CreateBase(metadata.SeriesId, true, metadata.PixelOutputCount.Value, metadata.TotalPhases ?? metadata.PixelOutputCount.Value + 1, manifest, activeRefRequest, metadata.CollectionOrigin, metadata.ReferenceOrigin);
    }

    public PixelExactBatchState CreateManualLocalCollectionState(AssetRequestManifest manifest, AssetRequestItem activeRequest, int outputCount)
    {
        if (outputCount is < 1 or > AppConstants.MaxPixelExactOutputCount) throw new ArgumentOutOfRangeException(nameof(outputCount));
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(activeRequest);

        // Imported request keys are SHA-256 strings, but this service must not
        // turn an otherwise valid manual/recovered request into a substring
        // exception when a caller supplies a shorter key. The journal series
        // identity is deliberately non-canonical and only needs a stable,
        // filesystem-safe derivation from this request.
        var manualSeriesId = "manual-" + HashText(activeRequest.RequestKey ?? string.Empty)[..16];
        return CreateBase(manualSeriesId, false, outputCount, outputCount + 1, manifest, activeRequest, null, null);
    }

    public PixelExactBatchState StageBundle(PixelExactBatchState state, IReadOnlyList<string> orderedSourceImages, ProviderTemplateSnapshot? bundleProviderTemplate)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(orderedSourceImages);
        ValidateStateStructure(state);
        if (state.Outputs.Count != 0 || state.BatchId is not null || orderedSourceImages.Count != state.BundleCount)
            throw new InvalidOperationException("Pixel-Exact bundle staging state is not empty or does not match its required output count.");
        if (string.IsNullOrWhiteSpace(state.CollectionGenerationPrompt))
            throw new InvalidDataException("Pixel-Exact collection prompt is missing.");

        var working = CloneState(state);
        working.BatchId = Guid.NewGuid().ToString("N");
        working.BundleProviderTemplate = bundleProviderTemplate?.Clone();
        // The null/empty check above establishes this value as authoritative;
        // capture it locally so nullable flow analysis and the journal agree.
        var collectionPrompt = working.CollectionGenerationPrompt!;
        working.CollectionGenerationPromptSha256 = HashText(collectionPrompt);
        var batchDirectory = DeriveBatchDirectory(working.SeriesId, working.BatchId);
        var copied = new List<string>();
        try
        {
            EnsureSafeStagingDirectory(batchDirectory);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < orderedSourceImages.Count; index++)
            {
                var source = Path.GetFullPath(orderedSourceImages[index]);
                var leaf = Path.GetFileName(source);
                if (string.IsNullOrWhiteSpace(leaf) || !string.Equals(source, Path.Combine(Path.GetDirectoryName(source)!, leaf), StringComparison.OrdinalIgnoreCase) || !File.Exists(source) || IsReparsePoint(source) || !names.Add(leaf))
                    throw new InvalidDataException("Pixel-Exact sources must be unique, normal image files.");
                var before = new FileInfo(source);
                var beforeHash = HashFile(source);
                var staged = Path.Combine(batchDirectory, leaf);
                File.Copy(source, staged, overwrite: false);
                copied.Add(staged);
                var stagedHash = HashFile(staged);
                var after = new FileInfo(source);
                var afterHash = HashFile(source);
                if (!string.Equals(beforeHash, stagedHash, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(beforeHash, afterHash, StringComparison.OrdinalIgnoreCase)
                    || before.Length != after.Length
                    || before.LastWriteTimeUtc != after.LastWriteTimeUtc
                    || before.CreationTimeUtc != after.CreationTimeUtc)
                    throw new IOException($"Download source '{leaf}' changed while it was being staged.");
                working.Outputs.Add(new PixelExactStagedOutput
                {
                    OutputIndex = index + 1,
                    Phase = index + 2,
                    OriginalSourcePath = source,
                    StagedPath = staged,
                    Sha256 = beforeHash
                });
            }
            Save(working);
            return working;
        }
        catch
        {
            TryDeleteDerivedBatchDirectory(working.SeriesId, working.BatchId);
            throw;
        }
    }

    public void ValidateStateStructure(PixelExactBatchState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != 1 || !SeriesIdRegex.IsMatch(state.SeriesId) || state.BundleCount is < 1 or > AppConstants.MaxPixelExactOutputCount || state.TotalPhases != state.BundleCount + 1 || state.SeedQueueCompleted && !state.SeedCommitted)
            throw new InvalidDataException("Pixel-Exact batch state has an invalid shape.");
        if (state.SeedCommitted && (string.IsNullOrWhiteSpace(state.SeedRequestKey)
            || string.IsNullOrWhiteSpace(state.MasterAssetName)
            || string.IsNullOrWhiteSpace(state.MasterReferencePath)
            || !HashRegex.IsMatch(state.MasterReferenceSha256 ?? string.Empty)
            || state.MasterProcessedAt is null))
            throw new InvalidDataException("Committed Pixel-Exact seed lacks immutable master authority.");
        if (state.BatchId is not null && !BatchIdRegex.IsMatch(state.BatchId)) throw new InvalidDataException("Pixel-Exact batch state has an invalid batch id.");
        if (state.Completed && state.Outputs.Any(output => output.State != PixelExactOutputCommitState.QueueCompleted)) throw new InvalidDataException("A Pixel-Exact batch cannot be completed before every output is queued complete.");
        if (state.Outputs.Count != 0 && state.Outputs.Count != state.BundleCount) throw new InvalidDataException("Pixel-Exact staged output count is invalid.");
        if (state.Outputs.Count > 0 && (state.BatchId is null || string.IsNullOrWhiteSpace(state.CollectionGenerationPrompt) || !HashRegex.IsMatch(state.CollectionGenerationPromptSha256 ?? string.Empty) || !string.Equals(HashText(state.CollectionGenerationPrompt!), state.CollectionGenerationPromptSha256, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Pixel-Exact staged prompt authority is invalid.");
        foreach (var output in state.Outputs.OrderBy(output => output.OutputIndex))
        {
            if (output.OutputIndex is < 1 or > AppConstants.MaxPixelExactOutputCount || output.Phase != output.OutputIndex + 1 || !HashRegex.IsMatch(output.Sha256) || string.IsNullOrWhiteSpace(output.OriginalSourcePath) || string.IsNullOrWhiteSpace(output.StagedPath))
                throw new InvalidDataException("Pixel-Exact output authority is invalid.");
            if (output.State >= PixelExactOutputCommitState.CommitInProgress && (string.IsNullOrWhiteSpace(output.ManifestFingerprint) || string.IsNullOrWhiteSpace(output.RequestKey) || string.IsNullOrWhiteSpace(output.AssetName) || output.ExpectedCommitSession is null))
                throw new InvalidDataException("Pixel-Exact committing output lacks immutable target authority.");
            if (output.State >= PixelExactOutputCommitState.AssetCommitted && string.IsNullOrWhiteSpace(output.AssetFolderPath))
                throw new InvalidDataException("Pixel-Exact committed output lacks its asset folder authority.");
        }
        if (state.Outputs.Select(output => output.OutputIndex).Distinct().Count() != state.Outputs.Count) throw new InvalidDataException("Pixel-Exact output indices are duplicate.");
    }

    public void ValidateStagedAuthority(PixelExactBatchState state)
    {
        ValidateStateStructure(state);
        if (state.Outputs.Count == 0 || state.BatchId is null) throw new InvalidDataException("Pixel-Exact batch has not staged collection images.");
        var directory = DeriveBatchDirectory(state.SeriesId, state.BatchId);
        foreach (var output in state.Outputs)
        {
            var expected = Path.Combine(directory, Path.GetFileName(output.OriginalSourcePath));
            if (!string.Equals(Path.GetFullPath(output.StagedPath), expected, StringComparison.OrdinalIgnoreCase) || !File.Exists(expected) || IsReparsePoint(expected) || !string.Equals(HashFile(expected), output.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Pixel-Exact staged image authority no longer matches its durable receipt.");
        }
    }

    public AssetSession CloneSessionReceipt(AssetSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return JsonSerializer.Deserialize<AssetSession>(JsonSerializer.Serialize(session, JsonOptions), JsonOptions)
            ?? throw new InvalidDataException("AssetSession receipt could not be cloned.");
    }

    public void ClearCompletedState()
    {
        var state = Load();
        if (state is null || !state.Completed) throw new InvalidOperationException("No completed Pixel-Exact state is available for cleanup.");
        TryDeleteDerivedBatchDirectory(state.SeriesId, state.BatchId);
        if (File.Exists(StatePath)) File.Delete(StatePath);
    }

    public void DiscardPendingState()
    {
        PixelExactBatchState? state = null;
        try { state = Load(); } catch { }
        if (state is not null && !state.Completed) TryDeleteDerivedBatchDirectory(state.SeriesId, state.BatchId);
        if (File.Exists(StatePath)) File.Delete(StatePath);
    }

    private static PixelExactBatchState CreateBase(string series, bool canonical, int count, int total, AssetRequestManifest manifest, AssetRequestItem request, string? collectionOrigin, string? referenceOrigin) => new()
    {
        SeriesId = series, HasCanonicalSeriesIdentity = canonical, BundleCount = count, TotalPhases = total,
        CollectionOrigin = collectionOrigin, ReferenceOrigin = referenceOrigin,
        CollectionManifestFingerprint = manifest.ManifestFingerprint, CollectionRequestKey = request.RequestKey,
        CollectionGenerationPrompt = request.Prompt
    };

    private PixelExactBatchState CloneState(PixelExactBatchState state) => JsonSerializer.Deserialize<PixelExactBatchState>(JsonSerializer.Serialize(state, JsonOptions), JsonOptions) ?? throw new InvalidDataException("Pixel-Exact batch state could not be cloned.");
    private string DeriveBatchDirectory(string series, string? batchId)
    {
        if (!SeriesIdRegex.IsMatch(series) || batchId is null || !BatchIdRegex.IsMatch(batchId)) throw new InvalidDataException("Pixel-Exact staging authority is invalid.");
        var root = Path.GetFullPath(StagingRoot); var seriesPath = Path.GetFullPath(Path.Combine(root, series)); var batch = Path.GetFullPath(Path.Combine(seriesPath, batchId));
        if (!string.Equals(Path.GetDirectoryName(seriesPath), root, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetDirectoryName(batch), seriesPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Pixel-Exact staging path escaped its trusted root.");
        return batch;
    }
    private void EnsureSafeStagingDirectory(string batchDirectory)
    {
        if (Directory.Exists(StagingRoot) && IsReparsePoint(StagingRoot)) throw new InvalidDataException("Pixel-Exact staging root is a reparse point.");
        Directory.CreateDirectory(StagingRoot);
        var series = Path.GetDirectoryName(batchDirectory)!; if (Directory.Exists(series) && IsReparsePoint(series)) throw new InvalidDataException("Pixel-Exact series staging directory is a reparse point.");
        Directory.CreateDirectory(series); if (Directory.Exists(batchDirectory)) throw new IOException("Pixel-Exact batch staging directory already exists.");
        Directory.CreateDirectory(batchDirectory); if (IsReparsePoint(batchDirectory)) throw new InvalidDataException("Pixel-Exact batch staging directory is a reparse point.");
    }
    private void TryDeleteDerivedBatchDirectory(string series, string? batchId)
    { try { if (batchId is not null) { var path = DeriveBatchDirectory(series, batchId); if (Directory.Exists(path) && !IsReparsePoint(path)) Directory.Delete(path, recursive: true); } } catch { } }
    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string HashText(string text) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(text))).ToLowerInvariant();
}
