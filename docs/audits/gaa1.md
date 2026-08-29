# gaa1.md
# ZERO-EXCEPTION, 100% FUNCTIONAL + CODE-COVERAGE TEST AUDIT
**Repository:** `Ceegore/gpt_provenance_document_helper`
**Target branch:** `fix/bugrun1-issues`
**Target branch HEAD:** `6f20c4f3220ede5d19dafab056f3234109b242da`
**PR:** #2
**GitHub-Actions-Run:** `33225089350`
**Audit date:** 2026-08-29
---
# 0. Executive verdict
## FAIL — ZERO-EXCEPTION TEST STANDARD NOT SATISFIED
Ein PASS ist für den geprüften Stand objektiv nicht zulässig.
Die zentralen Blocker sind:
1. Der aktuelle Windows-CI-Lauf ist rot: **991/993 Tests bestanden, 2 fehlgeschlagen**.
2. Die gemessene Cobertura-Coverage beträgt nur **90,60 % Lines** und **85,06 % Branches**.
3. Bereits innerhalb des gemessenen Produktionscodes sind **4 Methoden vollständig unbetreten**.
4. `Program.cs` ist als gesamte Produktionsklasse mit `[ExcludeFromCodeCoverage]` ausgeschlossen.
5. `MainForm.Designer.cs` schließt `Dispose(bool)` und `InitializeComponent()` ebenfalls explizit aus.
6. Das CI-Coverage-Gate verlangt nur 90 % Lines und 85 % Branches, rundet Prozentwerte, prüft lediglich eine kleine hart codierte Dateimenge und prüft keinerlei Function-/Method-Coverage.
7. Mutation Testing deckt nicht den kompletten Produktionscode ab und erlaubt mit `break: 80` einen erheblichen Survivor-Anteil.
8. Der aktuelle CI-Ablauf bricht nach dem ersten Testfehler ab und führt dadurch wesentliche Teile des verlangten Audits gar nicht mehr aus.
9. Der vorhandene PR-Lauf testet GitHubs Merge-Commit `ca94f320...`; ein gesonderter sauber protokollierter Direktlauf auf exakt `6f20c4f...` inklusive `git status` fehlt.
10. Somit fehlen auch der finale RecoveryCritical-Nachweis, 20×-Gesamtlauf, 100×-Flake-Wiederholungen, aktueller Package-/Smoke-Nachweis und Zero-Survivor-Mutation-Nachweis.
Es wurde bei diesem Durchlauf **kein neuer eindeutig nachgewiesener Produktfehler mit Datenverlustwirkung** gefunden. Die neu bestätigten Fehler betreffen primär Test- und QA-Infrastruktur. Für den vorgegebenen Abnahmestandard genügt das dennoch zwingend für FAIL.
---
# A. Tested revision
* Branch: `fix/bugrun1-issues`
* Branch HEAD: `6f20c4f3220ede5d19dafab056f3234109b242da`
* Tree: `ef9b886f8685f0fd9e383759540042d9c9379b20`
* PR: `#2`
* PR base: `main`
* Base SHA: `adf63e60a12190e8b5afbaecc737e6c39be3b4aa`
* PR merge commit: `ca94f32067e0f844155b4ab014b7136bcd078dbe`
* GitHub Actions run: `33225089350`
* .NET SDK: `10.0.301`
* `global.json`: SDK `10.0.301`, `rollForward: disable`
* CI OS: Windows Server 2025 / `10.0.26100`
* SAC status des GitHub-Runners: nicht ausgewiesen
* `git status`: im Workflow nicht explizit protokolliert
Der Branch-HEAD wurde unmittelbar vor der Berichtsausgabe nochmals geprüft und war weiterhin `6f20c4f...`.
Wichtig: Der Actions-Lauf gehört zum Branch-HEAD `6f20c4f...`, aber ein `pull_request`-Workflow checkt GitHubs synthetischen Merge-Commit aus. Somit liegt sehr starke Evidenz für den geplanten Merge-Inhalt vor, aber **kein buchstabengetreuer Direktlauf des Branch-Commits**, wie es der Zero-Exception-Prompt für die finale Abnahme verlangt.
---
# B. Repository inventory
## B.1 Produktionsquellen
Unter `src/AssetProvenanceHelper/` befinden sich **55 eingecheckte Produktions-`.cs`-Dateien**.
Der aktuelle Cobertura-Bericht enthält:
* 52 eingecheckte Produktionsdateien;
* zusätzlich eine generierte Datei unter `obj/.../ApplicationConfiguration.g.cs`.
Drei eingecheckte Produktionsdateien fehlen im Report:
### `Program.cs`
Enthält ausführbaren Startup-Code, ist jedoch als ganze Klasse mit `[ExcludeFromCodeCoverage]` markiert.
Ausgeschlossen sind dadurch insbesondere:
* `Main()`
* `RunApplication()`
* Startup-Exception-Pfad
* Single-instance-Mutex-Pfad
* State-directory-Erstellung
* Migration
* Bootstrap
* `Application.Run(...)`
Das ist nach dem Abnahmeprompt ausdrücklich verboten.
### `MainForm.Designer.cs`
Die Datei enthält ausführbaren Produktionscode. Explizit ausgeschlossen sind:
* `Dispose(bool)`
* `InitializeComponent()`
Auch dies ist durch den Prompt ausdrücklich verboten.
### `Models/ImageSlot.cs`
Diese Datei enthält ausschließlich das Enum `ImageSlot` und besitzt keine ausführbaren Sequence Points. Das Fehlen aus einer Sequence-Point-Coverage kann deshalb legitim erklärt werden; sie muss aber trotzdem Bestandteil des vollständigen Source-Inventars bleiben.
## B.2 Methoden
Cobertura weist für die tatsächlich instrumentierten eingecheckten Produktionsdateien aus:
```text
Covered methods: 447
Total represented methods: 451
Represented method coverage: 99.11%
```
Dazu kommen mindestens die vier absichtlich ausgeblendeten ausführbaren Methoden aus `Program.cs` und `MainForm.Designer.cs`.
Damit ist bereits bewiesen:
```text
Function/method coverage != 100%
```
Der aktuelle Coverage-Report kann wegen seiner eigenen Ausschlüsse **keinen vertrauenswürdigen vollständigen Produktionsnenner liefern**.
## B.3 Erkannte Subsysteme
Erfasst wurden unter anderem:
* Startup / Bootstrap / Single Instance
* Settings
* Provider Templates
* Provider Snapshots
* Provider Rendering
* Reference-assisted Workflow
* No-Reference Workflow
* Direct Mode
* Image Discovery
* Reference/Main Image Slots
* Drag & Drop / File Selection
* Preview-Lifecycle
* Asset Naming
* Path Safety
* SHA-256 / Ownership Checks
* File Copy / Promotion
* Reference Transaction
* Main Transaction
* Journals
* Rollback
* Cancellation
* Recovery
* Recent Documents
* Request Queue
* Request Manifest
* Request Progress
* Prompt Preview Overlay
* Help Overlay
* Dialogs
* Packaging
* Smoke Testing
---
# C. Test results
## C.1 Aktueller CI-Lauf
| Check                         | Ergebnis                                                 |
| ----------------------------- | -------------------------------------------------------- |
| Restore                       | PASS                                                     |
| Debug Build `-warnaserror`    | PASS                                                     |
| Debug Tests                   | **FAIL — 991 passed / 2 failed / 0 skipped / 993 total** |
| Release Build im Coverage-Job | PASS                                                     |
| Release Tests mit Coverage    | **FAIL — 991 passed / 2 failed / 993 total**             |
| Main-Job Release Build        | SKIPPED                                                  |
| Main-Job Release Tests        | SKIPPED                                                  |
| RecoveryCritical              | SKIPPED                                                  |
| 20× Flakiness                 | SKIPPED                                                  |
| Publish                       | SKIPPED                                                  |
| Package/Startup Smoke         | SKIPPED                                                  |
| Mutation Testing              | für diesen Stand nicht nachgewiesen                      |
Damit widerspricht die unabhängige Windows-CI-Evidenz der Commit-Beschreibung, die `993/993` behauptet. Die Commit-Beschreibung ist entsprechend der Vorgabe nicht als Testbeweis zu behandeln.
## C.2 Fehlgeschlagene Tests
### 1. `RenderReferenceMapsAllNineValues`
Fehler:
```text
Assert.Contains() Failure: Sub-string not found
Actual contains:
"Provider: ChatGPT\r\nDate: 2026-08-26..."
Expected substring:
"Prompt:\nnot recorded"
```
### 2. `RenderFinalRefAssistedMapsPromptAndReference`
Fehler:
```text
Assert.Contains() Failure: Sub-string not found
Actual contains:
"Provider: ChatGPT\r\nDate: 2026-08-27..."
Expected substring:
"Prompt:\nexact prompt"
```
Die Tests schreiben explizit `\n` in ihre Erwartungen.
`ProviderTemplateRenderer.Render()` ersetzt dagegen ausschließlich Tags innerhalb von `snapshot.Content`; eine Zeilenenden-Konvertierung findet dort nicht statt.
Daraus folgt:
**Dies ist ein Testportabilitätsfehler und kein belegter Fehler der Provider-Template-Ersetzung.**
---
# D. Coverage
## D.1 Cobertura Root Counters
Der aktuelle Release-Coverage-Lauf erzeugte:
```text
Lines:    7869 / 8685 = 90.60%
Branches: 2432 / 2859 = 85.06%
```
Der Nenner enthält zusätzlich vier generierte Lines aus:
```text
obj\Release\net10.0-windows\
System.Windows.Forms.Analyzers.CSharp\
System.Windows.Forms.CSharp.Generators.ApplicationConfiguration\
ApplicationConfigurationGenerator\
ApplicationConfiguration.g.cs
```
Entfernt man ausschließlich diese generierte `obj`-Datei, ergeben die tatsächlich im Report vertretenen eingecheckten Produktionsdateien:
```text
Lines:   7869 / 8681 = 90.65%
Methods:  447 / 451  = 99.11%
```
Diese Werte sind **noch immer zu günstig**, weil `Program.cs` und ausführbare Designer-Methoden bereits vor der Messung ausgeschlossen wurden.
Somit ist bewiesen:
```text
True all-production line coverage   < 100%
True all-production branch coverage < 100%
True all-production method coverage < 100%
```
## D.2 Besonders große File-Gaps
| Datei                                | Line Coverage | Branch Coverage | Method Coverage |
| ------------------------------------ | ------------: | --------------: | --------------: |
| `MainForm.MainWorkflow.cs`           |        73.60% |          65.48% |            100% |
| `MainForm.ImageSelection.cs`         |        75.86% |          76.09% |            100% |
| `MainForm.Recovery.cs`               |        78.40% |          86.09% |            100% |
| `MainForm.ReferenceWorkflow.cs`      |        79.46% |          76.39% |            100% |
| `AssetProcessorService.FileOps.cs`   |        84.26% |          82.05% |          88.89% |
| `AssetProcessorService.Main.cs`      |        88.52% |          83.02% |            100% |
| `AssetProcessorService.Reference.cs` |        89.52% |          80.93% |            100% |
| `SessionService.cs`                  |        89.54% |          81.64% |            100% |
| `ValidationService.Session.cs`       |        89.59% |          90.79% |            100% |
| `MainForm.RecentDocuments.cs`        |        90.12% |          75.00% |          83.33% |
| `ProviderTemplateRenderer.cs`        |        96.67% |          75.00% |            100% |
| `HelpOverlayControl.cs`              |        98.48% |          25.00% |            100% |
| `PromptPreviewOverlayControl.cs`     |        95.52% |          62.50% |            100% |
Der vollständige Per-File-Report aller 55 Produktionsdateien befindet sich ohne Kürzung in der beiliegenden `gaa1.md`.
## D.3 Vollständig unbetretene dargestellte Methoden
```text
MainForm.RecentDocuments.cs
→ LoadRecentDocumentsSafe()
Services\AssetProcessorService.FileOps.cs
→ TryDeleteFile(string)
Services\ProviderTemplateCatalogService.cs
→ get_TemplateDirectory()
Services\RecentDocumentHistoryService.cs
→ get_FilePath()
```
Zusätzlich fehlen wegen der verbotenen Ausschlüsse mindestens:
```text
Program.Main()
Program.RunApplication()
MainForm.Dispose(bool)
MainForm.InitializeComponent()
```
Damit gilt mindestens:
```text
UNCOVERED_FUNCTIONS = NONEMPTY
```
## D.4 Uncovered Lines
Innerhalb der gemessenen Produktion existieren **812 nicht ausgeführte Lines**.
Besonders große Gruppen liegen in:
```text
MainForm.MainWorkflow.cs
MainForm.Recovery.cs
MainForm.ReferenceWorkflow.cs
AssetProcessorService.FileOps.cs
AssetProcessorService.Main.cs
AssetProcessorService.Reference.cs
SessionService.cs
ValidationService.Session.cs
```
Die vollständigen Source-Line-Ranges sind in der erzeugten `gaa1.md` enthalten.
## D.5 Uncovered Branches
Root-Cobertura:
```text
Covered branches: 2432
Total branches:   2859
Uncovered:         427
```
Hinzu kommen Branches aus den absichtlich ausgeschlossenen Produktionspfaden, die der Report überhaupt nicht zählt.
Somit:
```text
UNCOVERED_LINES = NONEMPTY
UNCOVERED_BRANCHES = NONEMPTY
UNCOVERED_FUNCTIONS = NONEMPTY
```
Die drei vom Prompt verlangten leeren Acceptance-Listen können nicht erzeugt werden.
---
# E. Functional traceability
| Subsystem                  | Evidenz                        | Status        |
| --------------------------- | ------------------------------ | ------------- |
| Startup / Program          | `Program.cs` ausgeschlossen    | **FAIL**      |
| Bootstrap                  | 98.35% Lines                   | **FAIL**      |
| Reference Workflow         | 79.46% / 76.39%                | **FAIL**      |
| No-Reference/Main Workflow | 73.60% / 65.48%                | **FAIL**      |
| Direct Mode                | 88.61% / 97.06%                | **FAIL**      |
| Image Selection            | 75.86% / 76.09%                | **FAIL**      |
| Recovery UI                | 78.40% / 86.09%                | **FAIL**      |
| Session Service            | 89.54% / 81.64%                | **FAIL**      |
| Session Validation         | 89.59% / 90.79%                | **FAIL**      |
| File Operations            | 84.26% / 82.05%, Methode fehlt | **FAIL**      |
| Main Processor             | 88.52% / 83.02%                | **FAIL**      |
| Reference Processor        | 89.52% / 80.93%                | **FAIL**      |
| Asset Naming               | 94.74% / 87.50%                | **FAIL**      |
| Image Discovery            | 95.35% / 83.33%                | **FAIL**      |
| Provider Rules             | 100% / 100%                    | Coverage-PASS |
| Provider Renderer          | 96.67% / 75.00%                | **FAIL**      |
| Provider Catalog           | 90.70% / 91.18%, Methode fehlt | **FAIL**      |
| Prompt Preview             | 96.77% / 77.50%                | **FAIL**      |
| Recent Documents           | mehrere Coverage-Gaps          | **FAIL**      |
| Settings                   | 98.44% / 86.67%                | **FAIL**      |
| Request Queue              | 97.45% / 83.90%                | **FAIL**      |
| Request Manifest           | 99.22% / 96.43%                | **FAIL**      |
| `TwoChoiceDialog`          | 100% / 100% / 100%             | Coverage-PASS |
| Help Overlay               | 98.48% / 25%                   | **FAIL**      |
| Prompt Overlay Control     | 95.52% / 62.50%                | **FAIL**      |
| Designer lifecycle         | ausgeschlossen                 | **FAIL**      |
| Package / Smoke            | aktueller Run übersprungen     | **UNPROVEN**  |
| Mutation Quality           | vollständig nicht nachgewiesen | **UNPROVEN**  |
Ein Coverage-PASS einzelner Komponenten beweist für sich allein noch keine ausreichende Assertion- oder Mutation-Qualität.
---
# F. Mutation testing
Die aktuelle Mutation-Konfiguration führt `dotnet stryker` aus, jedoch nur über einen begrenzten Produktionsausschnitt.
Der konfigurierte Mutationsumfang umfasst:
```text
Services/*.cs
Models/*.cs
MainForm.cs
MainForm.MainWorkflow.cs
MainForm.ReferenceWorkflow.cs
MainForm.Recovery.cs
```
und schließt ausdrücklich aus:
```text
!**/MainForm.Designer.cs
```
Außerdem gilt:
```text
break: 80
```
Damit fehlen unter anderem wesentliche Produktionsflächen wie:
```text
MainForm.DirectMode.cs
MainForm.ImageSelection.cs
MainForm.Layout.cs
MainForm.PromptPreview.cs
MainForm.ProviderTemplates.cs
MainForm.RecentDocuments.cs
MainForm.RequestQueue.cs
MainForm.ValidationUi.cs
Program.cs
UI controls
Dialogs
```
Die konkrete Konfiguration ist im Repository nachweisbar.
Für diesen Target-Stand ist daher nicht bewiesen:
```text
Meaningful surviving mutants: 0
```
Mutation Acceptance:
**FAIL / UNPROVEN**
---
# G. Defects and solutions
## B2-001 — HIGH — Provider-Renderer-Tests sind von Windows-Zeilenenden abhängig
### Area
Tests / Provider Template Rendering
### Summary
Zwei neue Regressionstests erwarten hart codiertes LF, während der Windows-Checkout CRLF in das Raw String Literal übernimmt.
### Reproduction
Aktueller Windows-CI-Lauf:
```text
RenderReferenceMapsAllNineValues                 FAIL
RenderFinalRefAssistedMapsPromptAndReference     FAIL
```
### Expected
Die semantische Prüfung der neun Templatewerte darf nicht davon abhängen, ob die Testquelldatei mit LF oder CRLF ausgecheckt wurde.
### Actual
```text
Actual:   Prompt:\r\nexact prompt
Expected: Prompt:\nexact prompt
```
### Why this is a defect
Der Test prüft zwei unabhängige Eigenschaften gleichzeitig:
1. semantisches Mapping;
2. Quelltext-Zeilenenden.
Dadurch scheitert ein korrektes semantisches Mapping auf Windows.
### Lösungsoption
Nur für die **semantischen Mapping-Tests** normalisieren:
```csharp
var normalized = result.Replace("\r\n", "\n");
Assert.Contains(
    $"Prompt:\n{AppConstants.NotRecordedValue}",
    normalized);
```
und entsprechend:
```csharp
Assert.Contains(
    "Prompt:\nexact prompt",
    normalized);
```
Zusätzlich separate Tests:
```text
Render_PreservesLfTemplateLineEndings
Render_PreservesCrLfTemplateLineEndings
```
Diese erhalten jeweils explizit erzeugte Inputstrings und prüfen die gewünschte Preservation-Eigenschaft.
### Konsistenzhinweis
**Nicht** `ProviderTemplateRenderer` auf LF umstellen, nur damit die aktuellen Tests grün werden. Der Renderer ist derzeit line-ending-preserving; eine Produktionsänderung wäre nur gerechtfertigt, wenn kanonisches LF tatsächlich Produktspezifikation wird.
---
## B2-002 — HIGH — verbotene Produktions-Coverage-Ausschlüsse
### Area
Coverage denominator / Startup / WinForms lifecycle
### Summary
`Program.cs` enthält:
```csharp
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class Program
```
`MainForm.Designer.cs` enthält `[ExcludeFromCodeCoverage]` auf:
```text
Dispose(bool)
InitializeComponent()
```
Der Acceptance-Prompt verbietet genau diese Vorgehensweise.
### Impact
Unter anderem bleiben unbewiesen:
* Startup
* Startup-Exception
* Single-instance-Verhalten
* Bootstrap-Verknüpfung
* `Application.Run`
* WinForms-Komponentenerstellung
* Event Wiring
* Disposal
* Timer-/Component-Cleanup
### Lösungsoption
1. Sämtliche genannten `[ExcludeFromCodeCoverage]` entfernen.
2. Startup durch minimale behavior-preserving Seams testbar machen:
   * Application-run delegate;
   * MessageBox delegate;
   * kontrollierbare Single-instance-/Mutex-Grenze.
