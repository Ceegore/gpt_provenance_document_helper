AI Asset Provenance Helper
==========================


FASTEST START (works on any Windows language, no dialogs)
---------------------------------------------------------

1. Extract this ZIP anywhere you can write, e.g. C:\Tools\AssetProvenanceHelper

2. Open PowerShell, paste this ONE line, and adjust the path if needed:

     Get-ChildItem "C:\Tools\AssetProvenanceHelper" -Recurse | Unblock-File

3. Double-click:  Start AI Asset Provenance Helper.cmd

Requires the free .NET 10 Desktop Runtime (x64), installed once:
  https://dotnet.microsoft.com/download/dotnet/10.0
Choose ".NET Desktop Runtime" - not the SDK, not the ASP.NET runtime.


DON'T WANT TO RUN ANY COMMAND? START THE APP DIRECTLY
-----------------------------------------------------

This always works, even on files still marked as downloaded:

  dotnet "C:\Tools\AssetProvenanceHelper\AssetProvenanceHelper.dll"

That is literally all the launcher does.


WHY STEP 2 IS NEEDED
--------------------

Windows tags every downloaded file as "came from the internet". Smart App
Control refuses to run .cmd files carrying that tag, no matter what is inside
them. You will see nothing at all, or:

  "Eine Anwendungssteuerungsrichtlinie hat diese Datei blockiert.
   Gefaehrliche Dateierweiterung aus dem Web."
  ("An application control policy has blocked this file.
    Dangerous file extension from the web.")

Unblock-File removes that tag. The app's .dll does NOT need this - only the
.cmd launcher does.


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
