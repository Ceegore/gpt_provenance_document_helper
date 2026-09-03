using System.Drawing;
using System.Security.Cryptography;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed class CandidateVerificationService
{
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

        // 8. Path lies in expected staging directory
        var expectedDir = Path.GetFullPath(_stagingService.GetItemDirectory(job.ManifestFingerprint, job.RequestKey));
        var fullStagedPath = Path.GetFullPath(job.StagedOutputPath);
        if (!fullStagedPath.StartsWith(expectedDir, StringComparison.OrdinalIgnoreCase))
        {
            return new CandidateVerificationResult(false, null, "untrusted_staging_path", $"Staged path '{fullStagedPath}' is not within expected staging directory '{expectedDir}'.");
        }

        // 1. Staged file exists
        if (!File.Exists(fullStagedPath))
        {
            return new CandidateVerificationResult(false, null, "staged_file_missing", $"Staged image file '{fullStagedPath}' does not exist.");
        }

        // 3. Candidate ID matches
        var candidateIdFromPath = Path.GetFileNameWithoutExtension(fullStagedPath);
        if (!string.IsNullOrEmpty(job.CandidateId) && !string.Equals(job.CandidateId, candidateIdFromPath, StringComparison.Ordinal))
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

        // 4. Job SHA matches metadata
        if (!string.IsNullOrEmpty(job.NormalizedSha256) &&
            !string.Equals(job.NormalizedSha256, metadata.NormalizedSha256, StringComparison.OrdinalIgnoreCase))
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

        if (!string.IsNullOrEmpty(metadata.NormalizedSha256) &&
            !string.Equals(actualFileSha, metadata.NormalizedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new CandidateVerificationResult(false, null, "file_hash_mismatch", $"Actual file SHA256 '{actualFileSha}' does not match expected metadata SHA256 '{metadata.NormalizedSha256}'.");
        }

        // 6. PNG decodable & 7. Target resolution matches
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
}
