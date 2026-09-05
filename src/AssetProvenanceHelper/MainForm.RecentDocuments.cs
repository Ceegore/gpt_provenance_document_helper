#nullable enable
using System.Drawing;
using System.Windows.Forms;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private void LoadRecentDocumentsIntoUi()
    {
        if (_recentDocumentHistoryService is null)
        {
            return;
        }

        try
        {
            RefreshRecentDocumentsUi(
                _recentDocumentHistoryService.Load());
        }
        catch
        {
            // Bookkeeping state; never block startup.
        }
    }

    private void RecordRecentDocument(
        ProvenanceDocumentKind kind,
        string path,
        string assetName,
        DateTimeOffset recordedAt)
    {
        if (_recentDocumentHistoryService is null)
        {
            return;
        }

        try
        {
            _recentDocumentHistoryService.Record(
                new RecentDocumentEntry
                {
                    Path = path,
                    AssetName = assetName,
                    Kind = kind,
                    RecordedAt = recordedAt
                });

            RefreshRecentDocumentsUi(
                _recentDocumentHistoryService.Load());
        }
        catch
        {
            // Never roll back a committed asset because bookkeeping failed.
        }
    }

    private void RemoveRecentDocumentsForCancelledSession(
        AssetSession session)
    {
        if (_recentDocumentHistoryService is null)
        {
            return;
        }

        try
        {
            _recentDocumentHistoryService.RemoveEntriesUnderAssetFolder(
                session.AssetFolder);

            RefreshRecentDocumentsUi(
                _recentDocumentHistoryService.Load());
        }
        catch
        {
            // Bookkeeping state; cancellation remains durable.
        }
    }

    private ListViewItem? _lastHoveredRecentDocItem;

    private void RefreshRecentDocumentsUi(
        IReadOnlyList<RecentDocumentEntry>? entries = null)
    {
        if (lvRecentDocuments.IsDisposed)
        {
            return;
        }

        _lastHoveredRecentDocItem = null;
        _toolTip.SetToolTip(lvRecentDocuments, null);

        lvRecentDocuments.BeginUpdate();

        try
        {
            lvRecentDocuments.Items.Clear();

            var source =
                entries
                ?? (_recentDocumentHistoryService is null
                    ? Array.Empty<RecentDocumentEntry>()
                    : LoadRecentDocumentsSafe());

            foreach (var entry in source)
            {
                var type =
                    entry.Kind == ProvenanceDocumentKind.Reference
                        ? "Reference"
                        : "Final";

                var item =
                    new ListViewItem(
                        new[]
                        {
                            entry.RecordedAt.ToString("HH:mm:ss"),
                            type,
                            entry.AssetName,
                            Path.GetFileName(entry.Path)
                        })
                    {
                        Tag = entry.Path
                    };

                lvRecentDocuments.Items.Add(item);
            }
        }
        finally
        {
            lvRecentDocuments.EndUpdate();
        }
    }

    private IReadOnlyList<RecentDocumentEntry> LoadRecentDocumentsSafe()
    {
        try
        {
            return _recentDocumentHistoryService!.Load();
        }
        catch
        {
            return Array.Empty<RecentDocumentEntry>();
        }
    }

    private void UpdateRecentDocumentTooltip(MouseEventArgs e)
    {
        var item =
            lvRecentDocuments.GetItemAt(
                e.X,
                e.Y);

        if (item == _lastHoveredRecentDocItem)
        {
            return;
        }

        _lastHoveredRecentDocItem = item;

        var fullPath =
            item?.Tag as string;

        if (!string.IsNullOrWhiteSpace(fullPath))
        {
            _toolTip.SetToolTip(
                lvRecentDocuments,
                fullPath);
        }
        else
        {
            _toolTip.SetToolTip(
                lvRecentDocuments,
                null);
        }
    }
}