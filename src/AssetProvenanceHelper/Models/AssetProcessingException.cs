namespace AssetProvenanceHelper.Models;

public sealed class AssetProcessingException : IOException
{
    public bool RollbackComplete { get; }

    public AssetProcessingException(string message, bool rollbackComplete)
        : base(message)
    {
        RollbackComplete = rollbackComplete;
    }

    public AssetProcessingException(string message, Exception? innerException, bool rollbackComplete)
        : base(message, innerException)
    {
        RollbackComplete = rollbackComplete;
    }
}
