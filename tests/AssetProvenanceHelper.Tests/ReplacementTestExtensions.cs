using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

internal static class ReplacementTestExtensions
{
    public static ReferenceReplacementTransaction MaterializeReplacementForTest(
        this AssetProcessorService processor,
        AssetSession oldSession,
        IReadOnlyCollection<string> extensions,
        string source,
        DateTimeOffset processedAt)
    {
        var tx = processor.CreateReferenceReplacementTransaction(
            oldSession,
            extensions,
            source,
            processedAt);

        processor.CreateReplacementTempFiles(
            tx,
            extensions);

        processor.BackupOldReference(tx);

        processor.PromoteNewReference(tx);

        return tx;
    }
}
