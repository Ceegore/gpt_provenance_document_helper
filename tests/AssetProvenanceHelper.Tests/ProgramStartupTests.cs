#nullable enable
using System.Windows.Forms;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Covers Program.Main/Run/RunApplication, previously excluded from coverage
/// wholesale. Every test installs ApplicationConfigurationInitializer /
/// ApplicationRunProvider / MessageProvider, Program.BaseDirectoryOverride, and
/// AppBootstrap's MutexNameOverride / StateDirectoryOverride, so the real
/// WinForms DPI initializer, the real message loop, the real systemwide
/// single-instance mutex, and - critically - the real per-user
/// %LOCALAPPDATA%\Ceegore\AssetProvenanceHelper state directory are never
/// touched. An earlier version of this file only seamed the mutex and left
/// state-directory resolution real, which meant running the test suite could
/// durably write the legacy-migration-complete marker under a real user's
/// LocalAppData without ever having imported anything - see the audit that
/// found this (docs/audits - "ProgramStartupTests modifies the real per-user
/// migration state"). Every test below must set both overrides.
/// </summary>
public class ProgramStartupTests
{
    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error != null)
        {
            throw new AggregateException(error);
        }
    }

    private static string UniqueTestMutexName() =>
        "Local\\AssetProvenanceHelperTest_" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Installs the mutex/base-directory/state-directory seams for the duration
    /// of <paramref name="action"/> and always clears them afterward, even on
    /// failure, so one test's overrides can never leak into another.
    ///
    /// Also installs no-op defaults for MainForm.MessageBoxProvider and
    /// Program.MessageProvider - two *separate* seams (one for MainForm's own
    /// dialogs such as template validation, one for Program's startup notices
    /// such as the settings-could-not-load warning). The isolated
    /// baseDirectory/stateDirectory these tests use routinely trip both real
    /// validation paths (no templates/ folder; deliberately non-JSON legacy
    /// settings content), and either one falling through to the real
    /// MessageBox.Show is a genuine modal dialog blocking whatever machine
    /// runs the suite. A caller that needs to capture Program.MessageProvider's
    /// arguments for its own assertions must set it BEFORE calling this
    /// method - the default here only applies when nothing is already
    /// installed, and only what this method installed is reset afterward.
    /// </summary>
    private static void WithProgramSeams(
        string baseDirectory,
        string stateDirectory,
        Action action)
    {
        var mutexName = UniqueTestMutexName();

        AppBootstrap.MutexNameOverride = () => mutexName;
        AppBootstrap.StateDirectoryOverride = () => stateDirectory;
        Program.BaseDirectoryOverride = () => baseDirectory;
        MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

        var installedDefaultMessageProvider = false;
        if (Program.MessageProvider is null)
        {
            Program.MessageProvider = (_, _, _) => { };
            installedDefaultMessageProvider = true;
        }

        try
        {
            action();
        }
        finally
        {
            AppBootstrap.MutexNameOverride = null;
            AppBootstrap.StateDirectoryOverride = null;
            Program.BaseDirectoryOverride = null;
            MainForm.MessageBoxProvider = null;

            if (installedDefaultMessageProvider)
            {
                Program.MessageProvider = null;
            }
        }
    }

    [Fact]
    public void RunApplication_NormalStartup_ConstructsMainFormAndInvokesRunProvider()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var baseDirectory = Path.Combine(workspace.Root, "base");
            var stateDirectory = Path.Combine(workspace.Root, "state");
            Directory.CreateDirectory(baseDirectory);

            Form? capturedForm = null;

            Program.ApplicationConfigurationInitializer = () => { };
            Program.ApplicationRunProvider = form => capturedForm = form;

            try
            {
                WithProgramSeams(
                    baseDirectory,
                    stateDirectory,
                    () => Program.RunApplication());
            }
            finally
            {
                capturedForm?.Dispose();
                Program.ApplicationConfigurationInitializer = null;
                Program.ApplicationRunProvider = null;
            }

            Assert.NotNull(capturedForm);
            Assert.IsType<MainForm>(capturedForm);
        });
    }

    [Fact]
    public void RunApplication_NormalStartup_CreatesStateDirectoryButNeverTouchesBaseDirectoryLegacyFiles()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var baseDirectory = Path.Combine(workspace.Root, "base");
            var stateDirectory = Path.Combine(workspace.Root, "state");
            Directory.CreateDirectory(baseDirectory);

            Assert.False(Directory.Exists(stateDirectory));

            Program.ApplicationConfigurationInitializer = () => { };
            Form? capturedForm = null;
            Program.ApplicationRunProvider = form => capturedForm = form;

            try
            {
                WithProgramSeams(
                    baseDirectory,
                    stateDirectory,
                    () => Program.RunApplication());
            }
            finally
            {
                capturedForm?.Dispose();
                Program.ApplicationConfigurationInitializer = null;
                Program.ApplicationRunProvider = null;
            }

            Assert.True(Directory.Exists(stateDirectory));
        });
    }

    [Fact]
    public void RunApplication_LegacyMigration_CopiesLegacyFilesIntoStateDirectory()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var baseDirectory = Path.Combine(workspace.Root, "base");
            var stateDirectory = Path.Combine(workspace.Root, "state");
            Directory.CreateDirectory(baseDirectory);

            var legacySettingsPath = Path.Combine(baseDirectory, AppConstants.SettingsFileName);
            var legacySessionPath = Path.Combine(baseDirectory, AppConstants.SessionFileName);
            File.WriteAllText(legacySettingsPath, "legacy settings");
            File.WriteAllText(legacySessionPath, "legacy pending session");

            Program.ApplicationConfigurationInitializer = () => { };
            Form? capturedForm = null;
            Program.ApplicationRunProvider = form => capturedForm = form;

            try
            {
                WithProgramSeams(
                    baseDirectory,
                    stateDirectory,
                    () => Program.RunApplication());
            }
            finally
            {
                capturedForm?.Dispose();
                Program.ApplicationConfigurationInitializer = null;
                Program.ApplicationRunProvider = null;
            }

            var stableSettingsPath = Path.Combine(stateDirectory, AppConstants.SettingsFileName);
            var stableSessionPath = Path.Combine(stateDirectory, AppConstants.SessionFileName);

            Assert.Equal("legacy settings", File.ReadAllText(stableSettingsPath));
            Assert.Equal("legacy pending session", File.ReadAllText(stableSessionPath));

            // The legacy source files are copied, never moved.
            Assert.True(File.Exists(legacySettingsPath));
            Assert.True(File.Exists(legacySessionPath));
        });
    }

    [Fact]
    public void RunApplication_LegacyMigration_ExistingStableStateWinsOverLegacyFiles()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var baseDirectory = Path.Combine(workspace.Root, "base");
            var stateDirectory = Path.Combine(workspace.Root, "state");
            Directory.CreateDirectory(baseDirectory);
            Directory.CreateDirectory(stateDirectory);

            File.WriteAllText(
                Path.Combine(baseDirectory, AppConstants.SettingsFileName),
                "legacy settings");
            File.WriteAllText(
                Path.Combine(stateDirectory, AppConstants.SettingsFileName),
                "already-migrated user settings");

            Program.ApplicationConfigurationInitializer = () => { };
            Form? capturedForm = null;
            Program.ApplicationRunProvider = form => capturedForm = form;

            try
            {
                WithProgramSeams(
                    baseDirectory,
                    stateDirectory,
                    () => Program.RunApplication());
            }
            finally
            {
                capturedForm?.Dispose();
                Program.ApplicationConfigurationInitializer = null;
                Program.ApplicationRunProvider = null;
            }

            Assert.Equal(
                "already-migrated user settings",
                File.ReadAllText(Path.Combine(stateDirectory, AppConstants.SettingsFileName)));
        });
    }

    [Fact]
    public void RunApplication_LegacyMigration_MarkerPreventsReimportOnSecondRun()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var baseDirectory = Path.Combine(workspace.Root, "base");
            var stateDirectory = Path.Combine(workspace.Root, "state");
            Directory.CreateDirectory(baseDirectory);

            var legacySettingsPath = Path.Combine(baseDirectory, AppConstants.SettingsFileName);
            File.WriteAllText(legacySettingsPath, "first legacy settings");

            void RunOnce()
            {
                Program.ApplicationConfigurationInitializer = () => { };
                Form? capturedForm = null;
                Program.ApplicationRunProvider = form => capturedForm = form;

                try
                {
                    WithProgramSeams(
                        baseDirectory,
                        stateDirectory,
                        () => Program.RunApplication());
                }
                finally
                {
                    capturedForm?.Dispose();
                    Program.ApplicationConfigurationInitializer = null;
                    Program.ApplicationRunProvider = null;
                }
            }

            RunOnce();

            var stableSettingsPath = Path.Combine(stateDirectory, AppConstants.SettingsFileName);
            Assert.Equal("first legacy settings", File.ReadAllText(stableSettingsPath));

            // Simulate the recovery journal being consumed and removed, then a
            // brand new legacy session appearing before the app is relaunched.
            // The durable marker must stop it from being (re-)imported.
            File.Delete(stableSettingsPath);
            File.WriteAllText(legacySettingsPath, "second legacy settings after marker written");

            RunOnce();

            Assert.False(
                File.Exists(stableSettingsPath),
                "The migration marker should have prevented a second import; " +
                "re-importing after the marker exists would resurrect stale legacy state.");
        });
    }

    [Fact]
    public void RunApplication_AlreadyRunning_ShowsMessageAndNeverConstructsUi()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var baseDirectory = Path.Combine(workspace.Root, "base");
            var stateDirectory = Path.Combine(workspace.Root, "state");
            Directory.CreateDirectory(baseDirectory);

            var mutexName = UniqueTestMutexName();

            using var externalMutex =
                new Mutex(
                    initiallyOwned: true,
                    name: mutexName,
                    createdNew: out var createdNew);

            Assert.True(createdNew);

            string? shownMessage = null;
            string? shownTitle = null;
            MessageBoxIcon shownIcon = default;
            var runProviderCalled = false;

            AppBootstrap.MutexNameOverride = () => mutexName;
            AppBootstrap.StateDirectoryOverride = () => stateDirectory;
            Program.BaseDirectoryOverride = () => baseDirectory;
            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
            Program.ApplicationConfigurationInitializer = () => { };
            Program.MessageProvider = (msg, title, icon) =>
            {
                shownMessage = msg;
                shownTitle = title;
                shownIcon = icon;
            };
            Program.ApplicationRunProvider = _ => runProviderCalled = true;

            try
            {
                Program.RunApplication();
            }
            finally
            {
                Program.ApplicationConfigurationInitializer = null;
                Program.MessageProvider = null;
                Program.ApplicationRunProvider = null;
                AppBootstrap.MutexNameOverride = null;
                AppBootstrap.StateDirectoryOverride = null;
                Program.BaseDirectoryOverride = null;
                MainForm.MessageBoxProvider = null;
            }

            Assert.False(runProviderCalled);
            Assert.Equal("Already Running", shownTitle);
            Assert.Equal(MessageBoxIcon.Information, shownIcon);
            Assert.Contains(
                "already running",
                shownMessage,
                StringComparison.OrdinalIgnoreCase);

            // The already-running branch returns before state-directory
            // resolution, so it must not have been created.
            Assert.False(Directory.Exists(stateDirectory));
        });
    }

    [Fact]
    public void Run_StartupException_ShowsStartupErrorMessageAndNeverConstructsUi()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var baseDirectory = Path.Combine(workspace.Root, "base");
            var stateDirectory = Path.Combine(workspace.Root, "state");
            Directory.CreateDirectory(baseDirectory);

            string? shownMessage = null;
            string? shownTitle = null;
            MessageBoxIcon shownIcon = default;
            var runProviderCalled = false;

            Program.ApplicationConfigurationInitializer =
                () => throw new InvalidOperationException("simulated startup failure");
            Program.MessageProvider = (msg, title, icon) =>
            {
                shownMessage = msg;
                shownTitle = title;
                shownIcon = icon;
            };
            Program.ApplicationRunProvider = _ => runProviderCalled = true;

            try
            {
                WithProgramSeams(
                    baseDirectory,
                    stateDirectory,
                    () => Program.Run());
            }
            finally
            {
                Program.ApplicationConfigurationInitializer = null;
                Program.MessageProvider = null;
                Program.ApplicationRunProvider = null;
            }

            Assert.False(runProviderCalled);
            Assert.Equal("Startup error", shownTitle);
            Assert.Equal(MessageBoxIcon.Error, shownIcon);
            Assert.Contains("simulated startup failure", shownMessage);
        });
    }

    [Fact]
    public void Main_DelegatesToRun()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var baseDirectory = Path.Combine(workspace.Root, "base");
            var stateDirectory = Path.Combine(workspace.Root, "state");
            Directory.CreateDirectory(baseDirectory);

            var runProviderCalled = false;

            Program.ApplicationConfigurationInitializer = () => { };
            Program.ApplicationRunProvider = form =>
            {
                runProviderCalled = true;
                form.Dispose();
            };

            var mainMethod =
                typeof(Program).GetMethod(
                    "Main",
                    System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Static);

            Assert.NotNull(mainMethod);

            try
            {
                WithProgramSeams(
                    baseDirectory,
                    stateDirectory,
                    () => mainMethod!.Invoke(null, null));
            }
            finally
            {
                Program.ApplicationConfigurationInitializer = null;
                Program.ApplicationRunProvider = null;
            }

            Assert.True(runProviderCalled);
        });
    }
}
