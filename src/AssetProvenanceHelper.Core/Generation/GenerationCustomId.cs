using System.Text.RegularExpressions;

namespace AssetProvenanceHelper.Core.Generation;

public static class GenerationCustomId
{
    private const string Prefix = "aph";
    private static readonly Regex CustomIdRegex = new(
        @"^aph-(?<fp>[0-9a-fA-F]{1,32})-(?<rk>[0-9a-fA-F]{1,64})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Create(string manifestFingerprint, string requestKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestKey);

        var fpPrefix = manifestFingerprint.Length > 12 ? manifestFingerprint[..12] : manifestFingerprint;
        var rkPrefix = requestKey.Length > 16 ? requestKey[..16] : requestKey;

        return $"{Prefix}-{fpPrefix.ToLowerInvariant()}-{rkPrefix.ToLowerInvariant()}";
    }

    public static bool TryParse(string customId, out string manifestFingerprintPrefix, out string requestKeyPrefix)
    {
        manifestFingerprintPrefix = string.Empty;
        requestKeyPrefix = string.Empty;

        if (string.IsNullOrWhiteSpace(customId))
        {
            return false;
        }

        var match = CustomIdRegex.Match(customId.Trim());
        if (!match.Success)
        {
            return false;
        }

        manifestFingerprintPrefix = match.Groups["fp"].Value.ToLowerInvariant();
        requestKeyPrefix = match.Groups["rk"].Value.ToLowerInvariant();
        return true;
    }
}
