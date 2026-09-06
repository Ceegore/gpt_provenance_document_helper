using System.Globalization;
using System.Text.RegularExpressions;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

/// <summary>Parses only the documented FLOWMETA/PROZESSMARKER contract and the
/// narrow legacy forms shipped by the helper. It never rewrites prompts.</summary>
public sealed class QueuePromptWorkflowParser
{
    private static readonly Regex FlowMetaRegex = new(@"(?i)\bFLOWMETA\s*:\s*(?<body>.*?)(?=\.\s*PROZESSMARKER\s*:|\r?\n|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MarkerRegex = new(@"(?i)\bPROZESSMARKER\s*:\s*(?<marker>Einzeln|Ref(?<ref>[0-9]+)|AusRef(?<ausref>[0-9]+)|Varianten(?<variants>[0-9]+))\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RefTokenRegex = new(@"(?i)^Ref(?<count>[0-9]+)(?:@(?<origin>[A-Za-z0-9_-]+))?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SeriesIdRegex = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OriginRegex = new(@"^[A-Za-z0-9_-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GermanVariantsRegex = new(@"(?is)\A\s*(?:Variantenregel\s*[–—-]\s*kritisch\s*:\s*)?Erzeuge\s+exakt\s+(?<count>[0-9]+|zwei)\b.{0,180}?\bVarianten\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EnglishVariantsRegex = new(@"(?is)\A\s*Generate\s+exactly\s+(?<count>[0-9]+|two)\b.{0,180}?\bvariants\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LegacySizeRegex = new(@"(?i)\bSERIENGROESSE\s*=\s*(?<count>[0-9]+)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LegacyNextRegex = new(@"(?i)\bNEXT\s*=\s*Ref(?<count>[0-9]+)(?:@(?<origin>[A-Za-z0-9_-]+))?\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public QueuePromptWorkflowMetadata Parse(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return Unknown();
        var flows = FlowMetaRegex.Matches(prompt);
        var markers = MarkerRegex.Matches(prompt);
        if (flows.Count > 1) return Invalid("Prompt contains more than one FLOWMETA clause.");
        if (markers.Count > 1) return Invalid("Prompt contains more than one PROZESSMARKER.");
        var marker = markers.Count == 1 ? markers[0] : null;
        if (flows.Count == 1)
        {
            if (marker is null) return Invalid("FLOWMETA is present but PROZESSMARKER is missing.");
            var errors = new List<string>();
            var pairs = ParsePairs(flows[0].Groups["body"].Value, errors);
            return errors.Count > 0 ? Invalid(errors) : ParseCanonical(pairs, marker);
        }
        return ParseLegacy(prompt, marker);
    }

    private static QueuePromptWorkflowMetadata ParseCanonical(IReadOnlyDictionary<string, string> values, Match marker)
    {
        var errors = new List<string>();
        var series = RequiredString(values, "SERIE", errors);
        if (series is not null && !SeriesIdRegex.IsMatch(series)) errors.Add("FLOWMETA SERIE is invalid.");
        var markerText = marker.Groups["marker"].Value;
        var refCount = GroupInt(marker, "ref");
        var outputCount = GroupInt(marker, "ausref");
        var variants = GroupInt(marker, "variants");
        if (variants is not null) return Invalid("FLOWMETA must not be combined with a Varianten marker.");

        if (markerText.Equals("Einzeln", StringComparison.OrdinalIgnoreCase))
        {
            var total = RequiredInt(values, "SERIENGROESSE", errors);
            var next = RequiredString(values, "NEXT", errors);
            var nextRef = next is null ? null : ParseRef(next, "FLOWMETA NEXT", errors);
            if (total is not null && nextRef is not null && total != nextRef.Value.Count + 1) errors.Add("FLOWMETA SERIENGROESSE must equal NEXT Ref count + 1.");
            Reject(values, errors, "OUTPUT_COUNT", "OUTPUT_INDEX", "MASTER", "REF_ORIGIN", "REFERENZ");
            return errors.Count > 0 ? Invalid(errors) : new QueuePromptWorkflowMetadata
            {
                Kind = QueuePromptWorkflowKind.PixelExactSeed, HasCanonicalMetadata = true, SeriesId = series,
                PixelOutputCount = nextRef!.Value.Count, TotalPhases = total, Phase = 1,
                CollectionOrigin = nextRef.Value.Origin, LegacyMarker = markerText
            };
        }

        if (refCount is not null)
        {
            var count = RequiredInt(values, "OUTPUT_COUNT", errors);
            if (!InPixel(refCount.Value) || count is null || !InPixel(count.Value)) errors.Add("Ref output count is outside 1..10.");
            if (count is not null && count != refCount) errors.Add("FLOWMETA OUTPUT_COUNT contradicts PROZESSMARKER RefN.");
            var refOrigin = OptionalOrigin(values, "REF_ORIGIN", errors);
            Reject(values, errors, "SERIENGROESSE", "NEXT", "OUTPUT_INDEX", "MASTER", "REFERENZ");
            return errors.Count > 0 ? Invalid(errors) : new QueuePromptWorkflowMetadata
            {
                Kind = QueuePromptWorkflowKind.PixelExactRef, HasCanonicalMetadata = true, SeriesId = series,
                PixelOutputCount = count, TotalPhases = count + 1, Phase = 2, OutputIndex = 1,
                ReferenceOrigin = refOrigin, LegacyMarker = markerText
            };
        }

        if (outputCount is not null)
        {
            var outputIndex = RequiredInt(values, "OUTPUT_INDEX", errors);
            var master = RequiredString(values, "MASTER", errors);
            var masterRef = master is null ? null : ParseRef(master, "FLOWMETA MASTER", errors);
            if (!InPixel(outputCount.Value)) errors.Add("AusRef count is outside 1..10.");
            if (masterRef is not null && masterRef.Value.Count != outputCount) errors.Add("FLOWMETA MASTER contradicts PROZESSMARKER AusRefN.");
            if (outputIndex is null || outputIndex < 2 || outputIndex > outputCount) errors.Add("FLOWMETA OUTPUT_INDEX must be in 2..N.");
            var referenceOrigin = OptionalOrigin(values, "REFERENZ", errors);
            Reject(values, errors, "SERIENGROESSE", "NEXT", "OUTPUT_COUNT", "REF_ORIGIN");
            return errors.Count > 0 ? Invalid(errors) : new QueuePromptWorkflowMetadata
            {
                Kind = QueuePromptWorkflowKind.PixelExactOutput, HasCanonicalMetadata = true, SeriesId = series,
                PixelOutputCount = outputCount, TotalPhases = outputCount + 1, Phase = outputIndex + 1,
                OutputIndex = outputIndex, CollectionOrigin = masterRef?.Origin,
                ReferenceOrigin = referenceOrigin, LegacyMarker = markerText
            };
        }
        return Invalid($"Unsupported FLOWMETA/PROZESSMARKER combination '{markerText}'.");
    }

    private static QueuePromptWorkflowMetadata ParseLegacy(string prompt, Match? marker)
    {
        if (marker is not null)
        {
            var text = marker.Groups["marker"].Value;
            var variants = GroupInt(marker, "variants");
            if (variants is not null) return InVariant(variants.Value)
                ? new QueuePromptWorkflowMetadata { Kind = QueuePromptWorkflowKind.Variants, VariantCount = variants, LegacyMarker = text }
                : Invalid("Varianten count is outside 1..10.");
            var reference = GroupInt(marker, "ref");
            if (reference is not null) return InPixel(reference.Value)
                ? new QueuePromptWorkflowMetadata { Kind = QueuePromptWorkflowKind.PixelExactRef, PixelOutputCount = reference, TotalPhases = reference + 1, Phase = 2, OutputIndex = 1, LegacyMarker = text }
                : Invalid("Ref count is outside 1..10.");
            var output = GroupInt(marker, "ausref");
            if (output is not null) return InPixel(output.Value)
                ? new QueuePromptWorkflowMetadata { Kind = QueuePromptWorkflowKind.PixelExactOutput, PixelOutputCount = output, TotalPhases = output + 1, LegacyMarker = text }
                : Invalid("AusRef count is outside 1..10.");
            if (text.Equals("Einzeln", StringComparison.OrdinalIgnoreCase))
            {
                var sizes = LegacySizeRegex.Matches(prompt); var nexts = LegacyNextRegex.Matches(prompt);
                if (sizes.Count == 0 && nexts.Count == 0) return new QueuePromptWorkflowMetadata { Kind = QueuePromptWorkflowKind.Single, LegacyMarker = text };
                if (sizes.Count != 1 || nexts.Count != 1) return Invalid("Legacy Pixel seed must contain exactly one SERIENGROESSE and NEXT=RefN.");
                var total = Int(sizes[0].Groups["count"].Value); var count = Int(nexts[0].Groups["count"].Value);
                if (total is null || count is null || !InPixel(count.Value) || total != count + 1) return Invalid("Legacy Pixel seed has inconsistent SERIENGROESSE/NEXT=RefN.");
                return new QueuePromptWorkflowMetadata { Kind = QueuePromptWorkflowKind.PixelExactSeed, PixelOutputCount = count, TotalPhases = total, Phase = 1, CollectionOrigin = Empty(nexts[0].Groups["origin"].Value), LegacyMarker = text };
            }
        }
        foreach (var regex in new[] { GermanVariantsRegex, EnglishVariantsRegex })
        {
            var match = regex.Match(prompt);
            if (!match.Success) continue;
            var raw = match.Groups["count"].Value;
            var count = raw.Equals("zwei", StringComparison.OrdinalIgnoreCase) || raw.Equals("two", StringComparison.OrdinalIgnoreCase) ? 2 : Int(raw);
            return count is not null && InVariant(count.Value)
                ? new QueuePromptWorkflowMetadata { Kind = QueuePromptWorkflowKind.Variants, VariantCount = count }
                : Invalid("Variants count is outside 1..10.");
        }
        return Unknown();
    }

    private static Dictionary<string, string> ParsePairs(string body, ICollection<string> errors)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in body.Split(';'))
        {
            var pair = raw.Trim(); if (pair.Length == 0) continue;
            var equals = pair.IndexOf('=');
            if (equals <= 0 || equals == pair.Length - 1 || pair.IndexOf('=', equals + 1) >= 0) { errors.Add($"Invalid FLOWMETA pair '{pair}'."); continue; }
            if (!result.TryAdd(pair[..equals].Trim(), pair[(equals + 1)..].Trim())) errors.Add($"Duplicate FLOWMETA key '{pair[..equals].Trim()}'.");
        }
        return result;
    }

    private static (int Count, string? Origin)? ParseRef(string raw, string label, ICollection<string> errors)
    {
        var match = RefTokenRegex.Match(raw.Trim());
        if (!match.Success) { errors.Add($"{label} must be RefN or RefN@origin."); return null; }
        var count = Int(match.Groups["count"].Value);
        if (count is null || !InPixel(count.Value)) { errors.Add($"{label} count is outside 1..10."); return null; }
        return (count.Value, Empty(match.Groups["origin"].Value));
    }

    private static string? RequiredString(IReadOnlyDictionary<string, string> values, string key, ICollection<string> errors)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) { errors.Add($"FLOWMETA is missing {key}."); return null; }
        return value.Trim();
    }
    private static int? RequiredInt(IReadOnlyDictionary<string, string> values, string key, ICollection<string> errors)
    {
        var value = RequiredString(values, key, errors); var number = value is null ? null : Int(value);
        if (value is not null && number is null) errors.Add($"FLOWMETA {key} is not a valid integer."); return number;
    }
    private static string? OptionalOrigin(IReadOnlyDictionary<string, string> values, string key, ICollection<string> errors)
    {
        if (!values.TryGetValue(key, out var raw)) return null;
        var value = raw.Trim(); if (!OriginRegex.IsMatch(value)) { errors.Add($"FLOWMETA {key} has an invalid origin tag."); return null; } return value;
    }
    private static void Reject(IReadOnlyDictionary<string, string> values, ICollection<string> errors, params string[] keys)
    { foreach (var key in keys) if (values.ContainsKey(key)) errors.Add($"FLOWMETA key {key} is not valid for this marker."); }
    private static int? GroupInt(Match match, string group) => match.Groups[group].Success ? Int(match.Groups[group].Value) : null;
    private static int? Int(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool InPixel(int value) => value is >= 1 and <= AppConstants.MaxPixelExactOutputCount;
    private static bool InVariant(int value) => value is >= 1 and <= AppConstants.MaxVariantCount;
    private static QueuePromptWorkflowMetadata Unknown() => new() { Kind = QueuePromptWorkflowKind.Unknown };
    private static QueuePromptWorkflowMetadata Invalid(params string[] errors) => Invalid((IReadOnlyList<string>)errors);
    private static QueuePromptWorkflowMetadata Invalid(IReadOnlyList<string> errors) => new() { Kind = QueuePromptWorkflowKind.Invalid, Errors = errors.ToArray() };
}
