using System.Threading;
using System.Windows.Forms;

namespace AssetProvenanceHelper;

internal static class Program
{
    /// <summary>
    /// Test seam: replaces the WinForms DPI/visual-style initializer, which the
    /// runtime only permits calling once before the first window handle is
    /// created in a process. The real test host has already created windows by
    /// the time a test drives RunApplication(), so tests must no-op this.
    /// </summary>
    internal static Action? ApplicationConfigurationInitializer;

    /// <summary>
    /// Test seam: replaces the blocking <see cref="Application.Run(Form)"/> call
    /// so a test can observe the constructed <see cref="MainForm"/> without
    /// entering the real WinForms message loop.
    /// </summary>
    internal static Action<Form>? ApplicationRunProvider;

    /// <summary>Test seam: replaces MessageBox.Show for startup notices.</summary>
    internal static Action<string, string, MessageBoxIcon>? MessageProvider;

    /// <summary>
    /// Test seam: replaces AppContext.BaseDirectory as the legacy-migration
    /// source directory, so a test can prove real legacy files at a controlled
    /// path get migrated into a controlled state directory (see
    /// AppBootstrap.StateDirectoryOverride) without touching the test host's
    /// own build output directory.
    /// </summary>
    internal static Func<string>? BaseDirectoryOverride;

    [STAThread]
    private static void Main() => Run();

    internal static void Run()
    {
        try
        {
            RunApplication();
        }
        catch (Exception ex)
        {
            ShowMessage(
                "Asset Provenance Helper could not start.\n\n"
                + ex.Message,
                "Startup error",
                MessageBoxIcon.Error);
        }
    }

    internal static void RunApplication()
    {
        if (ApplicationConfigurationInitializer is not null)
        {
            ApplicationConfigurationInitializer();
        }
        else
        {
            InitializeApplicationConfigurationForReal();
        }

        var baseDirectory =
            BaseDirectoryOverride?.Invoke()
            ?? AppContext.BaseDirectory;

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
            ShowMessage(
                "Asset Provenance Helper is already running.\n\n"
                + "Only one instance may run at a time to protect the shared session record.",
                "Already Running",
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
                    ShowMessage(
                        msg,
                        title,
                        MessageBoxIcon.Warning));

        var form =
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
                context.RequestProgressService,
                context.ImageGenerationProvider,
                context.SecretStore,
                context.GenerationJobStore,
                null,
                context.RequestQueueStateService);

        if (ApplicationRunProvider is not null)
        {
            ApplicationRunProvider(form);
        }
        else
        {
            RunApplicationForReal(form);
        }
    }

    private static void ShowMessage(
        string message,
        string title,
        MessageBoxIcon icon)
    {
        if (MessageProvider is not null)
        {
            MessageProvider(message, title, icon);
            return;
        }

        ShowMessageBoxForReal(message, title, icon);
    }

    // The three methods below each wrap a single call that either only the
    // WinForms runtime is allowed to make once per process
    // (ApplicationConfiguration.Initialize), blocks in a real message loop
    // (Application.Run), or shows a real modal dialog (MessageBox.Show) - none
    // of which can run unattended. Tests exercise everything around them
    // through the *Provider seams above; these three are the real fallback
    // and are the enumerated, justified exceptions in
    // code-coverage-exclusions.json.

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    private static void InitializeApplicationConfigurationForReal() =>
        ApplicationConfiguration.Initialize();

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    private static void RunApplicationForReal(Form form) =>
        Application.Run(form);

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    private static void ShowMessageBoxForReal(
        string message,
        string title,
        MessageBoxIcon icon) =>
        MessageBox.Show(
            message,
            title,
            MessageBoxButtons.OK,
            icon);
}
