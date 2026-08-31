#nullable enable
using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper;

partial class MainForm
{
    /// <summary>0 = "none". Otherwise 1..MaxVariantCount.</summary>
    private int GetSelectedVariantCount() => Math.Max(0, cmbVariants.SelectedIndex);

    private void ResetVariantSelectionToNone() => cmbVariants.SelectedIndex = 0;

    /// <summary>
    /// Resolves the N newest supported download-folder images for a variants batch,
    /// ordered OLDEST FIRST so index 0 becomes variant "A" (plan D-2).
    /// Returns null after reporting the problem.
    /// </summary>
    private IReadOnlyList<string>? TryResolveVariantMainImages(int count)
    {
        var downloadValidation = _validationService.ValidateDownloadFolder(txtDownloadFolder.Text);
        if (!downloadValidation.IsValid)
        {
            HighlightField(pnlDownloadFolderHost, true);
            ShowValidationError("Variants requires a valid Image Download Folder.", downloadValidation);
            return null;
        }

        var settings = ReadSettingsFromUi();

        IReadOnlyList<string> latest;
        try
        {
            latest = _imageFinderService.FindLatestImages(settings, count);
        }
        catch (Exception ex)
        {
            ShowError("Could not scan the Image Download Folder.", ex);
            return null;
        }

        if (latest.Count < count)
        {
            ShowMessageBox(
                $"Variants is set to {count} but only {latest.Count} supported images "
                + "were found in the Image Download Folder.",
                "Not enough images for Variants",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return null;
        }

        foreach (var path in latest)
        {
            var imageValidation = _validationService.ValidateImageFile(path, settings.AcceptedExtensions);
            if (!imageValidation.IsValid)
            {
                ShowValidationError($"Image '{Path.GetFileName(path)}' is invalid.", imageValidation);
                return null;
            }
        }

        var ordered = latest.ToList();
        ordered.Reverse();
        return ordered;
    }

    /// <summary>
    /// Derives every variant asset name and confirms no variant destination exists
    /// yet. Returns the names in variant order, or null after reporting the first
    /// problem, having touched nothing.
    /// </summary>
    private IReadOnlyList<string>? TryResolveVariantAssetNames(string baseName, int count, bool referenceAssisted)
    {
        var nameValidation = _validationService.ValidateAssetName(baseName, _settings.AcceptedExtensions);
        if (!nameValidation.IsValid)
        {
            HighlightField(pnlAssetFolderNameHost, true);
            ShowValidationError("Asset Name is invalid.", nameValidation);
            return null;
        }

        var names = new List<string>(count);

        for (var i = 1; i <= count; i++)
        {
            var name = AssetNaming.BuildVariantAssetName(baseName, i);
            names.Add(name);

            // Reference-assisted exception: variant A's folder must already exist -
            // the Reference click created it. Getting this backwards makes every
            // reference-assisted batch abort immediately.
            if (referenceAssisted && i == 1)
            {
                continue;
            }

            var targetFolder = Path.Combine(txtAssetRoot.Text, name);
            if (Directory.Exists(targetFolder))
            {
                ShowMessageBox(
                    $"Variant folder '{name}' already exists.\n\n"
                    + "The variants batch was aborted before any variant was created. "
                    + "Variants never offer \"Use Existing\" - remove the existing folder "
                    + "or choose a different Asset Name, then retry.",
                    "Variant destination exists",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return null;
            }
        }

        return names;
    }

    /// <summary>
    /// Runs a variants batch (plan §4.11). Produces `count` complete, independent
    /// assets sequentially, named baseName + "A".."J" oldest-to-newest.
    /// </summary>
    private void HandleVariantBatch(int count, IReadOnlyList<string>? precomputedMains = null)
    {
        if (!ValidateMainActionUi(requireSelectedMainImage: false))
        {
            return;
        }

        var isNoReference = chkNoReference.Checked || (_currentSession?.WorkflowMode == AssetWorkflowMode.NoReference);
        var referenceAssisted = false;

        if (isNoReference && _currentSession is null)
        {
            if (!CanStartNewAssetWithProvider)
            {
                ShowMessageBox(
                    "No valid AI Generation Provider template is available.\n\n"
                    + "Add a valid template to the provider_templates folder and restart the application.",
                    "Provider required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
        }
        else if (_currentSession is not null)
        {
            if (_state != UiState.ReferenceReady)
            {
                ShowMessageBox(
                    "No active reference session exists.",
                    "Main Image",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            referenceAssisted = true;

            var baseNameForCheck = txtAssetFolderName.Text.Trim();
            var expectedA = AssetNaming.BuildVariantAssetName(baseNameForCheck, 1);

            if (!string.Equals(_currentSession.AssetFolderName, expectedA, StringComparison.Ordinal))
            {
                // Recovered session, or a reference created while Variants was "none".
                // Refuse rather than derive image1AA / write into the wrong folder.
                ShowMessageBox(
                    "The active reference session was not created as a variant "
                    + $"('{_currentSession.AssetFolderName}' instead of '{expectedA}').\n\n"
                    + "Finish or cancel it, then start a new asset with Variants set "
                    + "before clicking Reference.",
                    "Variants unavailable",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
        }
        else
        {
            ShowMessageBox(
                "No active reference session exists.",
                "Main Image",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var settings = ReadSettingsFromUi();
        var baseName = txtAssetFolderName.Text.Trim();

        var mains = precomputedMains ?? TryResolveVariantMainImages(count);
        if (mains is null)
        {
            return;
        }

        var names = TryResolveVariantAssetNames(baseName, count, referenceAssisted);
        if (names is null)
        {
            return;
        }

        var reused = mains
            .Where(m => _committedMainSourcesThisSession.Contains(ValidationService.NormalizePath(m)))
            .ToList();

        if (reused.Count > 0)
        {
            var proceed = TwoChoiceDialog.ShowChoice(
                this,
                "Images already processed",
                $"{reused.Count} of these {mains.Count} images were already processed "
                + "in this session:" + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine, reused.Select(Path.GetFileName))
                + Environment.NewLine + Environment.NewLine
                + "Process them again anyway?",
                "Process Again",
                "Cancel");

            if (!proceed)
            {
                return;
            }
        }

        string? refSource = null;
        DateTimeOffset refProcessedAt = default;
        ProviderTemplateSnapshot? providerSnapshot = null;
        string? requestKey = null;
        string? requestKeyForCompletion = _activeRequest?.RequestKey;

        if (referenceAssisted)
        {
            // Variant A's session is deleted at its own commit point, so this
            // authority must be captured before variant A commits.
            refSource = _currentSession!.ReferenceDestinationPath;
            refProcessedAt = _currentSession.ReferenceProcessedAt;
            providerSnapshot = _currentSession.ProviderTemplate?.Clone();
            requestKey = _currentSession.SourceRequestKey;
            requestKeyForCompletion ??= _currentSession.SourceRequestKey;
        }

        var prompt = txtPrompt.Text;
        var processedAt = DateTimeOffset.Now;

        var completedNames = new List<string>();
        string? failureName = null;
        string? failureReason = null;

        for (var i = 1; i <= count; i++)
        {
            if (IsDisposed)
            {
                break;
            }

            SetSelectedImage(ImageSlot.Main, mains[i - 1]);

            bool ok;

            if (referenceAssisted)
            {
                AssetSession variantSession;

                if (i == 1)
                {
                    // Reference already committed by the Reference click.
                    variantSession = _currentSession!;
                }
                else
                {
                    AssetSession? preparedVariantSession = null;

                    try
                    {
                        preparedVariantSession = _assetProcessorService.CreateReferenceSession(
                            settings,
                            names[i - 1],
                            refSource!,
                            refProcessedAt,
                            providerSnapshot,
                            requestKey);

                        _sessionService.Save(preparedVariantSession);

                        variantSession = _assetProcessorService.ProcessReference(
                            preparedVariantSession,
                            settings,
                            refSource,
                            refProcessedAt);

                        _sessionService.Save(variantSession);

                        _currentSession = variantSession;

                        RecordRecentDocument(
                            ProvenanceDocumentKind.Reference,
                            variantSession.ReferenceProvenancePath,
                            variantSession.AssetFolderName,
                            refProcessedAt);
                    }
                    catch (Exception ex)
                    {
                        if (preparedVariantSession is not null)
                        {
                            try
                            {
                                var rollback = _assetProcessorService.RollbackReference(preparedVariantSession);
                                if (rollback.IsValid)
                                {
                                    _sessionService.Delete();
                                }
                            }
                            catch
                            {
                                // Best-effort cleanup; the primary error is reported below.
                            }
                        }

                        _currentSession = null;
                        _state = UiState.Idle;

                        ShowError(
                            $"Could not replicate the reference for variant {AssetNaming.GetVariantSuffix(i)}.",
                            ex);
                        failureName = names[i - 1];
                        failureReason = ex.Message;
                        break;
                    }
                }

                try
                {
                    _assetProcessorService.PrepareMainCommit(
                        variantSession,
                        settings.AcceptedExtensions,
                        mains[i - 1],
                        prompt,
                        processedAt);
                }
                catch (Exception prepEx)
                {
                    ShowError("Could not prepare Main image commit for this variant.", prepEx);
                    failureName = names[i - 1];
                    failureReason = prepEx.Message;
                    break;
                }

                try
                {
                    _sessionService.Save(variantSession);
                }
                catch (Exception saveEx)
                {
                    variantSession.ResetMainCommitMetadata();
                    ShowError(
                        "Could not update session state before Main Image processing. Operation was aborted.",
                        saveEx);
                    failureName = names[i - 1];
                    failureReason = saveEx.Message;
                    break;
                }

                ok = ExecuteMainCommit(variantSession, mains[i - 1], prompt, processedAt, suppressUiCompletion: true);
            }
            else
            {
                ok = CommitNoReferenceAsset(
                    settings,
                    names[i - 1],
                    mains[i - 1],
                    prompt,
                    processedAt,
                    suppressUiCompletion: true);
            }

            if (!ok)
            {
                failureName ??= names[i - 1];
                break;
            }

            var assetFolderPath = Path.Combine(settings.AssetRootFolder, names[i - 1]);

            RecordRecentDocument(
                ProvenanceDocumentKind.Final,
                Path.Combine(assetFolderPath, AppConstants.FinalProvenanceFileName),
                names[i - 1],
                processedAt);

            _lastCompletedAssetFolderPath = assetFolderPath;
            completedNames.Add(names[i - 1]);
            AddStatus($"Variant {AssetNaming.GetVariantSuffix(i)} completed: {names[i - 1]}");
            OnVariantCommittedHook?.Invoke(i, names[i - 1]);
        }

        if (IsDisposed)
        {
            return;
        }

        CompleteVariantBatchUi(count, completedNames, referenceAssisted, requestKeyForCompletion, failureName, failureReason);
    }

    private void CompleteVariantBatchUi(
        int totalCount,
        IReadOnlyList<string> completedNames,
        bool referenceAssisted,
        string? requestKeyForCompletion,
        string? failureName,
        string? failureReason)
    {
        var fullSuccess = completedNames.Count == totalCount;

        if (referenceAssisted && fullSuccess)
        {
            _currentSession = null;
            _state = UiState.Idle;
        }

        try
        {
            if (fullSuccess)
            {
                CompleteActiveRequestAfterMainCommit(
                    new AssetSession { SourceRequestKey = requestKeyForCompletion });
            }

            ResetAssetInputFieldsAfterDurableAction();
            ReloadProviderCatalog();

            var summary = fullSuccess
                ? $"{completedNames.Count} of {totalCount} variants completed: "
                  + string.Join(", ", completedNames) + "."
                : BuildPartialVariantBatchSummary(totalCount, completedNames, failureName, failureReason);

            AddStatus(summary.Replace(Environment.NewLine, " "));

            ApplyState();

            ShowMessageBox(
                summary,
                fullSuccess ? "Variants Complete" : "Variants Batch Incomplete",
                MessageBoxButtons.OK,
                fullSuccess ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            // Never roll back a committed asset.
            try
            {
                ShowMessageBox(
                    "The variants batch finished, but the interface could not be refreshed."
                    + Environment.NewLine
                    + Environment.NewLine
                    + ex.Message,
                    "Post-Commit UI Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
            }

            Close();
        }
    }

    private static string BuildPartialVariantBatchSummary(
        int totalCount,
        IReadOnlyList<string> completedNames,
        string? failureName,
        string? failureReason)
    {
        var succeededPart = completedNames.Count > 0
            ? "Succeeded: " + string.Join(", ", completedNames) + "."
            : "No variants completed.";

        var failedPart = failureName is not null
            ? "Failed: " + failureName + (failureReason is not null ? $" ({failureReason})" : ".")
            : string.Empty;

        return
            $"{completedNames.Count} of {totalCount} variants completed."
            + Environment.NewLine + Environment.NewLine
            + succeededPart
            + (string.IsNullOrEmpty(failedPart) ? string.Empty : Environment.NewLine + failedPart)
            + Environment.NewLine + Environment.NewLine
            + "The Request stays Pending."
            + Environment.NewLine
            + "To retry, remove the completed variant folders or change the Asset Name - "
            + "retrying with the same name will fail because those folders already exist.";
    }

    /// <summary>
    /// With Variants active, Main Refresh previews the N images that would be used
    /// instead of selecting a single image (plan §4.14).
    /// </summary>
    private void RefreshMainVariantBatchSelection(int count)
    {
        var mains = TryResolveVariantMainImages(count);
        if (mains is null)
        {
            return;
        }

        ShowMainVariantBatchLabel(mains);
        AddStatus($"Selected {mains.Count} variant Main images from download folder.");
    }

    private void ShowMainVariantBatchLabel(IReadOnlyList<string> mains)
    {
        var first = Path.GetFileName(mains[0]);
        var last = Path.GetFileName(mains[^1]);

        lblMainSelectedImage.Text = mains.Count == 1
            ? $"Selected: 1 variant ({first})"
            : $"Selected: {mains.Count} variants ({first} → {last})";

        lblMainTimestamp.Text = "Modified: -";

        _toolTip.SetToolTip(
            lblMainSelectedImage,
            string.Join(Environment.NewLine, mains.Select(Path.GetFileName)));

        ClearMainValidationVisuals();
    }
}
