#nullable enable
using System.Drawing;
using System.Windows.Forms;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Ui;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private System.Windows.Forms.Timer? _ctaPulseTimer;
    private int _ctaPulseCount;
    private Button? _pulsingButton;
    private Color _pulsingOriginalColor;

    internal void HighlightField(Control? host, bool isError)
    {
        if (host is null)
        {
            return;
        }

        host.BackColor = isError ? UiTheme.Error : UiTheme.Border;
    }

    internal void ClearValidationVisuals()
    {
        StopCtaPulse();

        HighlightField(pnlDownloadFolderHost, false);
        HighlightField(pnlAssetRootHost, false);
        HighlightField(pnlAssetFolderNameHost, false);
        HighlightField(pnlReferenceImageHost, false);
        HighlightField(pnlMainImageHost, false);
        HighlightField(pnlPromptHost, false);

        btnReference.BackColor = UiTheme.ReferenceAccent;
        btnMainImage.BackColor = UiTheme.MainAccent;
    }

    internal void ClearReferenceValidationVisuals()
    {
        HighlightField(pnlReferenceImageHost, false);
        if (_pulsingButton == btnReference)
        {
            StopCtaPulse();
            btnReference.BackColor = UiTheme.ReferenceAccent;
        }
    }

    internal void ClearMainValidationVisuals()
    {
        HighlightField(pnlMainImageHost, false);
        if (_pulsingButton == btnMainImage && !IsPromptInvalid())
        {
            StopCtaPulse();
            btnMainImage.BackColor = UiTheme.MainAccent;
        }
    }

    internal void ClearPromptValidation()
    {
        HighlightField(pnlPromptHost, false);
        if (_pulsingButton == btnMainImage && !IsMainImageInvalid())
        {
            StopCtaPulse();
            btnMainImage.BackColor = UiTheme.MainAccent;
        }
    }

    private bool IsPromptInvalid() =>
        pnlPromptHost?.BackColor == UiTheme.Error;

    private bool IsMainImageInvalid() =>
        pnlMainImageHost?.BackColor == UiTheme.Error;

    internal bool ValidateRefreshUi(ImageSlot slot)
    {
        var downloadFolder = txtDownloadFolder.Text;
        var validation = _validationService.ValidateDownloadFolder(downloadFolder);
        if (!validation.IsValid)
        {
            HighlightField(pnlDownloadFolderHost, true);
            txtDownloadFolder.Focus();
            ShowValidationError("Image Download Folder is required for Refresh.", validation);
            return false;
        }

        HighlightField(pnlDownloadFolderHost, false);
        return true;
    }

    internal bool ValidateReferenceActionUi()
    {
        ClearValidationVisuals();

        var assetRoot = txtAssetRoot.Text;
        var rootValidation = _validationService.ValidateAssetRootFolder(assetRoot);
        var assetName = txtAssetFolderName.Text;
        var nameValidation = _validationService.ValidateAssetName(assetName, _settings.AcceptedExtensions);
        var refImage = GetSelectedImage(ImageSlot.Reference);
        var templateValidation = _templateService.ValidateTemplates();

        bool hasError = false;
        Control? firstInvalid = null;

        if (!rootValidation.IsValid)
        {
            HighlightField(pnlAssetRootHost, true);
            hasError = true;
            firstInvalid ??= txtAssetRoot;
        }

        if (!nameValidation.IsValid)
        {
            HighlightField(pnlAssetFolderNameHost, true);
            hasError = true;
            firstInvalid ??= txtAssetFolderName;
        }

        if (string.IsNullOrWhiteSpace(refImage) || !File.Exists(refImage))
        {
            HighlightField(pnlReferenceImageHost, true);
            hasError = true;
            firstInvalid ??= btnChooseReference;
        }
        else
        {
            var imgValidation = _validationService.ValidateImageFile(refImage, _settings.AcceptedExtensions);
            if (!imgValidation.IsValid)
            {
                HighlightField(pnlReferenceImageHost, true);
                hasError = true;
                firstInvalid ??= btnChooseReference;
            }
        }

        if (!templateValidation.IsValid)
        {
            hasError = true;
        }

        if (hasError)
        {
            StartCtaPulse(btnReference, UiTheme.ReferenceAccent);
            firstInvalid?.Focus();
            return false;
        }

        return true;
    }

    internal bool ValidateMainActionUi(bool requireSelectedMainImage = true)
    {
        ClearValidationVisuals();

        bool isNoReference = chkNoReference.Checked || (_currentSession?.WorkflowMode == AssetWorkflowMode.NoReference);
        bool hasError = false;
        Control? firstInvalid = null;

        if (isNoReference)
        {
            var assetRoot = txtAssetRoot.Text;
            var rootValidation = _validationService.ValidateAssetRootFolder(assetRoot);
            var assetName = txtAssetFolderName.Text;
            var nameValidation = _validationService.ValidateAssetName(assetName, _settings.AcceptedExtensions);

            if (!rootValidation.IsValid)
            {
                HighlightField(pnlAssetRootHost, true);
                hasError = true;
                firstInvalid ??= txtAssetRoot;
            }

            if (!nameValidation.IsValid)
            {
                HighlightField(pnlAssetFolderNameHost, true);
                hasError = true;
                firstInvalid ??= txtAssetFolderName;
            }
        }

        if (requireSelectedMainImage)
        {
            var mainImage = GetSelectedImage(ImageSlot.Main);
            if (string.IsNullOrWhiteSpace(mainImage) || !File.Exists(mainImage))
            {
                HighlightField(pnlMainImageHost, true);
                hasError = true;
                firstInvalid ??= btnChooseMain;
            }
            else
            {
                var imgValidation = _validationService.ValidateImageFile(mainImage, _settings.AcceptedExtensions);
                if (!imgValidation.IsValid)
                {
                    HighlightField(pnlMainImageHost, true);
                    hasError = true;
                    firstInvalid ??= btnChooseMain;
                }
            }
        }

        var prompt = txtPrompt.Text;
        var promptValidation = _validationService.ValidatePrompt(prompt);
        if (!promptValidation.IsValid)
        {
            HighlightField(pnlPromptHost, true);
            hasError = true;
            firstInvalid ??= txtPrompt;
        }

        var templateValidation = _templateService.ValidateTemplates();
        if (!templateValidation.IsValid)
        {
            hasError = true;
        }

        if (hasError)
        {
            StartCtaPulse(btnMainImage, UiTheme.MainAccent);
            firstInvalid?.Focus();
            return false;
        }

        return true;
    }

    private void StartCtaPulse(Button btn, Color normalAccent)
    {
        StopCtaPulse();

        _pulsingButton = btn;
        _pulsingOriginalColor = normalAccent;
        _ctaPulseCount = 0;

        _ctaPulseTimer = components != null
            ? new System.Windows.Forms.Timer(components)
            : new System.Windows.Forms.Timer();
        _ctaPulseTimer.Interval = 175;
        _ctaPulseTimer.Tick += (_, _) =>
        {
            if (_pulsingButton is null || _pulsingButton.IsDisposed)
            {
                StopCtaPulse();
                return;
            }

            _ctaPulseCount++;
            if (_ctaPulseCount >= 8)
            {
                _pulsingButton.BackColor = UiTheme.Error;
                StopCtaPulse();
                return;
            }

            _pulsingButton.BackColor = (_ctaPulseCount % 2 == 1)
                ? UiTheme.ErrorPulse
                : UiTheme.Error;
        };

        btn.BackColor = UiTheme.Error;
        _ctaPulseTimer.Start();
    }

    private void StopCtaPulse()
    {
        if (_ctaPulseTimer != null)
        {
            _ctaPulseTimer.Stop();
            _ctaPulseTimer.Dispose();
            _ctaPulseTimer = null;
        }

        _pulsingButton = null;
    }
}
