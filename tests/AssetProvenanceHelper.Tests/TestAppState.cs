using System.Runtime.CompilerServices;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Keeps the whole test assembly out of the real per-user state folder.
///
/// A MainForm constructed without the optional state services falls back to
/// <see cref="AppBootstrap.GetStateDirectory"/>, which resolves to the
/// operator's live %LOCALAPPDATA%\Ceegore\AssetProvenanceHelper. Under test that
/// meant the suite read the real Pixel-Exact journal, ran generation-job and
/// candidate recovery against the real store, and could discard a pending batch.
/// It also made results depend on machine state: a live pending batch once made
/// a queue test hang for 30 seconds on a real modal confirmation dialog.
///
/// The module initializer installs the redirect before any test runs. The suite
/// is single-threaded by assembly policy, so a mutable current directory is safe
/// and lets each <see cref="TestWorkspace"/> own its own state folder.
/// </summary>
internal static class TestAppState
{
    // Deliberately short. Staged API candidates nest
    // <root>/generated/<64-hex fingerprint>/<64-hex request key>/<id>.png, and
    // GDI+ still fails to decode a PNG past MAX_PATH even where the .NET file
    // APIs succeed. A workspace-shaped root pushed that path to 279 characters
    // and broke candidate verification with a bogus "invalid input" error.
    private static readonly string ProcessRoot =
        Path.Combine(
            Path.GetTempPath(),
            "aph" + Guid.NewGuid().ToString("N")[..8]);

    private static string _current = ProcessRoot;
    private static int _scopeCounter;

    /// <summary>
    /// The directory AppBootstrap resolves to right now. Falls back to the
    /// per-process root once a workspace that owned it has been deleted, so an
    /// unbalanced restore can never expose the real user folder.
    /// </summary>
    internal static string Current
    {
        get => Directory.Exists(_current) ? _current : ProcessRoot;
        set => _current = value;
    }

    /// <summary>The assembly-wide override. Restore this, never null.</summary>
    internal static Func<string> Default { get; } = () => Current;

    [ModuleInitializer]
    internal static void Install()
    {
        Directory.CreateDirectory(ProcessRoot);
        AppBootstrap.StateDirectoryOverride = Default;

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (Directory.Exists(ProcessRoot))
                {
                    Directory.Delete(ProcessRoot, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leaked temp directory is harmless; failing process exit is not.
            }
            catch (UnauthorizedAccessException)
            {
            }
        };
    }

    /// <summary>
    /// A fresh, short state folder for one <see cref="TestWorkspace"/>. It lives
    /// beside the workspace rather than inside it purely to keep staged
    /// candidate paths under MAX_PATH.
    /// </summary>
    internal static string CreateScopedStateDirectory()
    {
        var scope = Path.Combine(
            ProcessRoot,
            Interlocked.Increment(ref _scopeCounter).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(scope);
        return scope;
    }

    /// <summary>
    /// Undoes a locally installed override. Tests must call this instead of
    /// assigning null, which would hand the rest of the run the real folder.
    /// </summary>
    internal static void RestoreDefault()
    {
        _current = ProcessRoot;
        AppBootstrap.StateDirectoryOverride = Default;
    }
}
