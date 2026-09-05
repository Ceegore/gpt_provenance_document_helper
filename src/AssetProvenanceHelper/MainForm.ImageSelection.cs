#nullable enable
using System.Drawing;
using System.Windows.Forms;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private string? _referenceImagePath;
    private string? _mainImagePath;

    internal string? GetSelectedImage(ImageSlot slot) =>
        slot switch
        {
            ImageSlot.Reference => _referenceImagePath,
            ImageSlot.Main => _mainImagePath,
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };

    internal void SetSelectedImage(ImageSlot slot, string? path)
    {
        switch (slot)
        {
            case ImageSlot.Reference:
                _referenceImagePath = path;
                UpdateImageSlotUi(ImageSlot.Reference, path);
                break;

            case ImageSlot.Main:
                _mainImagePath = path;
                if (_activeApiCandidateMetadata != null && !string.Equals(_activeApiCandidateMetadata.NormalizedImagePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _activeApiCandidateMetadata = null;
                }
                UpdateImageSlotUi(ImageSlot.Main, path);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(slot));
        }
    }

    private void UpdateImageSlotUi(ImageSlot slot, string? path)
    {
        var hasFile = !string.IsNullOrWhiteSpace(path) && File.Exists(path);

        if (slot == ImageSlot.Reference)
        {
            if (hasFile)
            {
                var info = new FileInfo(path!);
                lblReferenceSelectedImage.Text = $"Selected candidate: {info.Name}";
                lblReferenceTimestamp.Text = $"Modified: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
                _toolTip.SetToolTip(lblReferenceSelectedImage, path);
            }
            else
            {
                lblReferenceSelectedImage.Text = "Selected candidate: none";
                lblReferenceTimestamp.Text = "Modified: -";
                _toolTip.SetToolTip(lblReferenceSelectedImage, null);
            }

            ClearReferenceValidationVisuals();
        }
        else if (slot == ImageSlot.Main)
        {
            if (hasFile)
            {
                var info = new FileInfo(path!);
                lblMainSelectedImage.Text = $"Selected: {info.Name}";
                lblMainTimestamp.Text = $"Modified: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
                _toolTip.SetToolTip(lblMainSelectedImage, path);
            }
            else
            {
                lblMainSelectedImage.Text = "Selected: none";
                lblMainTimestamp.Text = "Modified: -";
                _toolTip.SetToolTip(lblMainSelectedImage, null);
            }

            ClearMainValidationVisuals();
        }
    }

    internal void RefreshImageSelection(ImageSlot slot)
    {
        if (slot == ImageSlot.Main)
        {
            var variantCount = GetSelectedVariantCount();
            if (variantCount > 0)
            {
                RefreshMainVariantBatchSelection(variantCount);
                return;
            }
        }

        var downloadFolder = txtDownloadFolder.Text;
        var validation = _validationService.ValidateDownloadFolder(downloadFolder);
        if (!validation.IsValid)
        {
            HighlightField(pnlDownloadFolderHost, true);
            txtDownloadFolder.Focus();
            ShowValidationError("Image Download Folder is required for Refresh.", validation);
            return;
        }

        HighlightField(pnlDownloadFolderHost, false);

        var settings = new AppSettings
        {
            DownloadFolder = downloadFolder,
            AcceptedExtensions = _settings.AcceptedExtensions
        };

        string? latest;
        try
        {
            latest = _imageFinderService.FindLatestImage(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HighlightField(pnlDownloadFolderHost, true);
            txtDownloadFolder.Focus();
            AddStatus($"Error scanning image download folder: {ex.Message}");
            ShowError($"Could not scan image download folder '{downloadFolder}'.", ex);
            return;
        }

        if (latest is null)
        {
            SetSelectedImage(slot, null);
            AddStatus($"No image found in '{downloadFolder}'.");
            return;
        }

        var imageValidation = _validationService.ValidateImageFile(latest, _settings.AcceptedExtensions);
        if (!imageValidation.IsValid)
        {
            SetSelectedImage(slot, null);
            ShowValidationError("Found image is invalid.", imageValidation);
            return;
        }

        SetSelectedImage(slot, latest);
        AddStatus($"Selected {slot} image from download folder: {Path.GetFileName(latest)}");
    }

    internal void ChooseImageFile(ImageSlot slot)
    {
        string? selectedFilePath = null;

        if (OpenFileDialogProvider is not null)
        {
            selectedFilePath = OpenFileDialogProvider(this, txtDownloadFolder.Text);
            if (selectedFilePath is null)
            {
                return;
            }
        }
        else
        {
            using var dialog = new OpenFileDialog
            {
                Title = $"Choose {slot} image",
                Filter = "Image files (*.png;*.webp;*.jpg;*.jpeg)|*.png;*.webp;*.jpg;*.jpeg|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (Directory.Exists(txtDownloadFolder.Text))
            {
                dialog.InitialDirectory = txtDownloadFolder.Text;
            }

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            selectedFilePath = dialog.FileName;
        }

        var validation = _validationService.ValidateImageFile(selectedFilePath, _settings.AcceptedExtensions);
        if (!validation.IsValid)
        {
            ShowValidationError("Invalid image", validation);
            return;
        }

        SetSelectedImage(slot, selectedFilePath);
        AddStatus($"Selected {slot} image: {Path.GetFileName(selectedFilePath)}");
    }

    private void ImageDrop_DragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
        }
        else
        {
            e.Effect = DragDropEffects.None;
        }
    }

    private void ImageDrop_DragDrop(ImageSlot slot, DragEventArgs e)
    {
        try
        {
            var files = e.Data?.GetData(DataFormats.FileDrop) as string[];
            if (files is null || files.Length != 1)
            {
                ShowMessageBox(
                    "Drop exactly one image file.",
                    "Invalid drop",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var filePath = files[0];
            var validation = _validationService.ValidateImageFile(filePath, _settings.AcceptedExtensions);
            if (!validation.IsValid)
            {
                ShowValidationError("Invalid dropped image", validation);
                return;
            }

            SetSelectedImage(slot, filePath);
            AddStatus($"Dropped {slot} image: {Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            ShowError("Could not use dropped image.", ex);
        }
    }
}
