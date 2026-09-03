namespace AssetProvenanceHelper.Core.Generation;

public enum GenerationItemStatus
{
    Pending,
    Preparing,
    QueuedDirect,
    DirectRateLimited,
    DirectInFlight,
    BatchPreparing,
    BatchSubmitted,
    BatchRunning,
    Downloading,
    Normalizing,
    Ready,
    FailedRetryable,
    FailedPermanent,
    BlockedCapability,
    UncertainAfterInterruption,
    Committed
}
