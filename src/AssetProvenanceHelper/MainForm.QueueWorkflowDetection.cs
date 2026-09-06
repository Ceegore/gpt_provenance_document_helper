using System.Windows.Forms;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private QueuePromptWorkflowMetadata GetActiveQueueWorkflowMetadata() =>
        _activeRequest is null
            ? new QueuePromptWorkflowMetadata { Kind = QueuePromptWorkflowKind.Unknown }
            : _queuePromptWorkflowParser.Parse(_activeRequest.Prompt);

    private void ApplyQueueWorkflowAutodetection(AssetRequestItem item)
    {
        var metadata = _queuePromptWorkflowParser.Parse(item.Prompt);
        var previous = _settingWorkflowSelectors;
        _settingWorkflowSelectors = true;
        try
        {
            switch (metadata.Kind)
            {
                case QueuePromptWorkflowKind.Invalid:
                    ResetVariantSelectionToNone();
                    AddStatus($"Workflow metadata for '{item.AssetName}' is invalid: {string.Join("; ", metadata.Errors)}");
                    break;
                case QueuePromptWorkflowKind.Variants:
                    chkPixelExact.Checked = false;
                    if (metadata.VariantCount is int variants) cmbVariants.SelectedIndex = variants;
                    break;
                case QueuePromptWorkflowKind.PixelExactSeed:
                    ResetVariantSelectionToNone();
                    chkPixelExact.Checked = true;
                    SetNoReferenceCheckedProgrammatically(true);
                    SetDirectModeCheckedProgrammatically(false);
                    SetPixelExactOutputCount(0);
                    break;
                case QueuePromptWorkflowKind.PixelExactRef:
                case QueuePromptWorkflowKind.PixelExactOutput:
                    ResetVariantSelectionToNone();
                    chkPixelExact.Checked = true;
                    // Pixel-Exact controls the image relationship in the external
                    // generator. Each imported output is still a complete asset,
                    // so it must not create a second internal reference workflow.
                    SetNoReferenceCheckedProgrammatically(true);
                    SetDirectModeCheckedProgrammatically(false);
                    if (metadata.PixelOutputCount is int outputs) SetPixelExactOutputCount(outputs);
                    break;
                case QueuePromptWorkflowKind.Single:
                    ResetVariantSelectionToNone();
                    chkPixelExact.Checked = false;
                    break;
                case QueuePromptWorkflowKind.Unknown:
                    break;
            }
        }
        finally
        {
            _settingWorkflowSelectors = previous;
        }
        ApplyState();
    }

    private bool EnsureActiveWorkflowMetadataIsExecutable(out QueuePromptWorkflowMetadata metadata)
    {
        metadata = GetActiveQueueWorkflowMetadata();
        if (metadata.Kind == QueuePromptWorkflowKind.Invalid)
        {
            ShowMessageBox(string.Join(Environment.NewLine, metadata.Errors), "Invalid queue workflow metadata", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (metadata.Kind == QueuePromptWorkflowKind.Variants && metadata.VariantCount != GetSelectedVariantCount())
        {
            ShowMessageBox("The Variants selector no longer matches the recognized queue request. Select the detected value before processing.", "Variants setting required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (metadata.IsPixelExact && !chkPixelExact.Checked)
        {
            ShowMessageBox("This Request is part of a Pixel-Exact series. Enable Pixel-exact mode before processing it.", "Pixel-Exact mode required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (metadata.Kind is QueuePromptWorkflowKind.PixelExactRef or QueuePromptWorkflowKind.PixelExactOutput)
        {
            if (metadata.PixelOutputCount != GetSelectedPixelExactOutputCount())
            {
                ShowMessageBox("The Pixel phases selector no longer matches the recognized queue request.", "Pixel-Exact setting required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }
        if (metadata.Kind == QueuePromptWorkflowKind.PixelExactSeed && GetSelectedVariantCount() != 0)
        {
            ShowMessageBox("A Pixel-Exact seed cannot be combined with Variants.", "Incompatible workflow", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return true;
    }

    private bool ManifestContainsUnsupportedAutomatedMultiOutputWorkflow(AssetRequestManifest manifest, out string reason)
    {
        foreach (var item in manifest.Items.Where(item => !item.IsCompleted && !_completedRequestKeys.Contains(item.RequestKey)))
        {
            var workflow = _queuePromptWorkflowParser.Parse(item.Prompt);
            if (workflow.Kind == QueuePromptWorkflowKind.Invalid
                || workflow.Kind is QueuePromptWorkflowKind.PixelExactRef or QueuePromptWorkflowKind.PixelExactOutput
                || workflow.Kind == QueuePromptWorkflowKind.PixelExactSeed && workflow.PixelOutputCount > 0
                || workflow.Kind == QueuePromptWorkflowKind.Variants && workflow.VariantCount > 1)
            {
                reason = "This manifest contains queue workflows that require the browser/download-folder multi-image orchestrator. Automated API generation creates one staged candidate per Request and cannot preserve those semantics.";
                return true;
            }
        }
        reason = string.Empty;
        return false;
    }
}
