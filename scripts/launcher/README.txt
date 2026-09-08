AI Asset Provenance Helper
==========================


INSTALL
-------

Either:

1. Install the prerequisite .NET 10 Desktop Runtime (x64), once:
     https://dotnet.microsoft.com/download/dotnet/10.0
   Choose ".NET Desktop Runtime", not the SDK and not the ASP.NET runtime.

2. Download AssetProvenanceHelper-v<version>.zip and extract it anywhere you
   can write, for example C:\Tools\AssetProvenanceHelper.

3. Run:

     dotnet "C:\Tools\AssetProvenanceHelper\AssetProvenanceHelper.dll"

Or:

1. Download AssetProvenanceHelper-v<version>.zip and extract it.

2. Run this one line in PowerShell (adjust the path if extracted elsewhere):

     Get-ChildItem "C:\Tools\AssetProvenanceHelper" -Recurse | Unblock-File

3. Double-click:  Start AI Asset Provenance Helper.cmd


WHY SO COMPLICATED?
-------------------

Windows tags every downloaded file as internet-sourced, and Smart App Control
refuses to run .cmd files carrying that tag - whatever is inside them. This app
is not code-signed. Smart App Control refuses to launch unsigned .exe files
outright - no error, no window. So the package contains no .exe; it runs the
app inside Microsoft's signed dotnet host instead. The application is identical either way.

If the .cmd launcher is still blocked, you may see:

  "Eine Anwendungssteuerungsrichtlinie hat diese Datei blockiert.
   Gefaehrliche Dateierweiterung aus dem Web."
  ("An application control policy has blocked this file.
    Dangerous file extension from the web.")

Unblock-File removes that tag. The app's .dll does NOT need this - only the
.cmd launcher does. The direct dotnet command above remains available even
before unblocking.


DOING IT THROUGH THE GUI INSTEAD (optional)
-------------------------------------------

Right-click the ZIP *before extracting* -> Properties -> at the bottom of the
first tab, next to "Security:" / "Sicherheit:", tick the checkbox -> OK.

  English Windows:  the checkbox is called  "Unblock"
  German Windows:   the checkbox is called  "Zulassen"

Note: the checkbox is only shown while the file still carries the tag. If you
do not see it, the file is already unblocked - or use the PowerShell line
above, which does not depend on your Windows language at all.


WHY THERE IS NO .EXE HERE
-------------------------

This app is a hobby project and is not code-signed.

Smart App Control refuses to launch unsigned .exe files outright - no error, no
window, nothing happens. So this package contains no .exe. It runs the app
inside Microsoft's own digitally signed "dotnet" host instead, so Windows
validates a Microsoft binary rather than an unsigned one.

The application itself is identical either way.

Do NOT turn off Smart App Control to work around this. On Windows 11 it can
only be re-enabled by reinstalling Windows.


WHERE YOUR DATA LIVES
---------------------

Settings and recovery state are stored per-user in:

  %LOCALAPPDATA%\Ceegore\AssetProvenanceHelper

Never inside this folder - so upgrading is just replacing this folder, and you
lose nothing.
