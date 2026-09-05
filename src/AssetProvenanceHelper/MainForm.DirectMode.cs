#nullable enable
using System.Windows.Forms;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private void HandleMainImageEntryPoint()
    {
        if (_activeApiCandidateMetadata is not null)
        {
            HandleMainImage();
            return;
        }

        if (!chkDirectMode.Checked)
        {
            HandleMainImage();
            return;
        }

        HandleDirectMainImage();
    }

    private void HandleDirectMainImage()
    {
        var variantCount = GetSelectedVariantCount();

        if (chkNoReference.Checked)
        {
            // No-Reference + variants: the batch does its own N-image resolution.
            if (variantCount == 0 && !TryAutoSelectLatestMain())
            {
                return;
            }

            HandleMainImage();
            return;
        }

        if (_state == UiState.ReferenceReady)
        {
            // Existing Reference is already durable.
            // Retry/continuation refreshes only Main.
            if (variantCount == 0)
            {
                if (!TryAutoSelectLatestMain())
                {
                    return;
                }

                HandleMainImage();
                return;
            }

            var retryMains = TryResolveVariantMainImages(variantCount);
            if (retryMains is null)
            {
                return;
            }

            HandleVariantBatch(variantCount, retryMains);
            return;
        }

        if (variantCount == 0)
        {
            if (TrySelectDirectReferencePair() is null)
            {
                return;
            }

            HandleReference();

            if (IsDisposed
                || _currentSession is null
                || _state != UiState.ReferenceReady)
            {
                return;
            }

            // Main candidate selected by pair preflight is still held.
            HandleMainImage();
            return;
        }

        var mains = TrySelectDirectReferencePair(variantCount);
        if (mains is null)
        {
            return;
        }

        HandleReference();

        if (IsDisposed
            || _currentSession is null
            || _state != UiState.ReferenceReady)
        {
            return;
        }

        HandleVariantBatch(variantCount, mains);
    }

    private bool TryAutoSelectLatestMain()
    {
        var validation =
            _validationService.ValidateDownloadFolder(
                txtDownloadFolder.Text);

        if (!validation.IsValid)
        {
            HighlightField(
                pnlDownloadFolderHost,
                true);

            ShowValidationError(
                "Direct mode requires a valid Image Download Folder.",
                validation);

            return false;
        }

        var settings =
            new AppSettings
            {
                DownloadFolder =
                    txtDownloadFolder.Text,

                AcceptedExtensions =
                    _settings.AcceptedExtensions.ToList()
            };

        string? latest;

        try
        {
            latest =
                _imageFinderService.FindLatestImage(
                    settings);
        }
        catch (Exception ex)
        {
            ShowError(
                "Could not scan the Image Download Folder.",
                ex);

            return false;
        }

        if (string.IsNullOrWhiteSpace(latest))
        {
            SetSelectedImage(
                ImageSlot.Main,
                null);

            ShowMessageBox(
                "No supported image was found in the Image Download Folder.",
                "No Main image found",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return false;
        }

        var imageValidation =
            _validationService.ValidateImageFile(
                latest,
                _settings.AcceptedExtensions);

        if (!imageValidation.IsValid)
        {
            SetSelectedImage(
                ImageSlot.Main,
                null);

            ShowValidationError(
                "Latest image is invalid.",
                imageValidation);

            return false;
        }

        SetSelectedImage(
            ImageSlot.Main,
            latest);

        return true;
    }

    /// <summary>
    /// Resolves a Direct-mode Reference + Main selection. With mainCount == 1 (the
    /// default) this is byte-for-byte the original two-image behavior. With
    /// mainCount > 1 (Direct + Reference-assisted + Variants, plan §4.7) it resolves
    /// mainCount + 1 images: the oldest becomes the Reference, and the newest
    /// mainCount become the ordered (oldest-first) Main variants.
    /// Returns null after reporting the problem; otherwise the ordered Main list
    /// (length == mainCount). The Reference slot is set as a side effect on success.
    /// </summary>
    private IReadOnlyList<string>? TrySelectDirectReferencePair(int mainCount = 1)
    {
        var downloadValidation =
            _validationService.ValidateDownloadFolder(
                txtDownloadFolder.Text);

        if (!downloadValidation.IsValid)
        {
            HighlightField(
                pnlDownloadFolderHost,
                true);

            ShowValidationError(
                "Direct mode requires a valid Image Download Folder.",
                downloadValidation);

            return null;
        }

        var settings =
            new AppSettings
            {
                DownloadFolder =
                    txtDownloadFolder.Text,

                AcceptedExtensions =
                    _settings.AcceptedExtensions.ToList()
            };

        IReadOnlyList<string> latest;

        try
        {
            latest =
                _imageFinderService
                    .FindLatestImages(
                        settings,
                        mainCount + 1);
        }
        catch (Exception ex)
        {
            ShowError(
                "Could not scan the Image Download Folder.",
                ex);

            return null;
        }

        if (latest.Count < mainCount + 1)
        {
            ShowMessageBox(
                mainCount == 1
                    ? "Direct reference mode requires two downloaded images.\n\n"
                      + "Download the Reference image first and the Main image second."
                    : $"Direct reference mode with Variants set to {mainCount} requires "
                      + $"{mainCount + 1} downloaded images: one Reference plus {mainCount} Main variants.",
                "Two images required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return null;
        }

        var mains =
            latest
                .Take(mainCount)
                .Reverse()
                .ToList();

        var reference =
            latest[mainCount];

        if (mains.Any(main => ValidationService.PathsEqual(main, reference)))
        {
            ShowMessageBox(
                "Reference and Main resolved to the same file.",
                "Invalid Direct selection",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return null;
        }

        var referenceValidation =
            _validationService.ValidateImageFile(
                reference,
                _settings.AcceptedExtensions);

        if (!referenceValidation.IsValid)
        {
            ShowValidationError(
                "Direct Reference image is invalid.",
                referenceValidation);

            return null;
        }

        foreach (var main in mains)
        {
            var mainValidation =
                _validationService.ValidateImageFile(
                    main,
                    _settings.AcceptedExtensions);

            if (!mainValidation.IsValid)
            {
                ShowValidationError(
                    "Direct Main image is invalid.",
                    mainValidation);

                return null;
            }
        }

        SetSelectedImage(
            ImageSlot.Reference,
            reference);

        SetSelectedImage(
            ImageSlot.Main,
            mains[0]);

        return mains;
    }
}