using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

/// <summary>
/// Persists the imported request queue independently of its source manifest.
/// The snapshot is validated again on startup before it is made available.
/// </summary>
public sealed class RequestQueueStateService
{
    private const int SchemaVersion = 1;
    private const int MaxAssets = 5000;
    private const int MaxPromptCharacters = 1_000_000;

    private readonly string _path;
    private readonly ValidationService _validationService;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public RequestQueueStateService(string path, ValidationService validationService)
    {
        _path = path;
        _validationService = validationService;
    }

    public string FilePath => _path;

    public bool HasPersistedState => File.Exists(_path);

    public AssetRequestManifest? Load(IReadOnlyCollection<string> acceptedExtensions)
    {
        ArgumentNullException.ThrowIfNull(acceptedExtensions);

        if (!File.Exists(_path))
        {
            return null;
        }

        PersistedQueueState state;
        try
        {
            state = JsonSerializer.Deserialize<PersistedQueueState>(
                        File.ReadAllText(_path, Encoding.UTF8), _jsonOptions)
                    ?? throw new InvalidDataException("request-queue-state.json could not be deserialized.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Could not parse request queue state '{_path}'.", ex);
        }

        return ValidateAndCreateManifest(state, acceptedExtensions);
    }

    public void Save(AssetRequestManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var state = new PersistedQueueState
        {
            SchemaVersion = SchemaVersion,
            Version = manifest.Version,
            SourcePath = manifest.SourcePath,
            ManifestFingerprint = manifest.ManifestFingerprint,
            Items = manifest.Items.Select(CloneItem).ToList()
        };

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        WriteAtomically(JsonSerializer.Serialize(state, _jsonOptions));
    }

    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private AssetRequestManifest ValidateAndCreateManifest(
        PersistedQueueState state,
        IReadOnlyCollection<string> acceptedExtensions)
    {
        if (state.SchemaVersion != SchemaVersion
            || state.Version is not (1 or 2)
            || string.IsNullOrWhiteSpace(state.SourcePath)
            || string.IsNullOrWhiteSpace(state.ManifestFingerprint)
            || state.Items is null
            || state.Items.Count is < 1 or > MaxAssets)
        {
            throw new InvalidDataException("Request queue state has an unsupported or incomplete shape.");
        }

        var filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<AssetRequestItem>(state.Items.Count);

        foreach (var item in state.Items)
        {
            if (item is null
                || string.IsNullOrWhiteSpace(item.FileName)
                || !string.Equals(Path.GetFileName(item.FileName), item.FileName, StringComparison.Ordinal)
                || !filenames.Add(item.FileName)
                || !acceptedExtensions.Contains(Path.GetExtension(item.FileName), StringComparer.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(item.Prompt)
                || item.Prompt.Length > MaxPromptCharacters
                || item.Width is < 1 or > 100_000
                || item.Height is < 1 or > 100_000
                || !string.Equals(item.Resolution, $"{item.Width}x{item.Height}", StringComparison.Ordinal)
                || !Enum.IsDefined(item.Alpha)
                || (state.Version == 1 && item.Alpha != Core.Generation.AlphaRequirement.Unknown))
            {
                throw new InvalidDataException("Request queue state contains an invalid item.");
            }

            var expectedAssetName = Path.GetFileNameWithoutExtension(item.FileName);
            if (!string.Equals(item.AssetName, expectedAssetName, StringComparison.Ordinal)
                || !_validationService.ValidateAssetName(item.AssetName, acceptedExtensions).IsValid)
            {
                throw new InvalidDataException("Request queue state contains an invalid asset name.");
            }

            var expectedKey = state.Version == 2
                ? AssetRequestManifestService.ComputeRequestKeyV2(item.FileName, item.Resolution, item.Prompt, item.Alpha)
                : AssetRequestManifestService.ComputeRequestKey(item.FileName, item.Resolution, item.Prompt);

            if (!string.Equals(item.RequestKey, expectedKey, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Request queue state contains an invalid request key.");
            }

            items.Add(CloneItem(item));
        }

        var expectedFingerprint = AssetRequestManifestService.ComputeManifestFingerprint(items, state.Version);
        if (!string.Equals(state.ManifestFingerprint, expectedFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Request queue state contains an invalid manifest fingerprint.");
        }

        return new AssetRequestManifest
        {
            Version = state.Version,
            SourcePath = Path.GetFullPath(state.SourcePath),
            ManifestFingerprint = state.ManifestFingerprint,
            Items = items
        };
    }

    private void WriteAtomically(string json)
    {
        var tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(true);
            }

            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static AssetRequestItem CloneItem(AssetRequestItem item) => new()
    {
        FileName = item.FileName,
        AssetName = item.AssetName,
        Width = item.Width,
        Height = item.Height,
        Resolution = item.Resolution,
        Prompt = item.Prompt,
        RequestKey = item.RequestKey,
        Alpha = item.Alpha,
        IsCompleted = false
    };

    private sealed class PersistedQueueState
    {
        public int SchemaVersion { get; set; }
        public int Version { get; set; }
        public string SourcePath { get; set; } = string.Empty;
        public string ManifestFingerprint { get; set; } = string.Empty;
        public List<AssetRequestItem>? Items { get; set; }
    }
}
