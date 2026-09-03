using System.Text;
using System.Text.Json;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed class GeneratedImageStagingService
{
    private readonly string _baseStagingPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public GeneratedImageStagingService(string? baseStagingPath = null)
    {
        if (string.IsNullOrWhiteSpace(baseStagingPath))
        {
            _baseStagingPath = Path.Combine(AppBootstrap.GetStateDirectory(), "generated");
        }
        else
        {
            _baseStagingPath = Path.GetFullPath(baseStagingPath);
        }
    }

    public string BaseStagingPath => _baseStagingPath;

    public string GetItemDirectory(string manifestFingerprint, string requestKey)
    {
        var safeFp = SanitizePathSegment(manifestFingerprint);
        var safeRk = SanitizePathSegment(requestKey);
        return Path.Combine(_baseStagingPath, safeFp, safeRk);
    }

    internal static Action<string>? OnBeforeCandidatePromoteForTests;

    public string SaveRawCandidate(
        string manifestFingerprint,
        string requestKey,
        string candidateId,
        byte[] rawBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentNullException.ThrowIfNull(rawBytes);

        var safeCandidateId = SanitizePathSegment(candidateId);
        var itemDir = GetItemDirectory(manifestFingerprint, requestKey);
        Directory.CreateDirectory(itemDir);

        var rawPath = Path.Combine(itemDir, $"{safeCandidateId}.raw.png");
        WriteBytesAtomicNoOverwrite(rawPath, rawBytes);
        return rawPath;
    }

    public string CompleteCandidate(
        string manifestFingerprint,
        string requestKey,
        string candidateId,
        byte[] normalizedBytes,
        ApiCandidateMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentNullException.ThrowIfNull(normalizedBytes);
        ArgumentNullException.ThrowIfNull(metadata);

        var safeCandidateId = SanitizePathSegment(candidateId);
        var itemDir = GetItemDirectory(manifestFingerprint, requestKey);
        Directory.CreateDirectory(itemDir);

        var normalizedPath = Path.Combine(itemDir, $"{safeCandidateId}.png");
        var metadataPath = Path.Combine(itemDir, $"{safeCandidateId}.metadata.json");

        OnBeforeCandidatePromoteForTests?.Invoke(normalizedPath);

        WriteBytesAtomicNoOverwrite(normalizedPath, normalizedBytes);

        var json = JsonSerializer.Serialize(new StagingMetadataDto
        {
            SchemaVersion = 1,
            CandidateId = metadata.CandidateId,
            Provider = metadata.Provider,
            Model = metadata.Model,
            Mode = metadata.Mode,
            ProviderRequestId = metadata.ProviderRequestId,
            BatchId = metadata.BatchId,
            CustomId = metadata.CustomId,
            TargetResolution = metadata.TargetResolution,
            ProviderResolution = metadata.ProviderResolution,
            RawSha256 = metadata.RawSha256,
            NormalizedSha256 = metadata.NormalizedSha256,
            CreatedAtUtc = metadata.CreatedAtUtc.ToString("O")
        }, JsonOptions);

        WriteTextAtomicNoOverwrite(metadataPath, json);

        return normalizedPath;
    }

    public string SaveCandidate(
        string manifestFingerprint,
        string requestKey,
        string candidateId,
        byte[] rawBytes,
        byte[] normalizedBytes,
        ApiCandidateMetadata metadata)
    {
        SaveRawCandidate(manifestFingerprint, requestKey, candidateId, rawBytes);
        return CompleteCandidate(manifestFingerprint, requestKey, candidateId, normalizedBytes, metadata);
    }

    public ApiCandidateMetadata? LoadMetadata(string manifestFingerprint, string requestKey, string candidateId)
    {
        var safeCandidateId = SanitizePathSegment(candidateId);
        var itemDir = GetItemDirectory(manifestFingerprint, requestKey);
        var metadataPath = Path.Combine(itemDir, $"{safeCandidateId}.metadata.json");
        var normalizedPath = Path.Combine(itemDir, $"{safeCandidateId}.png");

        if (!File.Exists(metadataPath) || !File.Exists(normalizedPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(metadataPath, Encoding.UTF8);
            var dto = JsonSerializer.Deserialize<StagingMetadataDto>(json, JsonOptions);
            if (dto == null) return null;

            return new ApiCandidateMetadata(
                CandidateId: dto.CandidateId ?? candidateId,
                Provider: dto.Provider ?? "OpenAI",
                Model: dto.Model ?? "gpt-image-2",
                Mode: dto.Mode ?? "direct",
                CustomId: dto.CustomId ?? string.Empty,
                TargetResolution: dto.TargetResolution ?? string.Empty,
                ProviderResolution: dto.ProviderResolution ?? string.Empty,
                RawSha256: dto.RawSha256 ?? string.Empty,
                NormalizedSha256: dto.NormalizedSha256 ?? string.Empty,
                NormalizedImagePath: normalizedPath,
                CreatedAtUtc: DateTimeOffset.TryParse(dto.CreatedAtUtc, out var dt) ? dt : DateTimeOffset.UtcNow,
                ProviderRequestId: dto.ProviderRequestId,
                BatchId: dto.BatchId);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteBytesAtomicNoOverwrite(string destinationPath, byte[] bytes)
    {
        if (File.Exists(destinationPath))
        {
            throw new IOException($"Destination already exists: {destinationPath}");
        }

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
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static void WriteTextAtomicNoOverwrite(string destinationPath, string text)
    {
        WriteBytesAtomicNoOverwrite(destinationPath, new UTF8Encoding(false).GetBytes(text));
    }

    private static void WriteBytesAtomic(string destinationPath, byte[] bytes)
    {
        var tempPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private static void WriteTextAtomic(string destinationPath, string text)
    {
        WriteBytesAtomic(destinationPath, new UTF8Encoding(false).GetBytes(text));
    }

    private static string SanitizePathSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = segment.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray();
        var result = new string(chars).Trim('.');
        return string.IsNullOrEmpty(result) ? "unknown" : result;
    }

    private sealed class StagingMetadataDto
    {
        public int SchemaVersion { get; set; } = 1;
        public string? CandidateId { get; set; }
        public string? Provider { get; set; }
        public string? Model { get; set; }
        public string? Mode { get; set; }
        public string? ProviderRequestId { get; set; }
        public string? BatchId { get; set; }
        public string? CustomId { get; set; }
        public string? TargetResolution { get; set; }
        public string? ProviderResolution { get; set; }
        public string? RawSha256 { get; set; }
        public string? NormalizedSha256 { get; set; }
        public string? CreatedAtUtc { get; set; }
    }
}
