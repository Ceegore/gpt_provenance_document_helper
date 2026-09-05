using System.Text;
using System.Text.Json;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed class RequestProgressService
{
    private readonly string _path;

    private readonly JsonSerializerOptions _jsonOptions =
        new()
        {
            WriteIndented = true
        };

    public RequestProgressService(
        string path)
    {
        _path =
            path;
    }

    public string FilePath => _path;

    public HashSet<string> LoadForManifest(
        string manifestFingerprint)
    {
        if (!File.Exists(_path))
        {
            return new HashSet<string>(
                StringComparer.Ordinal);
        }

        var json =
            File.ReadAllText(
                _path,
                Encoding.UTF8);

        RequestProgressState state;

        try
        {
            state =
                JsonSerializer.Deserialize<RequestProgressState>(
                    json,
                    _jsonOptions)
                ?? throw new InvalidDataException(
                    "request-progress.json could not be deserialized.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Could not parse request-progress file '{_path}'.",
                ex);
        }

        if (!string.Equals(
                state.ManifestFingerprint,
                manifestFingerprint,
                StringComparison.Ordinal))
        {
            return new HashSet<string>(
                StringComparer.Ordinal);
        }

        return new HashSet<string>(
            state.CompletedRequestKeys
                .Where(
                    key =>
                        !string.IsNullOrWhiteSpace(key)),
            StringComparer.Ordinal);
    }

    public void Save(
        string manifestFingerprint,
        IEnumerable<string> completedKeys)
    {
        var state =
            new RequestProgressState
            {
                ManifestFingerprint =
                    manifestFingerprint,

                CompletedRequestKeys =
                    completedKeys
                        .Distinct(
                            StringComparer.Ordinal)
                        .OrderBy(
                            key => key,
                            StringComparer.Ordinal)
                        .ToList()
            };

        var directory =
            Path.GetDirectoryName(_path);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        var json =
            JsonSerializer.Serialize(
                state,
                _jsonOptions);

        var tempPath =
            _path
            + "."
            + Guid.NewGuid().ToString("N")
            + ".tmp";

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
                writer.Write(json);
                writer.Flush();
                stream.Flush(true);
            }

            File.Move(
                tempPath,
                _path,
                overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }

    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