3. `Program.Main()` auf einem STA-Testthread ausführen, wobei der Message Loop über die Test-Seam abgefangen wird.
4. Normale, Already-running- und Startup-exception-Pfade testen.
5. `MainForm` konstruieren und disposen und dabei tatsächliche Zustände/Side Effects prüfen.
### Nicht zulässig
* neue Runsettings-Exclusions;
* namespace/file exclusions;
* `[ExcludeFromCodeCoverage]` an anderer Stelle;
* Reflexionsaufrufe ohne sinnvolle Assertions.
---
## B2-003 — HIGH — Coverage liegt massiv unter 100/100/100
### Area
Test completeness
### Actual
```text
Line:   90.60%
Branch: 85.06%
Method: <100%
```
### Particularly affected
```text
MainForm.MainWorkflow.cs
MainForm.ReferenceWorkflow.cs
MainForm.Recovery.cs
MainForm.ImageSelection.cs
AssetProcessorService.FileOps.cs
AssetProcessorService.Main.cs
AssetProcessorService.Reference.cs
SessionService.cs
ValidationService.Session.cs
```
Diese Bereiche umfassen gerade die sicherheitsrelevanten Transaktions-, Recovery-, Rollback- und Dateisystempfade.
### Lösungsoption
Coverage gezielt in dieser Reihenfolge schließen:
1. Main Workflow
2. Reference Workflow
3. Recovery
4. FileOps
5. Main Processor
6. Reference Processor
7. SessionService
8. ValidationService.Session
9. Direct/Image Selection/UI
10. kleine Restbranches und Getter
Für jeden Gap:
```text
Normalfall
Boundary
Failure
negative side effects
Recovery
Retry
state after operation
filesystem after operation
```
testen.
Keine Tests hinzufügen, die lediglich eine Methode betreten.
---
## B2-004 — HIGH — permanentes Coverage-Gate entspricht nicht dem verlangten Standard
### Area
`.github/workflows/ci.yml`
### Current behavior
Das Gate berechnet gerundete Prozentwerte und scheitert nur bei:
```text
lineRate   < 90
branchRate < 85
```
Außerdem prüft es nur eine hart codierte Liste von 13 Klassen/Dateien.
Function Coverage wird gar nicht geprüft.
Damit könnte CI bei ungefähr den **jetzigen** Coverage-Werten grün werden, obwohl der neue Acceptance-Prompt exakt 100/100/100 verlangt.
### Lösungsoption
Gate auf exakte Counter umstellen:
```text
covered_lines    == total_lines
covered_branches == total_branches
covered_methods  == total_methods
```
Zusätzlich:
* alle `src/AssetProvenanceHelper/**/*.cs` dynamisch inventarisieren;
* Dateien ohne ausführbare Sequence Points explizit als solche klassifizieren;
* jede fehlende ausführbare Produktionsdatei als Fehler behandeln;
* Coverage-XML missing/empty ⇒ FAIL;
* verbotene Coverage-Ausschlüsse ⇒ FAIL;
* generiertes `obj/` aus dem Produktionsnenner entfernen;
* keine Prozent-Rundung;
* vollständige Per-File-Tabelle erzeugen.
---
## B2-005 — HIGH — Mutation Gate deckt nicht die komplette Produktion ab
### Area
Stryker
### Summary
Der Mutationstest mutiert nur einen Teil der Anwendung und erlaubt `break: 80`.
### Why this is a defect
Das erlaubt theoretisch eine große Zahl überlebender Mutanten, obwohl das Acceptance-Kriterium lautet:
```text
zero meaningful surviving mutants
```
### Lösungsoption
Mutation standardmäßig über den gesamten Produktionsbaum:
```text
**/*.cs
```
Nur Compiler-/Buildoutput darf ausgeschlossen werden.
Ergänzen:
```text
JSON mutation reporter
reviewed-equivalent-mutants manifest
final survivor validation
```
Der finale Gate-Schritt darf einen Survivor nur akzeptieren, wenn dessen konkrete Mutant-ID mit nachvollziehbarer Equivalent-Begründung hinterlegt und geprüft wurde.
Das ist konsistenter als pauschal `break: 100`, weil echte Equivalent Mutants laut Acceptance-Prompt dokumentiert werden dürfen.
---
## B2-006 — MEDIUM — Acceptance-Workflow stoppt nach dem ersten Defekt
### Area
QA orchestration
### Actual
Nach den beiden Debug-Testfehlern überspringt der Hauptjob:
```text
Release Build
Release Tests
RecoveryCritical
20x Flakiness
Publish
Smoke
```
Der Coverage-Job überspringt nach den Testfehlern außerdem seinen eigentlichen Coverage-Verification-Step.
### Why this matters
Der Acceptance-Prompt schreibt ausdrücklich vor, nach einem gefundenen Defekt weiterzutesten.
### Lösungsoption
Normales CI darf weiterhin fail-fast bleiben.
Zusätzlich einen dedizierten:
```text
zero-exception-audit.yml
```
anlegen.
Dieser:
* speichert Exitcodes aller Phasen;
* lässt nach einem Fehler die weiteren Auditphasen trotzdem laufen;
* sammelt immer TRX/Cobertura/Mutation/Smoke/Leak-Artefakte;
* hat am Ende einen Aggregator;
* Aggregator gibt FAIL aus, sobald irgendeine Pflichtphase fehlschlug.
Damit wird nichts weichgeschaltet; nur die Fehlererfassung wird vollständig.
---
## B2-007 — MEDIUM — exakter Target-SHA-/Clean-Tree-Nachweis fehlt
### Area
Reproducibility
### Actual
Der bekannte Branch-HEAD ist:
```text
6f20c4f3220ede5d19dafab056f3234109b242da
```
Der PR-Workflow hat jedoch den Merge-Commit:
```text
ca94f32067e0f844155b4ab014b7136bcd078dbe
```
ausgecheckt. PR-Metadaten bestätigen beide SHAs.
`git status` wird nicht als Auditbeweis gespeichert.
### Lösungsoption
Finalen Acceptance-Run direkt auf dem Target-SHA ausführen und am Anfang speichern:
```powershell
git rev-parse HEAD
git status --porcelain=v1
git diff --stat
dotnet --info
```
Akzeptanz nur wenn:
```text
HEAD == requested SHA
git status == empty
```
Alle Coverage-/Mutation-/Package-Artefakte mit demselben SHA versehen.
---
# H. `_bugRun1.md` regression matrix
Der vorherige Bugrun identifizierte B-01 bis B-11.
| Historical issue              | Status                                                  |
| ------------------------------ | --------------------------------------------------------|
| B-01 `CS8625`                 | **FIX VERIFIED**                                        |
| B-02 Overlay Timer Flake      | Fix vorhanden; finale Repeat-Evidenz fehlt              |
| B-03 Non-parallel invariant   | **FIX PRESENT**                                         |
| B-04 TestWorkspace Leak       | **FIX PRESENT**, finaler Leak-Sweep fehlt               |
| B-05 Coverage Gate            | **NOT FIXED**                                           |
| B-06 TickCount overflow       | Fix mit Timer-Testumbau vorhanden                       |
| B-07 physical cursor          | **FIX PRESENT**                                         |
| B-08 processedAt clock reread | **FIX PRESENT**                                         |
| B-09 CurrentCulture           | informational / kein Defekt                             |
| B-10 stale old-TFM artifacts  | kein eingecheckter Branchdefekt                         |
| B-11 short thread joins       | Fix laut Branchänderung vorhanden; Stressnachweis fehlt |
## B-01
Der aktuelle Debug Build mit `-warnaserror` ist grün.
Der Release Build im Coverage-Job ist ebenfalls grün.
Damit ist die ursprüngliche nullable/reflection-Compile-Störung unabhängig bestätigt.
## B-02 / B-06 / B-07
`MainForm.PromptPreview.cs` besitzt jetzt:
```csharp
internal static Func<Point>? CursorPositionProvider;
```
und verwendet:
```csharp
var cursor =
    CursorPositionProvider?.Invoke()
    ?? Cursor.Position;
```
Damit ist die zentrale Test-Seam vorhanden.
Die vorgeschriebene aktuelle 100×-Repeat-Abnahme konnte wegen des früheren CI-Abbruchs aber nicht belegt werden.
## B-03
Jetzt vorhanden:
```csharp
[assembly: CollectionBehavior(
    DisableTestParallelization = true)]
```
Zusätzlich setzt `xunit.runner.json`:
```text
parallelizeAssembly: false
parallelizeTestCollections: false
```
Und das Testprojekt kopiert die Datei nun ins Output-Verzeichnis.
Die Lösung ist intern konsistent.
## B-04
`TestWorkspace.Dispose()` besitzt nun Retry-then-throw statt eines verschluckten Cleanup-Fehlers.
Damit ist der Harness strukturell verbessert.
Der abschließende neue Zero-Leak-Gesamtlauf fehlt aufgrund des vorzeitigen CI-Abbruchs.
## B-05
Unverändert.
Der aktuelle PR ließ B-05 bewusst offen. Das neue Acceptance-Ziel fordert jedoch nicht nur Headroom, sondern **exakte 100 %**.
Damit ist B-05 unter dem neuen Standard eindeutig ein Abnahmeblocker.
## B-08
`processedAt` wird jetzt bis `CompleteMainUiAfterDurableCommit(...)` durchgereicht.
No-Reference Recovery verwendet zwingend das gespeicherte:
```csharp
session.MainProcessedAt!.Value
```
und liest dort nicht erneut `DateTimeOffset.Now`.
Dieser Fix ist konzeptionell korrekt und sollte beibehalten werden.
---
# I. Positive Befunde
Folgende bereits gute Mechanismen sollten bei den Korrekturen nicht zurückgebaut werden:
* .NET SDK ist reproduzierbar auf `10.0.301` gepinnt.
* Debug und Release kompilieren aktuell mit `-warnaserror`.
* Testparallelisierung ist doppelt abgesichert.
* Prompt-Overlay verwendet eine deterministische Cursor-Test-Seam.
* TestWorkspace-Cleanup schluckt dauerhafte Fehler nicht mehr.
* `processedAt` wird sauber propagiert.
* No-Reference-Recovery erfindet bei fehlendem Timestamp nicht mehr stillschweigend „now".
* `TwoChoiceDialog.cs` erreicht im aktuellen Coverage-Report 100/100/100.
* `ProviderTemplateRules.cs` erreicht 100 % Lines/Branches/Methods.
* Das aktuelle ChatGPT-Provider-Template verwendet vorsichtige Angaben wie `not recorded`, `draft` und `unapproved`, statt nicht überprüfbare rechtliche Reviews als Tatsachen zu behaupten.
* Die Smart-App-Control-Vorgaben dokumentieren korrekt den vorgesehenen SAC-sicheren Testpfad über `dotnet test` bzw. den signierten `dotnet`-Host.
---
# J. Remaining gaps
Folgende Pflichtpunkte sind für diesen Stand noch **nicht** objektiv bewiesen:
```text
Exact target SHA direct checkout + clean git status
Debug full-suite PASS
Release full-suite PASS
RecoveryCritical PASS
20 consecutive Release-suite passes
100x historically flaky-test passes
Full cleanup/leak/lock verification
100.00% line coverage
100.00% branch coverage
100.00% function/method coverage
All production files in valid denominator
Complete per-function traceability matrix
Complete state-transition proof
Every mutation/crash boundary
Complete historical bugs*.md re-execution
Zero meaningful surviving mutants
Current publish/package verification
Current startup smoke verification
Final clean post-fix acceptance run
```
Daher ist:
```text
REMAINING_GAPS != NONE
```
---
# K. Consistency check of all proposed solutions
Die vorgeschlagenen Maßnahmen wurden auf gegenseitige Widersprüche geprüft.
## Ergebnis: konsistent
### Newline-Testfix vs. Produktionsverhalten
Die CRLF-Probleme werden ausschließlich im Testdesign korrigiert.
Der Produktionsrenderer wird **nicht** verändert, sofern kein separates Produktrequirement LF-Kanonisierung verlangt.
### Coverage denominator
Die verbotenen Exclusions werden entfernt.
Es werden **keine Ersatz-Exclusions** in Coverlet oder Runsettings eingeführt.
### Startup testability
Startup wird über behavior-preserving Seams testbar gemacht.
Dadurch bleibt das echte Verhalten erhalten, während `Program.Main()` und `RunApplication()` deterministisch geprüft werden können.
### Coverage Gate
Das Gate wird exakt auf denselben vollständigen Produktionsnenner ausgerichtet, der auch für den Audit gilt.
Damit gibt es keine unterschiedliche Definition von „100 %" zwischen lokalem Audit und CI.
### Mutation Gate
Mutation verwendet denselben vollständigen Produktionsumfang.
Damit kann kein Code zwar zur Coverage zählen, gleichzeitig aber systematisch aus Mutation Testing verschwinden.
### Fail-fast CI
Normales CI kann weiterhin fail-fast arbeiten.
Nur der dedizierte Acceptance-Audit wird non-fail-fast ausgeführt und aggregiert anschließend alle Ergebnisse.
Das schwächt keine Quality Bar.
### Historical fixes
Cursor seam, serial execution, TestWorkspace-Cleanup und `processedAt`-Propagation bleiben erhalten.
Keine vorgeschlagene Korrektur baut diese Fixes zurück.
---
# L. Recommended fix order
## 1. B2-001
Die zwei CRLF-sensitiven Tests reparieren.
Danach sofort:
```text
Debug full suite
Release full suite
```
erneut ausführen.
## 2. B2-002
Alle Produktions-Coverage-Ausschlüsse entfernen und Startup/Designer deterministisch testen.
## 3. B2-004
Coverage-Gate auf exakte 100/100/100 plus vollständiges dynamisches Produktionsinventar umstellen.
## 4. B2-003
Alle verbleibenden Line-/Branch-/Method-Gaps systematisch schließen.
## 5. B2-005
Mutation Scope auf vollständigen Produktionscode erweitern und Survivor-Gate einführen.
## 6. B2-006
Dedizierten vollständigen Acceptance-Workflow hinzufügen, der nach einzelnen Fehlern weiterprüft.
## 7. B2-007
Finale Abnahme direkt auf exakt dem vorgesehenen SHA mit protokolliertem Clean-Status ausführen.
## 8. Final verification
Danach vollständig:
```text
Debug clean build
Debug tests
Release clean build
Release tests
RecoveryCritical
100/100/100 coverage
per-file verification
mutation
20x full Release suite
100x historically flaky tests
cleanup/locks/processes/timers
publish
package structure
SAC-appropriate smoke
final clean rerun
```
---
# M. Final verification status
```text
Debug build with -warnaserror:
PASS
Debug tests:
FAIL
991 passed
2 failed
0 skipped
993 total
Release build with -warnaserror:
PASS in Coverage job
Release tests:
FAIL
991 passed
2 failed
993 total
RecoveryCritical:
NOT RUN in current CI attempt
Line coverage:
90.60%
FAIL
Branch coverage:
85.06%
FAIL
Function/method coverage:
represented production = 447 / 451 = 99.11%
additional executable methods excluded from instrumentation
FAIL
Production executable files intentionally omitted:
Program.cs
MainForm.Designer.cs
Uncovered executable lines:
NONZERO
Uncovered branches:
NONZERO
Uncovered functions:
NONZERO
Meaningful surviving mutants:
NOT PROVEN
Unexplained skipped tests in executed test runs:
0
Flaky tests:
NOT PROVEN ZERO
Leaked temporary workspaces:
NOT PROVEN ZERO in final current run
Leaked files/directories:
NOT PROVEN ZERO
Unexpected file locks:
NOT PROVEN ZERO
Current package verification:
NOT COMPLETED
Current startup smoke:
NOT COMPLETED
```
---
# N. Final verdict
## FAIL — ZERO-EXCEPTION TEST STANDARD NOT SATISFIED
Der Branch darf unter dem vorgegebenen Acceptance-Prompt derzeit **nicht abgenommen** werden.
Die unmittelbare erste Korrektur sollte B2-001 beheben. Danach müssen B2-002 bis B2-007 umgesetzt und sämtliche verbleibenden Coverage-/Mutation-/Repeat-Gaps geschlossen werden.
Ein späteres PASS ist nur zulässig, wenn der finale saubere Lauf objektiv mindestens Folgendes nachweist:
```text
Debug build with -warnaserror: PASS
Debug tests: PASS
Release build with -warnaserror: PASS
Release tests: PASS
RecoveryCritical: PASS
Line coverage: 100.00%
Branch coverage: 100.00%
Function/method coverage: 100.00%
Production files missing unexpectedly: 0
Uncovered executable lines: 0
Uncovered branches: 0
Uncovered functions: 0
Meaningful surviving mutants: 0
Unexplained skipped tests: 0
Flaky tests: 0
Leaked temporary workspaces: 0
Leaked files/directories: 0
Unexpected file locks: 0
Packaging: PASS
Smoke: PASS
Historical regression matrix: COMPLETE
Functional traceability: COMPLETE
```
Bis dahin bleibt das Ergebnis eindeutig:
### FAIL — ZERO-EXCEPTION TEST STANDARD NOT SATISFIED
