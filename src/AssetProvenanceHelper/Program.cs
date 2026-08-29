using System.Threading;
using System.Windows.Forms;

namespace AssetProvenanceHelper;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            RunApplication();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Asset Provenance Helper could not start.\n\n"
                + ex.Message,
                "Startup error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void RunApplication()
    {
        ApplicationConfiguration.Initialize();

        var baseDirectory =
            AppContext.BaseDirectory;

        var mutexName =
            AppBootstrap.BuildSingleInstanceMutexName(
                baseDirectory);

        using var singleInstanceMutex =
            new Mutex(
                initiallyOwned: true,
                name: mutexName,
                createdNew: out bool acquiredMutex);

        if (!acquiredMutex)
        {
            MessageBox.Show(
                "Asset Provenance Helper is already running.\n\n"
                + "Only one instance may run at a time to protect the shared session record.",
                "Already Running",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return;
        }

        var stateDirectory = AppBootstrap.GetStateDirectory();
        Directory.CreateDirectory(stateDirectory);
        AppBootstrap.MigrateLegacyState(baseDirectory, stateDirectory);

        var context =
            AppBootstrap.CreateContext(
                baseDirectory,
                (msg, title) =>
                    MessageBox.Show(
                        msg,
                        title,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning));

        Application.Run(
            new MainForm(
                context.Settings,
                context.SettingsService,
                context.ImageFinderService,
                context.TemplateService,
                context.ValidationService,
                context.AssetProcessorService,
                context.SessionService,
                context.ProviderTemplateCatalogService,
                context.RecentDocumentHistoryService,
                context.RequestProgressService));
    }
}
