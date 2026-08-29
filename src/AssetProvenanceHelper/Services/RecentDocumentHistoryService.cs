using System.Text;
using System.Text.Json;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed class RecentDocumentHistoryService
{
    private const int MaxEntries = 3;

    private readonly string _path;

    private readonly JsonSerializerOptions _jsonOptions =
        new()
        {
            WriteIndented = true
        };

    public RecentDocumentHistoryService(
        string path)
    {
        _path =
            path;
    }

    public string FilePath => _path;

    public IReadOnlyList<RecentDocumentEntry> Load()
    {
        if (!File.Exists(_path))
        {
            return Array.Empty<RecentDocumentEntry>();
        }

        var json =
            File.ReadAllText(
                _path,
                Encoding.UTF8);

        RecentDocumentHistoryState state;

        try
        {
            state =
                JsonSerializer.Deserialize<RecentDocumentHistoryState>(
                    json,
                    _jsonOptions)
                ?? throw new InvalidDataException(
                    "recent-documents.json could not be deserialized.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Could not parse recent-documents file '{_path}'.",
                ex);
        }

        return state.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
            .Take(MaxEntries)
            .ToList();
    }

    public void Record(RecentDocumentEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var entries =
            Load().ToList();

        entries.RemoveAll(
            existing =>
                string.Equals(
                    existing.Path,
                    entry.Path,
                    StringComparison.OrdinalIgnoreCase));

        entries.Insert(
            0,
            entry);

        Save(
            entries
                .Take(MaxEntries)
                .ToList());
    }

    public void RemoveEntriesUnderAssetFolder(
        string assetFolder)
    {
        if (string.IsNullOrWhiteSpace(assetFolder))
        {
            return;
        }

        var entries =
            Load().ToList();

        var removed =
            entries.RemoveAll(
                existing =>
                {
                    try
                    {
                        var fullAssetFolder =
                            Path.GetFullPath(
                                assetFolder)
                            .TrimEnd(
                                Path.DirectorySeparatorChar,
                                Path.AltDirectorySeparatorChar);

                        var fullEntryPath =
                            Path.GetFullPath(
                                existing.Path);

                        if (string.Equals(
                                fullEntryPath,
                                fullAssetFolder,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }

                        return fullEntryPath.StartsWith(
                            fullAssetFolder
                            + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase);
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                });

        if (removed == 0)
        {
            return;
        }

        Save(entries);
    }

    private void Save(
        IReadOnlyList<RecentDocumentEntry> entries)
    {
        var state =
            new RecentDocumentHistoryState
            {
                Entries =
                    entries.ToList()
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
}