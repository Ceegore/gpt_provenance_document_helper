using System.Text;
using System.Text.Json;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

/// <summary>
/// Durable completion history. Schema 2 keeps independent histories for every
/// manifest so switching Teil files cannot make prior completed rows reappear.
/// </summary>
public sealed class RequestProgressService
{
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public RequestProgressService(string path) => _path = path;

    public string FilePath => _path;

    public HashSet<string> LoadForManifest(string manifestFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestFingerprint);
        var states = LoadAll();
        return states.TryGetValue(manifestFingerprint, out var keys)
            ? new HashSet<string>(keys, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    public void Save(string manifestFingerprint, IEnumerable<string> completedKeys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestFingerprint);
        ArgumentNullException.ThrowIfNull(completedKeys);
        var states = LoadAll();
        states[manifestFingerprint] = NormalizeKeys(completedKeys);
        WriteAll(states);
    }

    public void ClearForManifest(string manifestFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestFingerprint);
        var states = LoadAll();
        if (!states.Remove(manifestFingerprint))
        {
            return;
        }

        if (states.Count == 0)
        {
            Clear();
            return;
        }

        WriteAll(states);
    }

    /// <summary>Explicit full reset retained for tests/emergency maintenance.</summary>
    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private Dictionary<string, List<string>> LoadAll()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        RequestProgressState state;
        try
        {
            state = JsonSerializer.Deserialize<RequestProgressState>(File.ReadAllText(_path, Encoding.UTF8), _jsonOptions)
                ?? throw new InvalidDataException("request-progress.json could not be deserialized.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Could not parse request-progress file '{_path}'.", ex);
        }

        if (state.SchemaVersion == 1)
        {
            if (string.IsNullOrWhiteSpace(state.ManifestFingerprint))
            {
                throw new InvalidDataException("Schema-1 request progress is missing its manifest fingerprint.");
            }

            return new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                [state.ManifestFingerprint] = NormalizeKeys(state.CompletedRequestKeys ?? [])
            };
        }

        if (state.SchemaVersion != 2 || state.CompletedByManifest is null)
        {
            throw new InvalidDataException("request-progress.json has an unsupported schema.");
        }

        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (fingerprint, keys) in state.CompletedByManifest)
        {
            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                throw new InvalidDataException("request-progress.json contains an empty manifest fingerprint.");
            }
            result[fingerprint] = NormalizeKeys(keys ?? []);
        }
        return result;
    }

    private void WriteAll(Dictionary<string, List<string>> states)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var state = new RequestProgressState
        {
            SchemaVersion = 2,
            CompletedByManifest = states.ToDictionary(
                pair => pair.Key,
                pair => NormalizeKeys(pair.Value),
                StringComparer.Ordinal)
        };
        var tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(JsonSerializer.Serialize(state, _jsonOptions));
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static List<string> NormalizeKeys(IEnumerable<string> keys) => keys
        .Where(key => !string.IsNullOrWhiteSpace(key))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(key => key, StringComparer.Ordinal)
        .ToList();
}
