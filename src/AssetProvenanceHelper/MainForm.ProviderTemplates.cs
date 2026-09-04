#nullable enable
using System.Windows.Forms;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private bool CanStartNewAssetWithProvider =>
        _providerTemplateCatalogService is null
        || _selectedProvider is not null;

    private void LoadProviderCatalogAtStartup()
    {
        if (_providerTemplateCatalogService is null)
        {
            return;
        }

        _providerCatalog = _providerTemplateCatalogService.Load();

        _sessionSnapshotProviders.Clear();
        PopulateProviderComboBox();
        SelectProviderByFileName(
            _settings.SelectedProviderTemplateFileName);

        if (_selectedProvider is null)
        {
            var fallback =
                _providerCatalog?.Templates
                    .FirstOrDefault(
                        template =>
                            string.Equals(
                                template.FileName,
                                AppConstants.DefaultProviderTemplateFileName,
                                StringComparison.OrdinalIgnoreCase))
                ?? _providerCatalog?.Templates.FirstOrDefault();

            if (fallback is not null)
            {
                SelectProviderByFileName(
                    fallback.FileName);
            }
        }

        if (_selectedProvider is not null
            && !string.Equals(
                _settings.SelectedProviderTemplateFileName,
                _selectedProvider.FileName,
                StringComparison.Ordinal))
        {
            _settings.SelectedProviderTemplateFileName =
                _selectedProvider.FileName;
        }

        UpdateProviderWarning();
    }

    private void ReloadProviderCatalog()
    {
        if (_providerTemplateCatalogService is null)
        {
            return;
        }

        _providerCatalog = _providerTemplateCatalogService.Load();

        var previousFileName =
            _selectedProvider?.FileName;

        _sessionSnapshotProviders.Clear();
        PopulateProviderComboBox();

        if (!string.IsNullOrWhiteSpace(previousFileName))
        {
            SelectProviderByFileName(previousFileName);
        }

        UpdateProviderWarning();
    }

    private void PopulateProviderComboBox()
    {
        cmbProvider.Items.Clear();

        if (_providerCatalog is not null)
        {
            foreach (var template in _providerCatalog.Templates)
            {
                cmbProvider.Items.Add(template);
            }
        }

        foreach (var snapshotTemplate in _sessionSnapshotProviders)
        {
            cmbProvider.Items.Add(snapshotTemplate);
        }
    }

    internal void SelectProviderByFileName(string fileName)
    {
        _selectedProvider = null;

        foreach (var item in cmbProvider.Items)
        {
            if (item is ProviderTemplateDefinition definition
                && string.Equals(
                    definition.FileName,
                    fileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                _selectedProvider = definition;
                cmbProvider.SelectedItem = definition;
                return;
            }
        }

        cmbProvider.SelectedIndex = -1;
    }

    private void OnProviderSelectionChanged()
    {
        var selected =
            cmbProvider.SelectedItem
            as ProviderTemplateDefinition;

        _selectedProvider = selected;

        if (selected is not null
            && !selected.IsSessionSnapshot)
        {
            _settings.SelectedProviderTemplateFileName =
                selected.FileName;
        }

        ApplyState();
    }

    private ProviderTemplateSnapshot? GetProviderSnapshotForNewAsset()
    {
        if (_providerTemplateCatalogService is null)
        {
            return null;
        }

        return _selectedProvider?.CreateSnapshot();
    }

    private void UpdateProviderWarning()
    {
        if (_providerCatalog is null)
        {
            return;
        }

        if (_providerCatalog.HasUsableTemplates
            && _providerCatalog.Errors.Count == 0)
        {
            lblProviderWarning.Visible = false;
            lblProviderWarning.Text = string.Empty;
            return;
        }

        lblProviderWarning.Visible = true;

        if (_providerCatalog.HasUsableTemplates)
        {
            var ignoredTemplateCount =
                _providerCatalog.Errors
                    .Select(ExtractIgnoredTemplateFileName)
                    .Where(fileName => fileName is not null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

            lblProviderWarning.Text =
                ignoredTemplateCount == 1
                    ? "1 template ignored"
                    : $"{ignoredTemplateCount} templates ignored";

            _toolTip.SetToolTip(
                lblProviderWarning,
                string.Join(
                    Environment.NewLine,
                    _providerCatalog.Errors));
        }
        else
        {
            lblProviderWarning.Text =
                "No valid templates";

            _toolTip.SetToolTip(
                lblProviderWarning,
                "No valid Provider template is available. New assets cannot be started until a valid template exists in the provider_templates folder.");
        }
    }

    private static string? ExtractIgnoredTemplateFileName(
        string error)
    {
        const string templateMarker =
            "template '";

        var start =
            error.IndexOf(
                templateMarker,
                StringComparison.Ordinal);

        if (start < 0)
        {
            const string fileMarker =
                "File '";

            start =
                error.IndexOf(
                    fileMarker,
                    StringComparison.Ordinal);

            if (start < 0)
            {
                return null;
            }

            start += fileMarker.Length;
        }
        else
        {
            start += templateMarker.Length;
        }

        var end =
            error.IndexOf(
                "'",
                start,
                StringComparison.Ordinal);

        return end > start
            ? error[start..end]
            : null;
    }

    /// <summary>
    /// Binds the active recovered session's Provider snapshot into the dropdown
    /// (temporarily when the original template file is gone/changed), and
    /// re-asserts the Request association for queue-originated sessions.
    /// </summary>
    private void BindRecoveredSessionProvider()
    {
        if (_currentSession is null
            || _currentSession.ProviderTemplate is null)
        {
            return;
        }

        var snapshot = _currentSession.ProviderTemplate;

        var matching =
            _providerCatalog?.Templates.FirstOrDefault(
                template =>
                    string.Equals(
                        template.FileName,
                        snapshot.FileName,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        template.ContentSha256,
                        snapshot.ContentSha256,
                        StringComparison.OrdinalIgnoreCase));

        if (matching is not null)
        {
            _selectedProvider = matching;
            cmbProvider.SelectedItem = matching;
            return;
        }

        var temporary =
            ProviderTemplateDefinition.FromSnapshot(
                snapshot);

        if (_sessionSnapshotProviders.All(
                existing =>
                    !string.Equals(
                        existing.ContentSha256,
                        temporary.ContentSha256,
                        StringComparison.OrdinalIgnoreCase)))
        {
            _sessionSnapshotProviders.Add(temporary);
            cmbProvider.Items.Add(temporary);
        }

        _selectedProvider = temporary;
        cmbProvider.SelectedItem = temporary;
    }
}