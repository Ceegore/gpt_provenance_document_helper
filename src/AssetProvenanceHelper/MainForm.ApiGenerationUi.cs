using System.Drawing;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private static readonly Color ReadyRowBackColor = Color.FromArgb(220, 235, 252);
    private static readonly Color InFlightRowBackColor = Color.FromArgb(254, 243, 199);
    private static readonly Color BatchRowBackColor = Color.FromArgb(243, 232, 255);
    private static readonly Color FailedRowBackColor = Color.FromArgb(254, 226, 226);
    private static readonly Color UncertainRowBackColor = Color.FromArgb(254, 215, 170);

    private (string StatusText, Color BackColor) GetRequestItemVisualStatus(AssetRequestItem request, GenerationItemRecord? preloadedJob = null)
    {
        if (request.IsCompleted || _completedRequestKeys.Contains(request.RequestKey))
        {
            return ("Done", DoneRowBackColor);
        }

        if (_currentManifest != null)
        {
            var job = preloadedJob ?? _generationJobStore.GetItem(_currentManifest.ManifestFingerprint, request.RequestKey);
            if (job != null)
            {
                if (job.Status == GenerationItemStatus.Ready && !string.IsNullOrEmpty(job.StagedOutputPath) && File.Exists(job.StagedOutputPath))
                {
                    return ("Ready", ReadyRowBackColor);
                }

                if (job.Status == GenerationItemStatus.DirectInFlight || job.Status == GenerationItemStatus.Preparing || job.Status == GenerationItemStatus.Normalizing)
                {
                    return ("Generating", InFlightRowBackColor);
                }

                if (job.Status == GenerationItemStatus.QueuedDirect || job.Status == GenerationItemStatus.DirectRateLimited)
                {
                    return ("Queued", InFlightRowBackColor);
                }

                if (job.Status == GenerationItemStatus.BatchSubmitted)
                {
                    return ("Batch queued", BatchRowBackColor);
                }

                if (job.Status == GenerationItemStatus.BatchRunning || job.Status == GenerationItemStatus.Downloading)
                {
                    return ("Batch running", BatchRowBackColor);
                }

                if (job.Status == GenerationItemStatus.FailedPermanent || job.Status == GenerationItemStatus.FailedRetryable)
                {
                    return ("API failed", FailedRowBackColor);
                }

                if (job.Status == GenerationItemStatus.UncertainAfterInterruption)
                {
                    return ("Uncertain", UncertainRowBackColor);
                }

                if (job.Status == GenerationItemStatus.BlockedCapability)
                {
                    return ("Blocked: alpha", Color.White);
                }
            }
        }

        if (request.Alpha == AlphaRequirement.Required)
        {
            return ("Blocked: alpha", Color.White);
        }

        return ("Pending", Color.White);
    }
}
