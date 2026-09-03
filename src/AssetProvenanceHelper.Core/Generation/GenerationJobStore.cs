using System.Text;
using System.Text.Json;

namespace AssetProvenanceHelper.Core.Generation;

public sealed class GenerationJobStore
{
    private readonly string _statePath;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public GenerationJobStore(string? statePath = null)
    {
        if (string.IsNullOrWhiteSpace(statePath))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _statePath = Path.Combine(appData, "Ceegore", "AssetProvenanceHelper", "generation-jobs.json");
        }
        else
        {
            _statePath = Path.GetFullPath(statePath);
        }
    }

    public string StatePath => _statePath;

    public GenerationState Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_statePath))
            {
                return new GenerationState();
            }

            try
            {
                var json = File.ReadAllText(_statePath, Encoding.UTF8);
                return JsonSerializer.Deserialize<GenerationState>(json, JsonOptions) ?? new GenerationState();
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Failed to deserialize generation jobs from '{_statePath}': {ex.Message}", ex);
            }
        }
    }

    public void RecoverInterruptedJobsOnStartup()
    {
        lock (_lock)
        {
            if (!File.Exists(_statePath))
            {
                return;
            }

            GenerationState state;
            try
            {
                var json = File.ReadAllText(_statePath, Encoding.UTF8);
                state = JsonSerializer.Deserialize<GenerationState>(json, JsonOptions) ?? new GenerationState();
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Failed to deserialize generation jobs from '{_statePath}': {ex.Message}", ex);
            }

            var mutated = false;
            for (var i = 0; i < state.Batches.Count; i++)
            {
                var batch = state.Batches[i];
                if (string.Equals(batch.Status, "preparing", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(batch.ProviderBatchId))
                {
                    state.Batches[i] = batch with
                    {
                        Status = "failed",
                        ErrorMessage = "Batch preparation was interrupted before submission completed.",
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    };
                    mutated = true;
                }
            }

            for (var i = 0; i < state.Items.Count; i++)
            {
                var item = state.Items[i];
                if (item.Status == GenerationItemStatus.DirectInFlight)
                {
                    state.Items[i] = item with
                    {
                        Status = GenerationItemStatus.UncertainAfterInterruption,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        ErrorMessage = "Process interrupted while request was in-flight. Status uncertain."
                    };
                    mutated = true;
                }
                else if (item.Status == GenerationItemStatus.BatchPreparing ||
                         (item.Status == GenerationItemStatus.BatchSubmitted &&
                          !string.IsNullOrEmpty(item.BatchId) &&
                          state.Batches.Any(b => string.Equals(b.LocalBatchId, item.BatchId, StringComparison.Ordinal) && string.IsNullOrWhiteSpace(b.ProviderBatchId))))
                {
                    state.Items[i] = item with
                    {
                        Status = GenerationItemStatus.UncertainAfterInterruption,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        ErrorMessage = "Process interrupted during batch submission. Remote status is uncertain."
                    };
                    mutated = true;
                }
            }

            if (mutated)
            {
                SaveCore(state);
            }
        }
    }

    public void Save(GenerationState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (_lock)
        {
            SaveCore(state);
        }
    }

    private void SaveCore(GenerationState state)
    {
        var dir = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(state, JsonOptions);
        var tempPath = _statePath + $".{Guid.NewGuid():N}.tmp";

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(true);
            }

            File.Move(tempPath, _statePath, overwrite: true);
        }
        catch
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
                // Preserve original exception
            }
            throw;
        }
    }

    public void UpsertItem(GenerationItemRecord item)
    {
        ArgumentNullException.ThrowIfNull(item);

        lock (_lock)
        {
            var state = Load();
            var existingIndex = state.Items.FindIndex(i =>
                string.Equals(i.ManifestFingerprint, item.ManifestFingerprint, StringComparison.Ordinal) &&
                string.Equals(i.RequestKey, item.RequestKey, StringComparison.Ordinal));

            if (existingIndex >= 0)
            {
                state.Items[existingIndex] = item;
            }
            else
            {
                state.Items.Add(item);
            }

            SaveCore(state);
        }
    }

    public void UpsertBatch(GenerationBatchRecord batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        lock (_lock)
        {
            var state = Load();
            var existingIndex = state.Batches.FindIndex(b =>
                string.Equals(b.LocalBatchId, batch.LocalBatchId, StringComparison.Ordinal));

            if (existingIndex >= 0)
            {
                state.Batches[existingIndex] = batch;
            }
            else
            {
                state.Batches.Add(batch);
            }

            SaveCore(state);
        }
    }

    public GenerationItemRecord? GetItem(string manifestFingerprint, string requestKey)
    {
        lock (_lock)
        {
            var state = Load();
            return state.Items.FirstOrDefault(i =>
                string.Equals(i.ManifestFingerprint, manifestFingerprint, StringComparison.Ordinal) &&
                string.Equals(i.RequestKey, requestKey, StringComparison.Ordinal));
        }
    }

    public GenerationBatchRecord? GetBatch(string localBatchId)
    {
        lock (_lock)
        {
            var state = Load();
            return state.Batches.FirstOrDefault(b =>
                string.Equals(b.LocalBatchId, localBatchId, StringComparison.Ordinal));
        }
    }

    public IReadOnlyList<GenerationBatchRecord> GetActiveBatches()
    {
        lock (_lock)
        {
            var state = Load();
            return state.Batches
                .Where(b => !string.IsNullOrWhiteSpace(b.ProviderBatchId) &&
                            !string.Equals(b.Status, "completed", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(b.Status, "failed", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(b.Status, "expired", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(b.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    public IReadOnlyList<GenerationItemRecord> GetItemsForBatch(string localBatchId)
    {
        lock (_lock)
        {
            var state = Load();
            return state.Items
                .Where(i => string.Equals(i.BatchId, localBatchId, StringComparison.Ordinal))
                .ToList();
        }
    }
}
