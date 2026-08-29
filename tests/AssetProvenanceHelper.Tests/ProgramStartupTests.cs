#nullable enable
using System.Windows.Forms;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Covers Program.Main/Run/RunApplication, previously excluded from coverage
/// wholesale. Each test installs the ApplicationConfigurationInitializer /
/// ApplicationRunProvider / MessageProvider seams and AppBootstrap's
/// MutexNameOverride so the real WinForms DPI initializer, the real message
/// loop, and the real systemwide single-instance mutex are never touched -
/// touching the real mutex would risk colliding with an actually-running
/// instance of the app on a developer's machine.
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

    [Fact]
    public void RunApplication_NormalStartup_ConstructsMainFormAndInvokesRunProvider()
    {
        RunOnSta(() =>
        {
            var mutexName = UniqueTestMutexName();
            Form? capturedForm = null;

            AppBootstrap.MutexNameOverride = () => mutexName;
            Program.ApplicationConfigurationInitializer = () => { };
            Program.ApplicationRunProvider = form =>
            {
                capturedForm = form;
            };

            try
            {
                Program.RunApplication();
            }
            finally
            {
                capturedForm?.Dispose();
                Program.ApplicationConfigurationInitializer = null;
                Program.ApplicationRunProvider = null;
                AppBootstrap.MutexNameOverride = null;
            }

            Assert.NotNull(capturedForm);
            Assert.IsType<MainForm>(capturedForm);
        });
    }

    [Fact]
    public void RunApplication_AlreadyRunning_ShowsMessageAndNeverConstructsUi()
    {
        RunOnSta(() =>
        {
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
            }

            Assert.False(runProviderCalled);
            Assert.Equal("Already Running", shownTitle);
            Assert.Equal(MessageBoxIcon.Information, shownIcon);
            Assert.Contains(
                "already running",
                shownMessage,
                StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Run_StartupException_ShowsStartupErrorMessageAndNeverConstructsUi()
    {
        RunOnSta(() =>
        {
            var mutexName = UniqueTestMutexName();
            string? shownMessage = null;
            string? shownTitle = null;
            MessageBoxIcon shownIcon = default;
            var runProviderCalled = false;

            AppBootstrap.MutexNameOverride = () => mutexName;
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
                Program.Run();
            }
            finally
            {
                Program.ApplicationConfigurationInitializer = null;
                Program.MessageProvider = null;
                Program.ApplicationRunProvider = null;
                AppBootstrap.MutexNameOverride = null;
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
            var mutexName = UniqueTestMutexName();
            var runProviderCalled = false;

            AppBootstrap.MutexNameOverride = () => mutexName;
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
                mainMethod!.Invoke(null, null);
            }
            finally
            {
                Program.ApplicationConfigurationInitializer = null;
                Program.ApplicationRunProvider = null;
                AppBootstrap.MutexNameOverride = null;
            }

            Assert.True(runProviderCalled);
        });
    }
}
