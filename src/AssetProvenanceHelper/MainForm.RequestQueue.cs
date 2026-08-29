#nullable enable
using System.Drawing;
using System.Windows.Forms;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private static readonly Color DoneRowBackColor =
        Color.FromArgb(222, 242, 222);

    private void HandleImportRequest()
    {
        string? path = null;

        if (OpenFileDialogProvider is not null)
        {
            path = OpenFileDialogProvider(this, txtDownloadFolder.Text);
            if (path is null)
            {
                return;
            }
        }
        else
        {
            path = PickManifestPathWithDialog();
            if (path is null)
            {
                return;
            }
        }

        var manifestService =
            new AssetRequestManifestService(
                _validationService);

        AssetRequestManifest manifest;

        try
        {
            manifest =
                manifestService.Load(
                    path,
                    _settings.AcceptedExtensions);
        }
        catch (Exception ex)
        {
            // Atomic import: the current queue and all user fields stay untouched.
            ShowMessageBox(
                "Request Manifest could not be imported."
                + Environment.NewLine
                + Environment.NewLine
                + ex.Message
                + Environment.NewLine
                + Environment.NewLine
                + "No Request Queue changes were applied.",
                "Import failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        // Recovered queue-originated session: the manifest must contain the
        // durable session Request key, otherwise the association would be lost.
        // A manual Reference session must never accept an import either:
        // the queue stays locked until the session is completed or cancelled.
        if (_state == UiState.ReferenceReady)
        {
            if (_currentSession?.SourceRequestKey is null)
            {
                ShowMessageBox(
                    "The active reference-assisted asset is not bound to a Request."
                    + Environment.NewLine
                    + "Finish or cancel it before importing a Request Manifest.",
                    "Import rejected",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (!manifest.Items.Any(
                    item =>
                        string.Equals(
                            item.RequestKey,
                            _currentSession.SourceRequestKey,
                            StringComparison.Ordinal)))
            {
                ShowMessageBox(
                    "The active recovered asset belongs to a Request that is not present in this manifest."
                    + Environment.NewLine
                    + "Import was cancelled.",
                    "Import rejected",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
        }

        // Atomic apply: only now replace the current queue.
        _currentManifest = manifest;
        _activeRequest = null;
        _completedRequestKeys.Clear();

        txtAssetFolderName.Clear();
        txtPrompt.Clear();
        UpdatePromptPreview();
        SetSelectedImage(ImageSlot.Main, null);

        if (_state != UiState.ReferenceReady)
        {
            SetSelectedImage(ImageSlot.Reference, null);
        }

        try
        {
            var restored =
                _requestProgressService?.LoadForManifest(
                    manifest.ManifestFingerprint)
                ?? new HashSet<string>(
                    StringComparer.Ordinal);

            _completedRequestKeys.UnionWith(restored);
        }
        catch
        {
            // Corrupt progress state is handled as empty; import still succeeds.
        }

        foreach (var item in manifest.Items)
        {
            item.IsCompleted =
                _completedRequestKeys.Contains(
                    item.RequestKey);
        }

        lblRequestSource.Text =
            Path.GetFileName(manifest.SourcePath);

        RefreshRequestQueueVisuals();
        UpdateRequestProgressLabel();
        BindRecoveredSessionRequest();
        ApplyRequestQueueState();

        AddStatus(
            $"Request Manifest imported: {Path.GetFileName(manifest.SourcePath)}");
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    private string? PickManifestPathWithDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import Request Manifest",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (Directory.Exists(txtDownloadFolder.Text))
        {
            dialog.InitialDirectory = txtDownloadFolder.Text;
        }

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return null;
        }

        return dialog.FileName;
    }

    private void RefreshRequestQueueVisuals()
    {
        if (IsDisposed || Disposing || lvRequestQueue.IsDisposed)
        {
            return;
        }

        lvRequestQueue.BeginUpdate();

        try
        {
            lvRequestQueue.Items.Clear();

            if (_currentManifest is null)
            {
                return;
            }

            foreach (var request in _currentManifest.Items)
            {
                var completed =
                    request.IsCompleted
                    || _completedRequestKeys.Contains(
                        request.RequestKey);

                var lvi =
                    new ListViewItem(
                        new[]
                        {
                            completed ? "Done" : "Pending",
                            request.AssetName,
                            request.Resolution
                        })
                    {
                        Tag = request
                    };

                if (completed)
                {
                    lvi.BackColor = DoneRowBackColor;
                }

                if (_activeRequest is not null
                    && string.Equals(
                        _activeRequest.RequestKey,
                        request.RequestKey,
                        StringComparison.Ordinal))
                {
                    lvi.Font = new Font(
                        lvRequestQueue.Font,
                        FontStyle.Bold);
                }

                lvRequestQueue.Items.Add(lvi);
            }
        }
        finally
        {
            lvRequestQueue.EndUpdate();
        }
    }

    private void HandleRequestQueueMouseUp(MouseEventArgs e)
    {
        var hit =
            lvRequestQueue.HitTest(
                e.Location);

        if (hit.Item is null)
        {
            return;
        }

        HandleRequestQueueItemActivate(hit.Item);
    }

    private void HandleRequestQueueItemActivate(
        ListViewItem? lvi)
    {
        if (lvi?.Tag is not AssetRequestItem item)
        {
            return;
        }

        if (item.IsCompleted
            || _completedRequestKeys.Contains(
                item.RequestKey))
        {
            // Done rows may be selected visually but never reactivated.
            return;
        }

        if (_state == UiState.ReferenceReady)
        {
            var sessionKey =
                _currentSession?.SourceRequestKey;

            if (sessionKey is null
                || !string.Equals(
                    sessionKey,
                    item.RequestKey,
                    StringComparison.Ordinal))
            {
                ShowMessageBox(
                    "Finish or cancel the current reference-assisted asset before selecting another Request.",
                    "Request selection blocked",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
        }

        _activeRequest = item;

        _settingRequestBoundFields = true;

        try
        {
            txtAssetFolderName.Text =
                item.AssetName;

            txtPrompt.Text =
                item.Prompt;
        }
        finally
        {
            _settingRequestBoundFields = false;
        }

        UpdatePromptPreview();
        TryCopyPromptToClipboard(item.Prompt);
        RefreshRequestQueueVisuals();
    }

    private void CheckActiveRequestBinding()
    {
        if (_settingRequestBoundFields
            || _activeRequest is null)
        {
            return;
        }

        var stillMatches =
            string.Equals(
                txtAssetFolderName.Text.Trim(),
                _activeRequest.AssetName,
                StringComparison.Ordinal)
            && string.Equals(
                txtPrompt.Text,
                _activeRequest.Prompt,
                StringComparison.Ordinal);

        if (stillMatches)
        {
            return;
        }

        _activeRequest =
            null;

        RefreshRequestQueueVisuals();
    }

    private void TryCopyPromptToClipboard(
        string prompt)
    {
        try
        {
            if (ClipboardWriter is not null)
            {
                ClipboardWriter(prompt);
                return;
            }

            Clipboard.SetText(prompt);
        }
        catch (Exception)
        {
            ShowMessageBox(
                "The Request was loaded successfully, but its Prompt could not be copied to the clipboard."
                + Environment.NewLine
                + Environment.NewLine
                + "You can still use the Prompt shown in Final Prompt.",
                "Clipboard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ApplyRequestQueueState()
    {
        if (IsDisposed || Disposing || btnImportRequest.IsDisposed)
        {
            return;
        }

        if (_state == UiState.ReferenceReady)
        {
            var sessionKey =
                _currentSession?.SourceRequestKey;

            if (sessionKey is null)
            {
                // Manual Reference session: import blocked until done/cancelled.
                btnImportRequest.Enabled = false;
            }
            else if (_currentManifest is not null)
            {
                // Same-run queue already loaded.
                btnImportRequest.Enabled = false;
            }
            else
            {
                // Recovered queue-originated session without a loaded manifest:
                // import is allowed so the queue association can be restored.
                btnImportRequest.Enabled = true;
            }
        }
        else
        {
            btnImportRequest.Enabled = true;
        }
    }

    /// <summary>
    /// Marks the matching Request Done only after the Main durable commit.
    /// Progress persistence is updated only while the manifest is loaded.
    /// </summary>
    private void CompleteActiveRequestAfterMainCommit(
        AssetSession session)
    {
        var completedRequestKey =
            _activeRequest?.RequestKey
            ?? session.SourceRequestKey;

        _activeRequest = null;

        if (string.IsNullOrWhiteSpace(completedRequestKey)
            || _currentManifest is null)
        {
            return;
        }

        var item =
            _currentManifest.Items.FirstOrDefault(
                request =>
                    string.Equals(
                        request.RequestKey,
                        completedRequestKey,
                        StringComparison.Ordinal));

        if (item is null)
        {
            return;
        }

        item.IsCompleted = true;
        _completedRequestKeys.Add(completedRequestKey);

        try
        {
            _requestProgressService?.Save(
                _currentManifest.ManifestFingerprint,
                _completedRequestKeys);
        }
        catch
        {
            // Progress is bookkeeping; never roll back the completed asset.
        }

        RefreshRequestQueueVisuals();
        UpdateRequestProgressLabel();
    }

    private void HandleRequestCancellation()
    {
        _activeRequest = null;
        RefreshRequestQueueVisuals();
        UpdateRequestProgressLabel();
    }

    private void UpdateRequestProgressLabel()
    {
        if (IsDisposed || Disposing || lblRequestProgress.IsDisposed)
        {
            return;
        }

        if (_currentManifest is null)
        {
            lblRequestProgress.Text = string.Empty;
            return;
        }

        lblRequestProgress.Text =
            $"{_completedRequestKeys.Count} of {_currentManifest.Items.Count} done";
    }

    /// <summary>
    /// Rebinds a recovered queue-originated Reference session when the manifest
    /// is imported (or re-imported) and contains the durable Request key.
    /// </summary>
    private void BindRecoveredSessionRequest()
    {
        if (_state != UiState.ReferenceReady
            || _currentSession?.SourceRequestKey is null
            || _currentManifest is null)
        {
            return;
        }

        var item =
            _currentManifest.Items.FirstOrDefault(
                request =>
                    string.Equals(
                        request.RequestKey,
                        _currentSession.SourceRequestKey,
                        StringComparison.Ordinal));

        if (item is null
            || item.IsCompleted
            || _completedRequestKeys.Contains(
                item.RequestKey))
        {
            return;
        }

        _activeRequest = item;

        _settingRequestBoundFields = true;

        try
        {
            txtAssetFolderName.Text =
                _currentSession.AssetFolderName;

            txtPrompt.Text = item.Prompt;
        }
        finally
        {
            _settingRequestBoundFields = false;
        }

        UpdatePromptPreview();
        RefreshRequestQueueVisuals();
    }
}