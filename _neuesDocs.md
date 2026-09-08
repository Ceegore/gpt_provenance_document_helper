# Plan zur dauerhaften Release Dokumentation

## Aktueller Stand

Am 6. September 2026 wurde die Datei THIRD_PARTY_AI_WORKFLOW_GUIDE.md direkt
in den realen Installationsordner
C:\Tools\AssetProvenanceHelper-v1.5.2\examples gelegt. Sie gehört damit zur
lokal entpackten Version 1.5.2, ist jedoch bewusst noch nicht Teil des
Repository-Quellbaums und daher noch nicht in einem zukünftigen GitHub-Release.

Diese Planung beschreibt die spätere Produktänderung. Sie erzeugt jetzt weder
eine neue Release-Version noch einen Git-Commit.

## Bereits umgesetzt: Smart App Control Installationshinweis

Die komplette Installations- und Entsperrerklärung ist nun zusätzlich in der
GitHub-README und in scripts/launcher/README.txt verankert. Letztere Datei wird
bei jedem Release als README.txt in den Paketstamm kopiert. Der neue Check
scripts/verify_release_readme.ps1 prüft im tatsächlich vorbereiteten
Release-Ordner die .NET-10-Runtime-Anforderung, den direkten dotnet-Start, den
Unblock-File-Befehl, den Launcher, die Smart-App-Control-Erklärung, die
fehlende Codesignatur, den apphost-freien Paketinhalt und die Aussage zur
identischen Anwendung. .github/workflows/release.yml führt diesen Check vor dem
Archivieren aus und lässt einen unvollständigen Release fehlschlagen.

## Ziel

Jeder künftige Download aus dem GitHub-Release soll im Verzeichnis examples
dieselbe versionierte Anleitung enthalten. Eine externe KI soll mit dem
entpackten Release, den Beispieldateien und einem Asset-Request-Dokument einen
vollständig importierbaren Manifest-Entwurf erstellen können, ohne die
Pixel-Exact-Steuerung aus JSON-Beispielen erraten zu müssen.

## Umsetzungsschritte

1. Dokument als Release-Quelle übernehmen

   Die geprüfte Datei nach
   src/AssetProvenanceHelper/examples/THIRD_PARTY_AI_WORKFLOW_GUIDE.md
   übernehmen. Sie ist dort neben den bestehenden Dateien
   asset_request_manifest_template.json,
   pixel_exact_manifest_template.json und
   asset_request_conversion_prompt.txt die maßgebliche Quelle.

2. Konsistenz zur Anwendung prüfen

   Vor jedem Dokument-Update müssen mindestens diese Implementierungsstellen
   gegen die Textaussagen geprüft werden:

   - Services/AssetRequestManifestService.cs für die strikte JSON-Schema-,
     Dateinamen-, Alpha- und Auflösungsprüfung;
   - Services/QueuePromptWorkflowParser.cs für die exakte FLOWMETA- und
     PROZESSMARKER-Grammatik;
   - MainForm.QueueWorkflowDetection.cs und MainForm.PixelExact.cs für die
     automatisch gesetzten UI-Modi, Phasenreihenfolge, Bestätigungsdialog und
     Wiederaufnahme;
   - MainForm.ApiGeneration.cs und Services/ApiPreflightService.cs für die
     API-Grenzen;
   - MainForm.MainWorkflow.cs, MainForm.RequestQueue.cs und AppBootstrap.cs für
     Collect, Reset, Queue-Persistenz und State-Pfade;
   - templates sowie provider_templates für die Erklärung ihrer getrennten
     Aufgaben.

3. Installations- und Releasekopie absichern

   Die vorhandene Projektregel
   <None Update="examples\**\*"> in
   src/AssetProvenanceHelper/AssetProvenanceHelper.csproj kopiert alle Dateien
   unter examples bereits in Build- und Publish-Ausgaben. Sie soll bestehen
   bleiben. Nach Hinzufügen der Quelldatei muss daher keine parallele
   Copy-Sonderlogik implementiert werden.

4. Automatischen Pakettest ergänzen

   Einen Test zur Publish- oder Release-Struktur ergänzen, der nachweist, dass
   das erzeugte Paket genau diese Datei unter
   examples/THIRD_PARTY_AI_WORKFLOW_GUIDE.md enthält. Der Test soll zusätzlich
   prüfen, dass die Datei nicht leer ist und die zentralen eindeutigen Abschnitte
   enthält: JSON-Schema, manueller Browser-Flow, Pixel-Exact-Flow und API-Grenze.
   Er soll nicht fragil auf vollständigen Fließtextvergleich beruhen.

5. Verweise in Produktdokumentation ergänzen

   README und Help-Overlay um einen kurzen Verweis auf die Anleitung im
   Installationsordner ergänzen. Die Help-Oberfläche bleibt dabei eine kompakte
   Bedienhilfe; die neue Datei ist das ausführliche Übergabehandbuch für externe
   KIs und darf nicht als riesiger Textblock in das Dialogfenster kopiert werden.

6. Beispiele und Dokumentation gemeinsam validieren

   - asset_request_manifest_template.json mit dem dokumentierten V2-Schema
     importieren;
   - pixel_exact_manifest_template.json mit dem dokumentierten kanonischen
     Ablauf importieren;
   - prüfen, dass keine Dokumentation die Platzhalter-AusRefN-Zeilen als
     separate externe Bildgenerierung beschreibt;
   - prüfen, dass alle Count-, Index- und Serienregeln mit dem Parser
     übereinstimmen.

7. Build, Paketprüfung und Release erst nach ausdrücklichem Auftrag

   Nach einer späteren inhaltlichen Änderung die SAC-sicheren Testvorgaben aus
   AGENTS.md befolgen, Release und Paket strukturell prüfen und erst dann
   versionieren, committen, pushen, taggen und veröffentlichen. Der aktuelle
   Auftrag enthält ausdrücklich keinen solchen Release-Schritt.

## Abnahmekriterien für die spätere Änderung

- Die Anleitung ist im Repository unter src/AssetProvenanceHelper/examples.
- Ein Framework-dependent Publish und das Release-ZIP enthalten sie unter
  examples.
- Die installierte Anleitung, die Quelldatei und die Beispiel-JSONs sind
  fachlich konsistent.
- Der Pakettest schützt gegen ein versehentliches Weglassen in künftigen
  Releases.
- Es gibt keinen API-Schlüssel oder sonstiges Geheimnis in Dokumentation,
  Beispielen oder Manifesten.
