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

            // Also update legacy labels if present for backwards compatibility in tests
            if (lblLatestImage != null)
            {
                lblLatestImage.Text = hasFile ? Path.GetFileName(path!) : "No image found.";
            }
            if (lblLatestTimestamp != null)
            {
                lblLatestTimestamp.Text = hasFile ? $"Modified: {File.GetLastWriteTime(path!):yyyy-MM-dd HH:mm:ss}" : "Modified: -";
            }
            if (lblManualSelection != null)
            {
                lblManualSelection.Text = hasFile ? $"Manual selection: {path}" : "Manual selection: none";
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

        var latest = _imageFinderService.FindLatestImage(settings);
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

    // Legacy method wrappers for backwards compatibility
    private void RefreshLatestImage()
    {
        RefreshImageSelection(ImageSlot.Reference);
    }

    private string? ResolveImageSelection()
    {
        return GetSelectedImage(ImageSlot.Reference);
    }

    private void ChooseFile()
    {
        ChooseImageFile(ImageSlot.Reference);
    }

    private void SetManualSelection(string path)
    {
        SetSelectedImage(ImageSlot.Reference, path);
    }

    private void ClearManualSelection()
    {
        SetSelectedImage(ImageSlot.Reference, null);
    }

    private void ManualSelection_DragEnter(object? sender, DragEventArgs e)
    {
        ImageDrop_DragEnter(sender, e);
    }

    private void ManualSelection_DragDrop(object? sender, DragEventArgs e)
    {
        ImageDrop_DragDrop(ImageSlot.Reference, e);
    }
}
