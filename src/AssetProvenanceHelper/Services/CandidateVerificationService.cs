using System.Drawing;
using System.Security.Cryptography;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed class CandidateVerificationService
{
    private static readonly byte[] PngSignature =
    [
        0x89, 0x50, 0x4E, 0x47,
        0x0D, 0x0A, 0x1A, 0x0A
    ];

    private readonly GeneratedImageStagingService _stagingService;

    public CandidateVerificationService(GeneratedImageStagingService stagingService)
    {
        _stagingService = stagingService ?? throw new ArgumentNullException(nameof(stagingService));
    }

    public CandidateVerificationResult VerifyCandidate(
        GenerationItemRecord job,
        int targetWidth,
        int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (string.IsNullOrWhiteSpace(job.StagedOutputPath))
        {
            return new CandidateVerificationResult(false, null, "staged_path_missing", "Staged output path is not set.");
        }

        if (string.IsNullOrWhiteSpace(job.CandidateId))
        {
            return new CandidateVerificationResult(false, null, "candidate_id_missing", "Job CandidateId is missing or empty.");
        }

        // 8. Path lies strictly within expected staging directory
        var expectedDir = Path.GetFullPath(_stagingService.GetItemDirectory(job.ManifestFingerprint, job.RequestKey));
        var expectedDirWithSep = expectedDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullStagedPath = Path.GetFullPath(job.StagedOutputPath);
        if (!fullStagedPath.StartsWith(expectedDirWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return new CandidateVerificationResult(false, null, "untrusted_staging_path", $"Staged path '{fullStagedPath}' is not within expected staging directory '{expectedDir}'.");
        }

        // 1. Staged file exists and is a PNG
        if (!File.Exists(fullStagedPath))
        {
            return new CandidateVerificationResult(false, null, "staged_file_missing", $"Staged image file '{fullStagedPath}' does not exist.");
        }

        if (!string.Equals(Path.GetExtension(fullStagedPath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            return new CandidateVerificationResult(false, null, "invalid_extension", $"Staged candidate file '{fullStagedPath}' must have a .png extension.");
        }

        // 3. Candidate ID matches exactly
        var candidateIdFromPath = Path.GetFileNameWithoutExtension(fullStagedPath);
        if (!string.Equals(job.CandidateId, candidateIdFromPath, StringComparison.Ordinal))
        {
            return new CandidateVerificationResult(false, null, "candidate_id_mismatch", $"Candidate ID in job record '{job.CandidateId}' does not match file candidate ID '{candidateIdFromPath}'.");
        }

        // 2. Metadata exists and deserializes
        var metadata = _stagingService.LoadMetadata(job.ManifestFingerprint, job.RequestKey, candidateIdFromPath);
        if (metadata == null)
        {
            return new CandidateVerificationResult(false, null, "metadata_missing_or_corrupt", $"Metadata file for candidate '{candidateIdFromPath}' is missing or invalid.");
        }

        if (!string.Equals(metadata.CandidateId, candidateIdFromPath, StringComparison.Ordinal))
        {
            return new CandidateVerificationResult(false, null, "metadata_candidate_id_mismatch", $"Metadata candidate ID '{metadata.CandidateId}' does not match '{candidateIdFromPath}'.");
        }

        if (string.IsNullOrWhiteSpace(metadata.NormalizedSha256) ||
            metadata.NormalizedSha256.Length != 64 ||
            !metadata.NormalizedSha256.All(Uri.IsHexDigit))
        {
            return new CandidateVerificationResult(false, null, "metadata_sha_invalid", "Metadata NormalizedSha256 is missing or not a valid 64-character hexadecimal SHA-256.");
        }

        if (!string.Equals(metadata.Provider, job.ProviderId, StringComparison.Ordinal))
        {
            return new CandidateVerificationResult(false, null, "provider_mismatch", "Candidate metadata Provider does not match the generation job.");
        }

        if (!string.Equals(metadata.Model, job.Model, StringComparison.Ordinal))
        {
            return new CandidateVerificationResult(false, null, "model_mismatch", "Candidate metadata Model does not match the generation job.");
        }

        var expectedMode = job.Mode == GenerationMode.Batch ? "batch" : "direct";
        if (!string.Equals(metadata.Mode, expectedMode, StringComparison.Ordinal))
        {
            return new CandidateVerificationResult(false, null, "mode_mismatch", "Candidate metadata Mode does not match the generation job.");
        }

        if (!string.Equals(metadata.CustomId, job.CustomId, StringComparison.Ordinal))
        {
            return new CandidateVerificationResult(false, null, "custom_id_mismatch", "Candidate metadata CustomId does not match the generation job.");
        }

        if (!string.Equals(metadata.TargetResolution, $"{job.TargetWidth}x{job.TargetHeight}", StringComparison.Ordinal))
        {
            return new CandidateVerificationResult(false, null, "target_resolution_mismatch", "Candidate metadata target resolution is inconsistent.");
        }

        if (!string.Equals(metadata.ProviderResolution, $"{job.GenerationWidth}x{job.GenerationHeight}", StringComparison.Ordinal))
        {
            return new CandidateVerificationResult(false, null, "provider_resolution_mismatch", "Candidate metadata provider resolution is inconsistent.");
        }

        if (!string.IsNullOrWhiteSpace(job.ProviderRequestId)
            && !string.Equals(metadata.ProviderRequestId, job.ProviderRequestId, StringComparison.Ordinal))
        {
            return new CandidateVerificationResult(false, null, "provider_request_id_mismatch", "Candidate Provider Request ID is inconsistent.");
        }

        if (job.Mode == GenerationMode.Batch
            && !string.IsNullOrWhiteSpace(job.ProviderBatchId)
            && !string.Equals(metadata.BatchId, job.ProviderBatchId, StringComparison.Ordinal))
        {
            return new CandidateVerificationResult(false, null, "provider_batch_id_mismatch", "Candidate Batch ID is inconsistent.");
        }

        if (!string.IsNullOrWhiteSpace(job.ProviderRawPath))
        {
            var expectedRawPath = Path.GetFullPath(_stagingService.GetRawCandidatePath(job.ManifestFingerprint, job.RequestKey, job.CandidateId));
            var actualRawPath = Path.GetFullPath(job.ProviderRawPath);

            if (!string.Equals(expectedRawPath, actualRawPath, StringComparison.OrdinalIgnoreCase))
            {
                return new CandidateVerificationResult(false, null, "raw_path_invalid", "Provider raw path does not match the candidate's expected raw path.");
            }

            if (!File.Exists(actualRawPath))
            {
                return new CandidateVerificationResult(false, null, "raw_file_missing", "Provider raw candidate is missing.");
            }

            string actualRawSha;
            try
            {
                actualRawSha = ComputeSha256File(actualRawPath);
            }
            catch (Exception ex)
            {
                return new CandidateVerificationResult(false, null, "raw_file_read_error", $"Failed to read provider raw candidate file: {ex.Message}");
            }

            if (!string.Equals(actualRawSha, job.RawSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(actualRawSha, metadata.RawSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new CandidateVerificationResult(false, null, "raw_hash_mismatch", "Provider raw candidate hash does not match job/metadata authority.");
            }
        }

        // 4. Job SHA matches metadata
        if (string.IsNullOrWhiteSpace(job.NormalizedSha256))
        {
            return new CandidateVerificationResult(false, null, "job_sha_missing", "Job NormalizedSha256 is missing or empty.");
        }

        if (!string.Equals(job.NormalizedSha256, metadata.NormalizedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new CandidateVerificationResult(false, null, "job_metadata_sha_mismatch", $"Job NormalizedSha256 '{job.NormalizedSha256}' does not match metadata NormalizedSha256 '{metadata.NormalizedSha256}'.");
        }

        // 5. Actual file SHA matches
        string actualFileSha;
        try
        {
            actualFileSha = ComputeSha256File(fullStagedPath);
        }
        catch (Exception ex)
        {
            return new CandidateVerificationResult(false, null, "file_read_error", $"Failed to read staged image file: {ex.Message}");
        }

        if (!string.Equals(actualFileSha, metadata.NormalizedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new CandidateVerificationResult(false, null, "file_hash_mismatch", $"Actual file SHA256 '{actualFileSha}' does not match expected metadata SHA256 '{metadata.NormalizedSha256}'.");
        }

        // 6. PNG magic header check
        try
        {
            Span<byte> header = stackalloc byte[PngSignature.Length];
            using (var stream = new FileStream(fullStagedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var bytesRead = stream.Read(header);
                if (bytesRead < PngSignature.Length || !header.SequenceEqual(PngSignature))
                {
                    return new CandidateVerificationResult(false, null, "invalid_png_header", $"Staged image file '{fullStagedPath}' does not have a valid PNG magic header.");
                }
            }
        }
        catch (Exception ex)
        {
            return new CandidateVerificationResult(false, null, "file_read_error", $"Failed to read staged image file header: {ex.Message}");
        }

        // 7. PNG decodable & 8. Target resolution matches
        try
        {
            using var img = Image.FromFile(fullStagedPath);
            if (img.Width != targetWidth || img.Height != targetHeight)
            {
                return new CandidateVerificationResult(false, null, "resolution_mismatch", $"Decoded image resolution {img.Width}x{img.Height} does not match target resolution {targetWidth}x{targetHeight}.");
            }
        }
        catch (Exception ex)
        {
            return new CandidateVerificationResult(false, null, "image_decode_error", $"Failed to decode staged PNG image: {ex.Message}");
        }

        return new CandidateVerificationResult(true, new VerifiedApiCandidate(fullStagedPath, metadata), null, null);
    }

    internal static string ComputeSha256File(string path)
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
}
