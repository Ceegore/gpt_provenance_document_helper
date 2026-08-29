#nullable enable
using System.Windows.Forms;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private void HandleMainImageEntryPoint()
    {
        if (!chkDirectMode.Checked)
        {
            HandleMainImage();
            return;
        }

        HandleDirectMainImage();
    }

    private void HandleDirectMainImage()
    {
        if (chkNoReference.Checked)
        {
            if (!TryAutoSelectLatestMain())
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
            if (!TryAutoSelectLatestMain())
            {
                return;
            }

            HandleMainImage();
            return;
        }

        if (!TrySelectDirectReferencePair())
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

    private bool TrySelectDirectReferencePair()
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

        IReadOnlyList<string> latest;

        try
        {
            latest =
                _imageFinderService
                    .FindLatestImages(
                        settings,
                        2);
        }
        catch (Exception ex)
        {
            ShowError(
                "Could not scan the Image Download Folder.",
                ex);

            return false;
        }

        if (latest.Count < 2)
        {
            ShowMessageBox(
                "Direct reference mode requires two downloaded images.\n\n"
                + "Download the Reference image first and the Main image second.",
                "Two images required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return false;
        }

        var main =
            latest[0];

        var reference =
            latest[1];

        if (ValidationService.PathsEqual(
                main,
                reference))
        {
            ShowMessageBox(
                "Reference and Main resolved to the same file.",
                "Invalid Direct selection",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return false;
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

            return false;
        }

        var mainValidation =
            _validationService.ValidateImageFile(
                main,
                _settings.AcceptedExtensions);

        if (!mainValidation.IsValid)
        {
            ShowValidationError(
                "Direct Main image is invalid.",
                mainValidation);

            return false;
        }

        SetSelectedImage(
            ImageSlot.Reference,
            reference);

        SetSelectedImage(
            ImageSlot.Main,
            main);

        return true;
    }
}