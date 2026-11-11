# Audit: Weiterentwicklung mit KI-Coding-Agenten, Architektur und Fachlogik

Stand: 2026-11-11, Commit `d553222` (`main`). Art: Analyse ohne Änderung am Produktcode; einzige neue Datei ist dieser Bericht.
Vorgänger: `ai-development-readiness.md` (2026-11-08, Commit `2029f15`). Dieser Bericht prüft, was davon erledigt ist,
und geht zusätzlich in die Fachlogik (gemeinsame Steuerung, Bilanzierung, Preise, Zeit, Datenhaltung).

Sprache: Deutsch wie Auftrag und Vorgängerbericht (abweichend von der Regel „docs in English“ in `AGENTS.md`).

**Einstufung der Sicherheit**

- **nachgewiesen** – durch Ausführung oder eindeutigen Codepfad belegt.
- **begründetes Risiko** – Codepfad belegt, Häufigkeit oder Wirkung im Betrieb nicht gemessen.
- **offene Frage** – ohne Betreiberwissen oder Zugriff auf den Pi nicht entscheidbar.

Aufwand: **S** < ½ Tag · **M** 1–3 Tage · **L** > 3 Tage (jeweils inklusive Tests und Doku).

---

## 0. Abdeckung und Prüfprotokoll

### Untersucht

| Bereich | Tiefe |
|---|---|
| `AGENTS.md`, `CLAUDE.md`, `README.md`, `docs/README.md`, Governance-, Architektur-Dokumente | vollständig gelesen |
| `docs/20-domain/battery-decision-engine.md`, `energy-costs.md`, `docs/30-operations/electricity-price-providers.md`, `persistence-model.md`, Teile von `e3dc-rscp.md`, `backlog.md`, `security-and-deployment.md`, `frontend-architecture.md` | gelesen bzw. gezielt durchsucht |
| Gemeinsame Steuerung: `JointBatteryExecutionBackgroundService`, `JointBatteryExecutionCoordinator`, `JointBatteryLiveLimiter`, `JointBatterySlotCommitment`, `JointBatteryAllocator`, `JointBatteryHardware`, `JointAware*`, `JointBatteryPlanner`, `JointBatteryShadowCalculation`/`-BackgroundService`, `JointPlanInputFallback` | Zeile für Zeile |
| Einzelakku-Optimierer `BatterySocOptimizer` | Kostenmodell und Grenzen |
| Bilanzierung: `BatterySavingsAccountingService`, `SavingsEndpoints`, `EnergyHistoryService`, `EfEnergyHistoryRepositories`, `EfDecisionLogRepository` | Codepfade |
| Einstellungen: `EfControllerSettingStore`, `ProtectedRscpSettingStore`, `ControllerSettingDefaults` | Codepfade |
| Querschnitt: `FileExceptionLoggingMiddleware`, `HealthEndpoints`, `AdminWriteGuardMiddleware`, anonyme Endpunkte (Push, OCPP), DI-Registrierung | Codepfade |
| Datenbank: `ControllerDbContext`, `ControllerDbInitializer`, Aufbewahrung | Codepfade |
| Frontend: Struktur, Router, Vuetify-Einbindung, alle `fetch`-Aufrufe, `zentraleApi.ts`, `useZentraleData.ts`, `zentraleModel.ts` | Struktur und Muster |
| Tests: Zuordnung der Testklassen zu kritischen Klassen, Testlauf | siehe Protokoll |
| `scripts/deploy/*`, `.github/workflows/ci.yml` | gelesen |

### Nicht oder nur oberflächlich untersucht

- `CurrentBatteryDecisionService` (1149 Zeilen), `BatteryForecastSimulator` (798) und `MqttVictronSetpointPublisher` (542) nur stichprobenartig.
- E-Auto (außer manueller Wallbox-Grenze), Weishaupt, Homematic, BMW CarData, Kalender, Routing, Tarifvergleich.
- Protokollimplementierungen RSCP, OCPP, Modbus im Detail.
- Frontend im Browser, Barrierefreiheit, End-to-End-Abläufe.
- Laufzeitzustand des Raspberry Pi (Datenbankgröße, Zeilen pro Tabelle, gesetzte Einstellungen, verfolgter Branch, Cron, Logs). Kein Zugriff laut Auftrag.
- Reales Geräteverhalten.

### Ausgeführte Prüfungen (frischer Klon von `d553222` im Scratch-Verzeichnis, `TIBBER_ACCESS_TOKEN` und `RUN_LIVE_TESTS` entfernt)

| Befehl | Ergebnis |
|---|---|
| `dotnet test TibberVictronController.slnx` | **784 bestanden, 1 übersprungen (Live-Test Tibber), 0 Fehler**; Business 470, Dal 171+1, Api 143; 16,5 s inkl. Restore und Build |
| Build-Warnungen desselben Laufs | 5: `EF1002` ×3 (`ControllerDbInitializer.cs:76,99,128`), `xUnit1031` (`JointBatteryExecutionTests.cs:103`), `xUnit2017` (`CalendarTripTests.cs:92`) – unverändert seit 08.11. |
| `npm ci` | 109 Pakete, 9,4 s |
| `npx vue-tsc --noEmit` | Exit 0, 16,1 s |
| `node --experimental-strip-types --test tests/settingsPresentation.test.mjs` | 5/5 bestanden (Node 22.19) |
| `npx vite build --outDir <scratch>` (statt `npm run build`, um `dist/` nicht zu verändern) | Exit 0, 2,3 s; **ein JS-Chunk 1.440 kB (444 kB gzip), CSS 1.016 kB (154 kB gzip)**, MDI-Schrift in vier Formaten (woff2 403 kB …) |

Die dokumentierten Befehle aus `AGENTS.md` funktionieren in einer sauberen Umgebung. Nicht ausgeführt: `dotnet run`/`npm run dev`
(verbindet sich mit Geräten), Live-Tests, Paket-Schwachstellenprüfung (zuletzt am 08.11. ohne Befund).

### Stand der Vorbefunde vom 08.11.

| ID | Thema | Stand |
|---|---|---|
| F01 | Build-/Testbefehle | **erledigt** (Lauf oben) |
| F02 | CI / Auto-Deploy | **teilweise**: CI vorhanden; Pi wartet nicht auf CI, kein Rollback, Standard-Branch `develop` (→ K10) |
| F03 | Veraltete Agentenanweisungen | **weitgehend erledigt** (`AGENTS.md`); neue Widersprüche (→ W4) |
| F04 | Schreibschutz verteilt | **teilweise**: Schalterliste in `AGENTS.md:48`; kein zentrales Gate; neue Ausnahme (→ K11) |
| F05 | Schema ohne Migrationen | Regel dokumentiert (`AGENTS.md:47`); Abgleichtest fehlt; `project-structure.md:21` nennt weiter „migrations“; `dotnet-tools.json` noch vorhanden |
| F06 | Toter Code | **erledigt** (`472ee62`, `d913629`, `23e575c`) |
| F07 | Joint-Projektion in Api | offen, erweitert (→ W1, W2) |
| F08 | Zentrale DI-Datei | offen (287 Zeilen, von fast jedem Feature-Commit berührt, zuletzt `d553222`) |
| F09 | Verschlüsselung nur per Liste | offen; `AGENTS.md` und README beschreiben es jetzt korrekt |
| F10 | Live-Tests | **erledigt** |
| F11 | Systemzeit in Business | offen: `BatteryForecastService.cs:105`; Api 11, Dal 16 Stellen |
| F12–F14, F16–F21 | Verträge, Settings-Speicher, Designs, Health, Linter, Timeouts, Pipeline-Tests, Testdoubles, Sprache | offen (F18: 5 HttpClients ohne Timeout, `ControllerServiceCollectionExtensions.cs:199,207,211,241,263`) |
| F15 | Betriebshandbuch | offen (kein Runbook in `docs/30-operations/`) |

---

## A. Gesamtbewertung

**Gut geeignet für kleine, lokale Änderungen; riskant für Änderungen an der Akku-Steuerung und an Auswertungen.**

Der Einstieg für Agenten ist seit dem 08.11. deutlich besser: eine anbieterneutrale `AGENTS.md`, funktionierende
Prüfbefehle, CI, entfernte Altpfade. Die Tests sind schnell (rund 17 s für 784 Tests), brauchen keine Hardware und
prüfen im Joint-Kern fachliches Verhalten (Richtungswechsel, Rampe, Überschreitung, Reserve, Einspeiseschutz).

Die größten Hindernisse liegen jetzt in der **Fachlogik und ihrer Beschreibung**, nicht mehr im Werkzeug:

1. **Zwei Wirtschaftsmodelle mit unterschiedlichen Regeln.** Der im Betrieb aktive Zwei-Akku-Planer kennt die
   dokumentierten Regeln des Einzelakku-Optimierers (Halte-Hürde, Negativpreis-Vollladung, Überschreiten des
   Planungsmaximums) nicht (K1). Die Doku sagt nicht, für welchen Pfad welche Regel gilt.
2. **Irreführende Orientierung an der heikelsten Stelle.** Der produktive Planer heißt „Shadow“ und meldet „keine
   Hardwarebefehle“ (W1); drei Dokumente sagen, die gemeinsame Steuerung sei nicht in Betrieb, eines sagt „always on“ (K12).
3. **Implizite Datenmodelle.** Das Entscheidungsprotokoll ist zugleich Preisarchiv und Netzleistungsquelle für fünf
   Verbraucher (K5); die Ersparnisrechnung überschreibt Tageswerte aus unvollständigen Rohdaten (K3).
4. **Laufzeitlast auf dem Pi** im Planungspfad (K7) und in der Sicherheitsschleife (K8) – belegt durch Codepfad und
   im Code dokumentierte Betreibermessungen, aber nicht selbst gemessen.

### Stärken, die erhalten bleiben müssen

- Compiler-erzwungene Schichtgrenze: `Business.csproj` hat keine Referenzen; Dal referenziert nur Business (geprüft).
- Fail-closed-Ausführung: abgelaufene Messwerte oder Sollwerte werfen (`JointBatteryLiveLimiter.cs:14-16`), gegenläufige
  Sollwerte sind verboten (`:20-21`), Einspeiseschutz (`:45-46`, `:71`), zwei neutrale Messungen vor Richtungswechsel
  (`JointBatteryExecutionCoordinator.cs:15-31`), Wiederherstellungspflicht vor dem ersten Schreiben
  (`JointBatteryExecutionBackgroundService.cs:134-147`), Watchdog mit Zeitbudgets (`:14-19`), `--joint-stop` in systemd.
- E3/DC-Adapter prüft `jointBattery.executionEnabled` selbst (`Dal/E3dc/E3dcRscpPowerControl.cs:20`).
- Energiehistorie ist idempotent und verschlechtert gespeicherte Werte nie (`EnergyHistoryService.cs:7-9`; `EnergyQuarterHourCalculator.Merge`).
- Zeitumstellung ist in zehn Testdateien abgedeckt (u. a. `EnergyCostReportTests`, `ComparableDayPriceEstimatorTests`, `OctopusPriceForecastProviderTests`).
- Fehlermeldungen der Joint-Pfade geben bewusst nur eigene Texte weiter (`JointBatteryExecutionBackgroundService.cs:65-71`).
- Kommentare erklären Absicht mit Datum und Betreiberbeobachtung; das verhindert unbemerktes Zurückdrehen.

---

## B. Priorisierte Befunde

### B1. Korrektheits- und Betriebsrisiken

| ID | Befund | Codebeleg | Konkrete Auswirkung | Prio | Empfohlene Maßnahme | Aufwand | Sicherheit |
|---|---|---|---|---|---|---|---|
| K1 | Zwei Optimierer mit verschiedenen Wirtschaftsregeln. Joint: Strafkosten nur auf Netzladung (`MinimumSavings + Wear`, Verschleiß nicht mit Wirkungsgrad gewichtet), **keine Halte-Hürde**, Netzladung nie über das Planungsmaximum (auch bei Negativpreis), Gleichstand bevorzugt die kleinere Leistung (= Halten). Einzelakku: Halte-Hürde auf neu zurückgehaltene Energie, `Wear × Efficiency`, Sonderregel Negativpreis-Vollladung. E3/DC übernimmt Hürde und Verschleiß des Victron. | `JointBatteryPlanner.cs:60,74,76,78`; `BatterySocOptimizer.cs:215-216`; `BatteryForecastSimulator.cs:29-30`; `JointBatteryShadowCalculation.cs:224`; Regeln ohne Pfadangabe: `battery-decision-engine.md:64,257,265,272,280` | Im Betrieb (Joint aktiv) gelten dokumentierte Regeln nicht: Zurückhalten schon bei minimalem Preisvorteil, keine Vollladung bei negativen Preisen über das Planungsmaximum. Tuning im Einzelakku-Pfad bleibt wirkungslos, ohne dass ein Agent es merkt. | hoch | Erst Betreiberentscheidung, welche Regel für Joint gilt; Regeln in der Doku je Pfad kennzeichnen; Golden-Szenarien für `JointBatteryPlanner` (Negativpreis, Halten, kleine Spreizung); danach Angleichung je Regel in eigenem Commit | M | nachgewiesen (Code); Wirkung in Euro nicht gemessen |
| K2 | Fehlende PV-Prognose wird im Joint-Plan als 0 kWh angesetzt; der Kommentar nennt das „vorsichtig“. Für Netzladung ist es das Gegenteil: der Plan sieht einen PV-freien Tag und kauft nachts. Die Doku verbietet genau das, aber nur für die Restenergiebewertung. | `JointPlanInputFallback.cs:45-63`; `JointBatteryShadowCalculation.cs:135-139`; Doku `battery-decision-engine.md:364` | Bei Ausfall von Forecast.Solar (Rate-Limit, Netz) und leerem Cache unnötige Netzladung. | mittel | Slots ohne PV-Prognose wie geschätzte Preise behandeln: keine Netzladung, die nur durch diese Slots begründet ist (oder Plan als „degradiert“ ohne Netzladung). Test mit fehlender PV. | S–M | nachgewiesen (Code); Häufigkeit offen |
| K3 | Ersparnis-Neuberechnung ist nicht gegen unvollständige Rohdaten geschützt. `GET /api/savings` für Woche/Monat/Jahr rechnet den ganzen Zeitraum aus Live-Samples neu und **ersetzt** Tageswerte. Live-Samples werden rollierend gelöscht (Standard 14 Tage); der Tag an der Löschgrenze hat nur noch Teildaten. Fehler werden ohne Log verschluckt; ein GET schreibt. | `SavingsEndpoints.cs:71-80`; `BatterySavingsAccountingService.cs:48,89`; `VictronMqttClientService.cs:483`; Gegenbeispiel mit Schutz: `EnergyHistoryService.cs:7-9` | Gespeicherte, vollständige Tagesersparnis wird beim Öffnen einer Monats-/Jahresansicht durch einen kleineren Wert ersetzt. Unbemerkt, nicht rückgängig zu machen. Zusätzlich lädt eine Jahresabfrage alle vorhandenen Samples (bei 60 Tagen Aufbewahrung ≈ 5 Mio. Zeilen, s. K7) in einem Request. | hoch | Wie `EnergyHistoryService`: Tageswert nur ersetzen, wenn `MeasuredSeconds` nicht sinkt; Neuberechnung auf Tage innerhalb der Rohdaten-Aufbewahrung begrenzen; im `catch` loggen. Test: Tag mit halbierten Samples überschreibt nicht. | S | nachgewiesen (Codepfad); nicht durch Test reproduziert |
| K4 | Joint-Projektion ins Entscheidungsprotokoll setzt bei Ladung ohne Plan-Ladequelle „Netz“. Die Live-Begrenzung macht aus Halten/Entladen regelmäßig PV-Laden; die tatsächliche Ladequelle des Befehls wird nicht übernommen. | `JointAwareBatteryDecisionService.cs:27`; `JointBatteryLiveLimiter.cs:36-43`; Speicherung `JointBatteryExecutionBackgroundService.cs:367-375`; Anzeige `DecisionHistoryChartPanel.vue:206` | Verlauf zeigt PV-Überschussladung als Netzladung; `GridChargeEnergyCredit` könnte nach Rückkehr zum Einzelpfad falsche Gutschriften ableiten. | mittel | Ladequelle des ausgeführten Befehls in `JointBatteryExecutionStatus` führen und projizieren. Test. | S | begründetes Risiko |
| K5 | Das Entscheidungsprotokoll ist das einzige dauerhafte Preisarchiv und Quelle für Netzleistung. Verbraucher: Energiehistorie/Stromkosten, Vergleichstag-Schätzer, Restenergiewert, E-Auto-Schwelle, Preisverlauf. Im Joint-Betrieb wird nur im Status `Active` protokolliert. Die Energiehistorie filtert geschätzte Preise nicht (die Preisabfrage für Schätzer schon). | `EfDecisionLogRepository.cs:18-60`; `EfEnergyHistoryRepositories.cs:15-28`; `JointBatteryExecutionBackgroundService.cs:356-375`; Verbraucher: `JointBatteryPriceHistory.cs:21`, `EvCheapPriceThresholdCache.cs:46`, `ComparableDayPriceEstimator.cs`, `Api/Prices/PriceTimelineService.cs`; Doku `energy-costs.md:17` | Jede Änderung an Häufigkeit oder Inhalt des Entscheidungsprotokolls verändert still Stromkosten, Preisschätzung und E-Auto-Plan. Lücken bei `Blocked`/`Restoring` erzeugen „Teilkosten“. | mittel | Eigene, additive Tabelle für veröffentlichte Preise je Anbieter und Slot (Schritt D8); Verbraucher einzeln umstellen; bis dahin Hinweis in `AGENTS.md`. | M | nachgewiesen |
| K6 | Aufbewahrung des Entscheidungsprotokolls ist einstellbar (`decisionLog.retentionDays=90`, in der GUI), wird aber nie angewendet; `OperationalEvents` haben gar keine Aufbewahrung. | `ControllerSettingDefaults.cs:282`; `GuiMetadataCatalog.cs:98`; `EfDecisionLogRepository.cs:116` (einziger Aufrufer ist ein Test, `SqlitePersistenceTests.cs:117`) | Datenbank wächst unbegrenzt (Betreibernotiz: ca. 1,25 GB); die Einstellung täuscht Wirkung vor. **Achtung:** einfaches Aktivieren löscht das Preisarchiv aus K5 und den Startpunkt der Energie-Nachberechnung. | mittel | Erst K5, dann Aufbewahrung aktivieren; bis dahin Einstellung als „noch ohne Wirkung“ kennzeichnen. | S (nach K5) | nachgewiesen |
| K7 | Der produktive Joint-Planer lädt bei jedem Lauf (30 s Pause) 14 Tage Live-Samples und lernt das Verbrauchsprofil neu. Laut `energy-costs.md:15` entsteht etwa ein Sample pro Sekunde (≈ 1,2 Mio. Zeilen); der Code dokumentiert 25–30 s Ladezeit auf dem Pi. Pläne gelten 75 s; mit 30 s Pause bleiben rund 15 s Reserve. Der Einzelakku-Pfad baut das Profil höchstens stündlich. | `JointBatteryShadowCalculation.cs:142,152,227`; `JointBatteryShadowBackgroundService.cs:36`; `JointBatteryExecutionBackgroundService.cs:19,223`; Doku `battery-decision-engine.md:305` | Wird das Laden langsamer (Deploy-Build auf dem Pi, Datenbanklast), gilt der Plan als veraltet: Notbetrieb Eigenverbrauch **ohne geplante Netzladung**. Dauerhafte CPU- und I/O-Last auf dem Pi. | hoch | Erst messen (Ladezeit wird bereits als Warnung ab 20 s geschrieben, `:249-250`); dann Profil wie `PvForecastLearning` stündlich zwischenspeichern oder je Viertelstunde in SQL aggregieren. | S–M | Pfad nachgewiesen; Laufzeit aus Code-Kommentar, Zeilenzahl aus Doku abgeleitet |
| K8 | Die watchdog-überwachte Sicherheitsschleife liest jede Sekunde Dutzende Einstellungen einzeln aus SQLite (kein Cache) und entschlüsselt die RSCP-Zugangsdaten mehrfach. | `EfControllerSettingStore.cs:26-31`; `JointBatteryExecutionBackgroundService.cs:88-106,158-179`; `JointBatteryHardware.cs:74-84,109`; `ProtectedRscpSettingStore.cs:24-36`; `update-linux.sh:212-213` (`WatchdogSec=15`, `SIGKILL`) | Blockiert die Datenbank (Sicherung, lange Schreibvorgänge), überschreitet der Takt sein Budget; über ~12 s folgt ein Watchdog-Kill. Eine Betreibernotiz (nicht im Repo) meldet Watchdog-Neustarts während `sqlite3 .backup` am 2026-11-04. | mittel | Einstellungs-Snapshot je Takt bzw. Cache mit Invalidierung beim Speichern; Test mit zählendem Fake (z. B. ≤ 1 Lesevorgang je Schlüssel und Takt). | M | Pfad nachgewiesen; Abfragezahl aus Code geschätzt |
| K9 | Uneinheitliche Fehlerweitergabe. Unbehandelte 500er liefern `exceptionMessage` und Typname an den Client, das Frontend zeigt sie an; das **anonyme** `/health` liefert `LastErrorMessage` aus `exception.Message`; Betriebsereignisse speichern `exception.ToString()`. Die Joint-Pfade filtern dagegen bewusst. | `FileExceptionLoggingMiddleware.cs:150-157`; `DashboardPage.vue:466`; `HealthEndpoints.cs:52-55`; `DecisionExecutionBackgroundService.cs:123,203` | Texte von Socket-/HTTP-Ausnahmen (Adressen, URLs) erreichen Browser und ein anonymes Endpoint. | mittel | Eine Regel: Clients bekommen nur `{ message }` + `traceId`; `/health` nur Status; Details nur ins Server-Log. Test pro Pfad. | S | nachgewiesen (Code); ob reale Meldungen Geheimnisse enthalten: offene Frage |
| K10 | Auto-Update wartet nicht auf CI, Standard-Branch ist `develop`, fehlgeschlagener Healthcheck wird ignoriert, kein Rollback. | `auto-update-linux.sh:19`; `install-auto-update-cron.sh:22`; `update-linux.sh:351`; `security-and-deployment.md:47,55` | Ein roter Commit auf dem verfolgten Branch erreicht innerhalb von 5 min die Anlage. | hoch | Pi zieht nur einen von CI gesetzten Tag/Branch (`deploy/main`); vorheriges Release als Verzeichnis behalten; Healthcheck-Fehler → Rückfall. Doku-Beispiele auf `main`. | M | Repo nachgewiesen; verfolgter Branch auf dem Pi offen |
| K11 | Manuelle Wallbox-Grenze schreibt ohne `evCharging.controlEnabled`. | `EvChargingEndpoints.cs:65-69`; Regel `AGENTS.md:48` | Widerspruch zwischen Regel und Code; nur Admin, zeitlich begrenzt. Ein Agent kann daraus schließen, Schalter seien optional. | niedrig | Entweder als bewusste manuelle Ausnahme in `AGENTS.md` und Inventar dokumentieren oder Schalter prüfen. | S | nachgewiesen |
| K12 | Widersprüchliche Aussagen zum Betriebszustand der gemeinsamen Steuerung: „standardmäßig deaktiviert, Inbetriebnahme steht aus“ gegenüber „joint control is always on“ und Code-Kommentaren mit Vorfällen auf dem Pi (28./29.10., 24.10.). | `battery-decision-engine.md:337`; `e3dc-rscp.md:33,171-173`; `backlog.md:65` vs. `backlog.md:96`; `JointBatteryShadowCalculation.cs:227`; `JointBatteryLiveLimiter.cs:31` | Ein Agent kann nicht beurteilen, ob eine Änderung an Joint-Code sofort reale Hardware steuert. | hoch | Betreiber bestätigt den Ist-Zustand; eine Stelle (`e3dc-rscp.md`, Abschnitt Inbetriebnahme) wird Quelle, die anderen verweisen. | S | Widerspruch nachgewiesen; Ist-Zustand offene Frage |
| K13 | `/health` bewertet nur MQTT und den Einzelakku-Worker; im Joint-Betrieb rechnet dieser nur die Projektion. | `HealthEndpoints.cs:23-41` | Deploy-Healthcheck und Monitoring erkennen eine blockierte oder in Rücknahme hängende gemeinsame Ausführung nicht. | mittel | Komponente „JointExecution“ (Status, Alter) ergänzen; `Restoring` länger als X min → `Unhealthy`. | S | nachgewiesen |

### B2. Wartbarkeit und Orientierung für Agenten

| ID | Befund | Codebeleg | Konkrete Auswirkung | Prio | Empfohlene Maßnahme | Aufwand | Sicherheit |
|---|---|---|---|---|---|---|---|
| W1 | Der produktive Planer heißt „Shadow“: Klassen, Endpoint `/api/joint-battery/shadow`, DTO, Status `Shadow` mit Text „Es werden keine Hardwarebefehle gesendet“ – auch wenn die Ausführung genau diesen Plan nutzt. Doku: „Shadow-Modus bleibt rein lesend“. | `JointBatteryShadowBackgroundService.cs:7,67,71`; `JointBatteryShadowCalculation.cs:11`; Nutzung `JointBatteryExecutionBackgroundService.cs:189-190`; `battery-decision-engine.md:337`; `zentraleModel.ts:20` | Agenten halten Planer-Änderungen für folgenlos und testen sie zu schwach. | hoch | Sofort Hinweis in `AGENTS.md`; dann reine Umbenennung (`JointBatteryPlanning*`), Route als Alias behalten; Statustext korrigieren. | S + M | nachgewiesen |
| W2 | Fachliche Ablaufsteuerung der gemeinsamen Steuerung liegt in Api: Eingabeaufbereitung mit Ersatzregeln (250 Zeilen) und die Auswahl des Befehls je Takt (Handbetrieb, Festschreibung, Aktualisierung, PV-Ersatz, Notbetrieb). Für den Einzelpfad liegt das Gegenstück in Business (`BatteryForecastService`). | `JointBatteryShadowCalculation.cs:85-252`; `JointBatteryExecutionBackgroundService.cs:155-249` | Regeln sind nur über DI-lastige Api-Tests prüfbar; Agenten finden die Regel nicht unter `Business/Decisions`. | mittel | Reine Funktion „Befehl für diesen Takt“ nach Business; Eingabeaufbereitung als Business-Dienst über vorhandene Schnittstellen (Schritt D6). | M | nachgewiesen |
| W3 | Zwei vollständige Entscheidungspfade bleiben gepflegt (Einzelpfad ≈ 2300 Zeilen + 1148 Zeilen Tests), obwohl der Joint-Pfad laut Backlog dauerhaft läuft. Unklar, ob der Einzelpfad unterstützter Rückfall ist. | `CurrentBatteryDecisionService.cs`, `BatteryForecastSimulator.cs`, `BatterySocOptimizer.cs`; `JointAwareBatteryDecisionService.cs:15-16` | Doppelte Pflege, und K1 bleibt unentdeckt. | mittel | Produktentscheidung: Einzelpfad als Rückfall (dann Regelgleichheit herstellen) oder einfrieren (dann in Doku markieren). | S (Entscheidung) | offene Frage |
| W4 | Doku-Drift und doppelte Wahrheiten: README beschreibt die entfernte Drittel-Regel, E3/DC als „geplant“, Auslieferung über ASP.NET und 24-h-Prognose; Schnittstellenliste nennt 6 nicht existierende Interfaces; `END_SOC_RESERVE` „nicht mehr erzeugt“, aber als Halte-Grund gelistet; Live-Sample-Aufbewahrung 14 vs. 60 Tage; Preisanbieter-Doku „Stand 25.10.“ widerspricht sich bei API-Key vs. E-Mail/Passwort; `working-agreement.md` widerspricht `AGENTS.md` (ein vs. drei Prüfläufe). | `README.md:58-66,120,130,182`; `battery-decision-engine.md:75,182,309,313-324`; `energy-costs.md:10`; `ControllerSettingDefaults.cs:276`; `electricity-price-providers.md:3,13-17,107-109`; `working-agreement.md:24-25` vs. `AGENTS.md:63-64`; `project-structure.md:21`; `persistence-model.md` (beschreibt Alttabelle ohne `BatteryId`, ca. 14 Tabellen fehlen) | Agenten folgen veralteten Regeln oder suchen nicht existierende Typen. | mittel | Bereinigung als reiner Doku-Commit; Lieferregeln nur in `AGENTS.md`. | S | nachgewiesen |
| W5 | Namensfalle in der Datenbank: `DbSet BatterySavingsDailySummaries` schreibt in die Tabelle `BatterySavingsByBattery`; eine Alttabelle namens `BatterySavingsDailySummaries` wird weiter angelegt. | `ControllerDbContext.cs:30,259`; `ControllerDbInitializer.cs:55-56,136,508` | SQL-Auswertungen und Agenten greifen auf die falsche Tabelle. | niedrig | DbSet umbenennen (kein Schemaeffekt); Alttabelle in Doku als „nur Import“ kennzeichnen. | S | nachgewiesen |
| W6 | Schema aus zwei Quellen (EF-Modell + Roh-SQL, 17 `CREATE TABLE`) ohne Abgleichtest. | `ControllerDbInitializer.cs:26-33,136` | Spalte nur im EF-Modell → Bestandsdatenbank auf dem Pi scheitert zur Laufzeit. | mittel | Test: frische DB vs. aus Altschema aktualisierte DB (`PRAGMA table_info`). | S–M | nachgewiesen |
| W7 | Einstellungen als stringbasierter Zustandsspeicher, uneinheitlich geparst (103 Aufrufe in 61 Dateien): `decimal.Parse(?? "0")` im Hardware-Adapter, `MissingSettingException` im Planer. Fehlender E3/DC-Wirkungsgrad wird 0 und führt im Begrenzer zu einer Division durch null. | `JointBatteryHardware.cs:76,83`; `JointBatteryLiveLimiter.cs:25-26`; `JointBatteryShadowCalculation.cs:90-91` | Bleibt fail-closed, aber mit unklarer Meldung; jeder neue Aufrufer erfindet eigenes Verhalten. | niedrig | Typisierte Leser je Bereich (Muster `DatabaseVictronMqttSettingsProvider`), zusammen mit K8. | M | nachgewiesen |
| W8 | Große, gekoppelte Dateien und 8 Designs ohne Entscheidung, welche gepflegt werden. | `DashboardPage.vue` 1656 Z., `SettingsPage.vue` 1210, `LeitstandDashboard.vue` 889, `VictronMqttClientService.cs` 701, `MqttVictronSetpointPublisher.cs` 542, `ControllerServiceCollectionExtensions.cs` 287; `frontend-architecture.md:120` („All other designs are unchanged“) | Jede UI-Funktion vervielfacht sich; hoher Kontextbedarf. | mittel | Gepflegte Designs festlegen; übrige einfrieren. Große Dateien nur beim fachlichen Anlass teilen. | S (Entscheidung) | nachgewiesen |
| W9 | Frontend-HTTP ohne gemeinsamen Client: 46 `fetch`-Aufrufe in 23 Dateien, mindestens vier eigene Helfer mit unterschiedlicher Fehlerauswertung; manche Aufrufe ignorieren den Status; Abbruch nur in einigen Komponenten; Statuswerte als freie Strings in Backend und Frontend; eine einzige Frontend-Testdatei. | `zentraleApi.ts:9-30`; `DashboardPage.vue:455-470`; `LeitstandDashboard.vue:330-336`; `SettingsPage.vue:845`; `DynamicTariffComparison.vue:25`; `CarDataPanel.vue:85`; `zentraleModel.ts:19-22` | Uneinheitliche Fehleranzeigen; ein Agent wählt zufällig ein Muster. | niedrig | `zentraleApi.ts` als einziger Client (mit `AbortSignal`), neue Aufrufe nur darüber; Statuswerte als Union-Typ. | M | nachgewiesen |
| W10 | Frontend-Bundle ohne Aufteilung: alle Vuetify-Komponenten und alle Seiten werden eager geladen. | `vuetify.ts:4-5`; `router.ts:3-10`; Messung oben | 444 kB gzip JS + 154 kB CSS beim ersten Aufruf, vor allem auf Telefonen spürbar; für den Pi selbst unerheblich (nginx liefert statisch). | niedrig | Lazy-Routen; Vuetify-Komponenten gezielt importieren. Vorher/Nachher mit `vite build` messen. | S | nachgewiesen (gemessen) |
| W11 | Testlücken an kritischen Stellen: `JointBatteryHardware` (MQTT-Rücklesen, AC-Out-Last) ohne Test; Ersparnis-Neuberechnung bei Teildaten; Zeitumstellung in Ersparnis und Joint-Verbrauchsprofil; kein HTTP-Pipeline-Test; kein Schema-Test; Frontend-Logik (`zentraleModel.ts`) ungetestet. | Zuordnung im Prüfprotokoll; `JointBatteryHardware.cs:107-145` | Regressionen an Grenzen bleiben unbemerkt. | mittel | Tests gemäß Schritt D0. | M | nachgewiesen |
| W12 | Lokale Arbeitskopie enthält ignorierte Kopien: `AI-FIRST/` mit eigener `CLAUDE.md`, `docs/`, `src/`; Alt-Projekt im Wurzelverzeichnis. | `.gitignore:44-60`; lokales Verzeichnislisting | Werkzeuge ohne `.gitignore`-Beachtung finden falsche Anweisungen und Duplikate. | niedrig | Nach Rückfrage verschieben oder löschen. | S | nachgewiesen (lokal) |
| W13 | Zeitzone an rund 30 Stellen aufgelöst, teils fest `Europe/Berlin`, teils aus `consumptionForecast.timeZone`, teils mit Windows-Fallback. | `BatterySavingsAccountingService.cs:413`; `EnergyCostReportService.cs:12`; `JointBatteryShadowCalculation.cs:42,147`; `WeishauptHotWaterPricePlanner.cs:8` | Weicht die Einstellung ab, rechnen Berichte unterschiedliche Tage. | niedrig | Eine `ReportingTimeZone`-Quelle in Business, beim Anfassen umstellen. | S | nachgewiesen |

---

## C. Entscheidung zum Umbau

**Gezielte Verbesserungen genügen für fast alles. Ein struktureller Umbau ist für genau ein Modul gerechtfertigt:
die gemeinsame Akku-Steuerung (Planung und Taktauswahl). Ein größerer Umbau oder Neubau ist nicht gerechtfertigt.**

Begründung:

- **Kein Neubau:** Schichtgrenze, Fail-closed-Ausführung und Testbasis sind tragfähig (Abschnitt A, Stärken). Die
  Probleme sind lokal benennbar und einzeln behebbar; kein Befund zeigt ein grundsätzlich falsches Modell.
- **Struktureller Umbau Joint-Modul:** K1, K7, K8, W1 und W2 haben dieselbe Ursache – der aktive Pfad ist aus einem
  „Vorschau“-Prototyp gewachsen und liegt verteilt in Api. Einzelreparaturen (noch ein Cache, noch eine Ausnahme) würden
  das Muster „aufeinander aufbauende Reparaturen“ fortsetzen. Ziel ist: Planung und Taktauswahl als prüfbare
  Business-Funktionen, Api nur noch Zeitsteuerung, Hardware und Protokollierung. Das Verhalten bleibt dabei gleich;
  fachliche Änderungen (K1, K2) folgen getrennt danach.
- **Additive Datenmodell-Änderung:** K5/K6 brauchen eine eigene Preistabelle. Das ist kein Umbau, sondern eine neue
  Tabelle mit schrittweiser Umstellung der Verbraucher.
- **Gezielt:** K3, K4, K9–K13 und alle W-Befunde außer W2 sind kleine, einzeln prüfbare Änderungen.

---

## D. Schrittweiser Umbauplan

Reihenfolge nach Abhängigkeiten. Strukturänderungen (S) und Verhaltensänderungen (V) sind getrennt; jeder Schritt
ist ein eigener Commit bzw. eine kleine Commit-Folge. Fachliche Invarianten, die **in jedem Schritt** gelten:

- I1 Keine Entladung zur Einspeisung; Summe der Entladeleistung ≤ gemessener Hausbedarf.
- I2 Reserve je Speicher; kein gemeinsamer Prozent-Ladestand.
- I3 Keine gegenläufigen Sollwerte; Richtungswechsel nur nach zwei neutralen Messungen.
- I4 Abgelaufene Messwerte oder Sollwerte → keine Leistung (fail closed).
- I5 Netzladung nur auf veröffentlichten Preisen.
- I6 Wiederherstellungspflicht wird vor dem ersten Schreibbefehl persistiert; Rücknahme zuerst Victron, dann E3/DC.
- I7 Gerätschalter (`victron.dryRun`, `jointBattery.executionEnabled`, `evCharging.controlEnabled`, `weishauptHotWater.enabled`).
- I8 Gespeicherte Historie wird durch Neuberechnung nie unvollständiger.

### D0 – Verhalten absichern (nur Tests) · S · Aufwand M · Risiko gering

- **Problem/Ziel:** Joint-Planer, Taktauswahl und Bilanzierung sind an den entscheidenden Grenzen nicht festgehalten (W11).
- **Module:** `tests/TibberVictronController.Business.Tests`, `tests/TibberVictronController.Api.Tests`, `tests/TibberVictronController.Dal.Tests`.
- **Änderung:** Charakterisierungstests für den **heutigen** Stand:
  - `JointBatteryPlanner`: Negativpreis-Fenster, kleine Preisspreizung (heute: Halten), geschätzte Preise, fehlende PV, Planungsmaximum.
  - Taktauswahl über `JointBatteryExecutionBackgroundService.TickAsync`: Festschreibung, gleichgerichtete Aktualisierung,
    PV-Ersatz, veralteter Plan → Notbetrieb, Handladung endet → Plan.
  - `JointBatteryHardware` mit Fake-MQTT-Client: Rücklesen, fehlende AC-Out-Last, Zeitüberschreitung.
  - Ersparnis: Neuberechnung mit Teildaten (dokumentiert K3, zunächst als erwartetes heutiges Verhalten).
  - Schema-Abgleich frische vs. aktualisierte Datenbank (W6).
- **Abhängigkeiten:** keine.
- **Abnahme:** neue Tests grün ohne Produktänderung; Laufzeit der Suite steigt um < 5 s.

### D1 – Orientierung korrigieren (nur Doku) · S · Aufwand S · Risiko gering

- **Problem/Ziel:** K12, W1 (Doku-Teil), W4, K5-Hinweis.
- **Module:** `AGENTS.md`, `docs/*` (siehe Abschnitt E), `README.md`.
- **Änderung:** Ist-Zustand der gemeinsamen Steuerung vom Betreiber bestätigen lassen und an einer Stelle festhalten;
  „Shadow = produktiver Planer“ notieren; Regeln in `battery-decision-engine.md` je Pfad kennzeichnen; Widersprüche aus W4 beheben.
- **Abhängigkeiten:** Antwort auf offene Fragen 1 und 2.
- **Abnahme:** jede in `AGENTS.md` genannte Klasse existiert; keine zwei Dokumente widersprechen sich zu Betriebszustand, Aufbewahrung, Prüfläufen.

### D2 – Ersparnis-Neuberechnung absichern · V · Aufwand S · Risiko gering

- **Problem/Ziel:** K3.
- **Module:** `BatterySavingsAccountingService`, `EfBatterySavingsRepository`, `SavingsEndpoints`.
- **Änderung:** Tageswert nur ersetzen, wenn `MeasuredSeconds` nicht sinkt; Zeitraum auf Tage mit Rohdaten begrenzen; `catch` loggt.
- **Invarianten:** I8; Ersparnisformel unverändert.
- **Tests/Abnahme:** D0-Test zu Teildaten wird umgestellt auf „überschreibt nicht“; vollständiger Tag wird weiterhin ersetzt.
- **Rollback:** Commit zurücknehmen; keine Schemaänderung.

### D3 – Planungslast senken · S · Aufwand S–M · Risiko gering bis mittel

- **Problem/Ziel:** K7. Ziel: Ladezeit der Planungseingaben auf dem Pi < 10 s, Planalter bei Nutzung < 45 s.
- **Module:** `JointBatteryShadowCalculation`, `JointConsumptionProfile`, ggf. `EfLiveConsumptionRepository`.
- **Änderung:** Vorher messen (bestehende Warnung „Laden der Prognosedaten dauerte …“ auswerten, Betreiber). Dann gelerntes
  Profil stündlich zwischenspeichern (Muster `PvForecastLearning.GetAsync`) oder Viertelstunden-Mittel per SQL liefern.
- **Invarianten:** Profilwerte identisch (Test: gleiches Ergebnis mit und ohne Cache bzw. SQL-Aggregat gegen bisherige Berechnung).
- **Abnahme:** Testgleichheit; Betreiber misst Ladezeit vorher/nachher.
- **Rollback:** Commit zurücknehmen.

### D4 – Einstellungs-Snapshot für die Sicherheitsschleife · S · Aufwand M · Risiko mittel

- **Problem/Ziel:** K8, W7.
- **Module:** `JointBatteryExecutionBackgroundService`, `JointBatteryHardware`, `E3dcLimitPolicy`, Settings-Leser.
- **Änderung:** Ein typisierter, je Takt einmal gelesener Snapshot (oder Cache mit Invalidierung bei `SaveSettingAsync`);
  Entschlüsselung einmal je Änderung.
- **Invarianten:** I4, I6, I7 – insbesondere: ein Ausschalten von `jointBattery.executionEnabled` oder `victron.dryRun` wirkt spätestens im nächsten Takt.
- **Tests/Abnahme:** zählender Store-Fake: höchstens ein Lesevorgang je Schlüssel und Takt; Test „Schalter aus wirkt im nächsten Takt“.
- **Rollback:** Commit zurücknehmen. Hardwareabnahme durch den Betreiber (Ein/Aus der Ausführung im laufenden Betrieb).

### D5 – Umbenennen „Shadow“ → „Planning“ · S · Aufwand M · Risiko gering

- **Problem/Ziel:** W1.
- **Module:** `Api/JointBattery/*Shadow*`, DTOs, Frontend-Typen, Statuswert.
- **Änderung:** Klassen umbenennen; Route `/api/joint-battery/shadow` als Alias behalten; Statustext nennt, ob die Ausführung den Plan nutzt. Statuswert `Shadow` nur noch für „Ausführung aus“.
- **Abhängigkeiten:** D0.
- **Abnahme:** alle Tests unverändert grün (nur Namen); `vue-tsc` grün; alte Route liefert dasselbe.

### D6 – Joint-Ablaufsteuerung nach Business · S · Aufwand M · Risiko mittel

- **Problem/Ziel:** W2.
- **Module:** neu `Business/Decisions/JointBatteryTickSelector` (reine Funktion: Eingaben Handladung, Festschreibung, Plan, Uhr → Befehl + Ereignisse); `Business`-Dienst für die Eingabeaufbereitung; Api ruft nur noch auf.
- **Abhängigkeiten:** D0, D5.
- **Invarianten:** I1–I7; Notbetrieb ohne Plan bleibt Eigenverbrauch ohne Netzladung.
- **Tests/Abnahme:** D0-Tests laufen **unverändert** grün; neue Business-Tests ohne DI; `JointBatteryExecutionBackgroundService` < 200 Zeilen.
- **Rollback:** Commit-Folge zurücknehmen.

### D7 – Wirtschaftsregeln angleichen · V · Aufwand M · Risiko mittel

- **Problem/Ziel:** K1, K2 nach Betreiberentscheidung (offene Fragen 3, 4).
- **Module:** `JointBatteryPlanner`, `JointPlanInputFallback`, Doku.
- **Änderung:** je Regel ein Commit (Halte-Hürde, Negativpreis/Planungsmaximum, Verschleiß-Gewichtung, fehlende PV).
- **Abhängigkeiten:** D0, D6.
- **Tests/Abnahme:** D0-Golden-Szenarien ändern sich nur dort, wo die Regel es verlangt; Differenzen im Commit beschrieben. Rechenzeit des Planers nicht > 20 % höher (Stoppuhr im Test).
- **Rollback:** Commit je Regel zurücknehmen. Betreiber beobachtet die Pläne mindestens einen Tag mit Negativ- und Spreizungspreisen.

### D8 – Eigenes Preisarchiv, danach Aufbewahrung · V · Aufwand M · Risiko mittel

- **Problem/Ziel:** K5, dann K6.
- **Module:** `Dal/Persistence` (neue Tabelle, EF-Modell **und** `ControllerDbInitializer`), Preis-Abruf, Verbraucher aus K5.
- **Änderung:** (a) Tabelle `PublishedPriceSlots` (Anbieter, Währung, Slotbeginn, Preis, Abrufzeit), befüllt bei jedem erfolgreichen Abruf veröffentlichter Preise; (b) einmalige, idempotente Übernahme aus dem Entscheidungsprotokoll (`INSERT OR IGNORE`); (c) Verbraucher einzeln umstellen; (d) erst danach Aufbewahrung für `DecisionLogEntries` und `OperationalEvents` aktivieren.
- **Invarianten:** I8; Stromkosten bisheriger Tage bleiben gleich (Test: Bericht vor/nach Umstellung identisch).
- **Migration/Rollback:** rein additiv; alte Lesepfade bleiben bis (d) vorhanden. Vor (d) Tabellen-Dump nach Betreiberregel (keine Vollsicherung im laufenden Betrieb).

### D9 – Fehlerweitergabe vereinheitlichen · V · Aufwand S · Risiko gering

- **Problem/Ziel:** K9. **Module:** `FileExceptionLoggingMiddleware`, `HealthEndpoints`, `DecisionExecutionBackgroundService`, `DashboardPage.vue`.
- **Abnahme:** Tests: 500er enthält nur `message` und `traceId`; `/health` enthält keine Ausnahmetexte.

### D10 – Deploy-Gate und Rollback · Betrieb · Aufwand M · Risiko mittel (nur mit Freigabe)

- **Problem/Ziel:** K10. **Module:** `.github/workflows/ci.yml`, `scripts/deploy/*`.
- **Änderung:** CI setzt nach grünem Lauf `deploy/main`; Pi verfolgt diesen Branch; Release-Verzeichnisse mit Symlink; Healthcheck-Fehler schaltet zurück.
- **Abnahme:** absichtlich roter Commit erreicht den Pi nicht; simulierter Healthcheck-Fehler stellt das vorherige Release her (auf dem Pi durch den Betreiber).

### D11 – Health um gemeinsame Ausführung ergänzen · V · Aufwand S · Risiko gering

- **Problem/Ziel:** K13. **Abhängigkeit:** sinnvoll vor D10 (das Gate nutzt `/health`).

### Optional, jeweils einzeln

K4 (Ladequelle projizieren, S), K11 (Ausnahme dokumentieren, S), W5 (DbSet-Name, S), W8 (Designentscheidung), W9 (ein HTTP-Client), W10 (Bundle), W13 (Zeitzone), F08 (DI je Feature), F18 (Timeouts).

---

## E. Verbesserungen für die Arbeit mit Coding-Agenten

Eine Regel, ein Ablageort. `AGENTS.md` bleibt die einzige Quelle für Arbeitsregeln; `CLAUDE.md` verweist nur darauf.

| Regel / Inhalt | Ablageort | Anlass |
|---|---|---|
| Betriebszustand der gemeinsamen Steuerung (aktiv ja/nein, seit wann, welche Schalter gesetzt) | `docs/30-operations/e3dc-rscp.md`, Abschnitt „Inbetriebnahme“; `AGENTS.md` verweist mit einem Satz | K12 |
| „`JointBatteryShadow*` ist der produktive Planer“ (bis D5) | `AGENTS.md`, Abschnitt *Battery decision paths* | W1 |
| „Zwei Optimierer: jede Regeländerung nennt den Pfad; Joint ist im Betrieb maßgeblich“ | `AGENTS.md`, *Battery decision paths*; Regeln je Pfad in `battery-decision-engine.md` | K1, W3 |
| „Entscheidungsprotokoll ist Preisarchiv; Änderungen an Häufigkeit/Inhalt nur mit Prüfung der Verbraucher (Liste)“ | `AGENTS.md`, *Rules*; Liste in `persistence-model.md` | K5 |
| „Neuberechnung darf gespeicherte Historie nie verschlechtern“ | `AGENTS.md`, *Checks by change type* (Database) | K3, I8 |
| Inventar der Hardware-Schreibpfade (Adapter, Aufrufer, Schalter, Standard, Rücknahme, Ausnahmen wie K11) | neu `docs/30-operations/hardware-write-paths.md`; `AGENTS.md:48` verweist | F04, K11 |
| Modulkarte je Fachbereich (Einstiegspunkte je Schicht) | `docs/10-architecture/project-structure.md` ersetzen (Tabelle aus dem Vorbericht, aktualisiert) | Auffindbarkeit |
| Betriebshandbuch (Dienst, Pfade ohne Geheimnisse, Sicherungsregel, Schlüsselbund, Rollback, Prüfschritte nach Update) | neu `docs/30-operations/operations-runbook.md` | F15 |
| Lieferregeln (Prüfläufe, Stoppen bei Fehlern) | nur `AGENTS.md`; `working-agreement.md` Abschnitt *Delivery Rules* auf Verweis kürzen | W4 |
| Doku-Sprache | `AGENTS.md`, *Rules* (eine Zeile, Bestand nicht umschreiben) | F21 |

**Prüfkommandos für Agenten** (in `AGENTS.md`, *Commands*, ergänzen):

| Zweck | Befehl | Dauer (hier gemessen bzw. geschätzt) |
|---|---|---|
| Schnell während einer Joint-Änderung | `dotnet test TibberVictronController.slnx --filter "FullyQualifiedName~JointBattery"` | Sekunden |
| Schnell bei Bilanz/Kosten | `… --filter "FullyQualifiedName~Savings\|FullyQualifiedName~EnergyHistory\|FullyQualifiedName~EnergyCost"` | Sekunden |
| Schnell bei Persistenz | `… --filter "FullyQualifiedName~Sqlite\|FullyQualifiedName~Persistence"` | Sekunden |
| Vor Übergabe/Merge, Backend | `dotnet test TibberVictronController.slnx` | ≈ 17 s (frischer Klon) |
| Vor Übergabe/Merge, Frontend | `npx vue-tsc --noEmit` + Node-Test + `npx vite build --outDir <temp>` | ≈ 20 s |

**CI** (`.github/workflows/ci.yml`): `vite build` in einen temporären Ordner ergänzen (der Pi baut sonst als Erster);
Schema-Abgleichtest (D0) läuft automatisch mit; optional ein Test, der `DateTimeOffset.UtcNow`/`DateTime.UtcNow` in
`src/TibberVictronController.Business` verbietet (F11). Keine neuen Linter-Regelwerke ohne Anlass.

**Arbeitskopie:** ignorierte Kopien (`AI-FIRST/`, Alt-Projekt im Wurzelverzeichnis) nach Rückfrage entfernen (W12).

---

## F. Empfohlener Einstieg

1. **Orientierung korrigieren (D1, Aufwand S).** Betriebszustand der gemeinsamen Steuerung bestätigen und an einer
   Stelle festhalten, „Shadow = produktiver Planer“, Regel-Zuordnung je Optimierer, Preisarchiv-Hinweis, Widersprüche
   aus W4. Größter Nutzen pro Aufwand: verhindert, dass Agenten an der falschen Stelle ändern oder Hardware-Relevanz unterschätzen.
2. **Planungslast messen und senken (D3, Aufwand S–M).** Betrifft den laufenden Betrieb mit nur rund 15 s Reserve bis
   zum Notbetrieb ohne Netzladung; die Änderung ist strukturell und durch Gleichheitstests absicherbar.
3. **Ersparnis-Neuberechnung absichern (D2, Aufwand S).** Verhindert stillen, unumkehrbaren Verlust gespeicherter
   Tageswerte; kleine Änderung nach bewährtem Muster aus `EnergyHistoryService`.

Danach D0 (Absicherung) als Voraussetzung für D5–D7, und D11 + D10 (Health, Deploy-Gate) mit Betreiberfreigabe.

---

## Offene Fragen an den Betreiber

1. Ist `jointBattery.executionEnabled=true` auf dem Pi gesetzt, und seit wann? (K12)
2. Welche Einstellung hat `telemetry.liveSampleRetentionDays` auf dem Pi (14 oder 60)? Wie viele `LiveConsumptionSamples` entstehen pro Tag? (K3, K7) – nur lesende Abfrage, nach Freigabe.
3. Soll der Einzelakku-Pfad unterstützter Rückfall bleiben oder eingefroren werden? (W3)
4. Welche Regeln sollen im Joint-Betrieb gelten: Halte-Hürde, Negativpreis-Vollladung über das Planungsmaximum, eigener Verschleißwert für den E3/DC? (K1)
5. Ist die manuelle Wallbox-Grenze ohne `evCharging.controlEnabled` gewollt? (K11)
6. Welchen Branch verfolgt der Cron auf dem Pi? (K10)
7. Welche Dashboard-Designs werden weiter gepflegt? (W8)
8. Dürfen `AI-FIRST/` und das Alt-Projekt im lokalen Wurzelverzeichnis entfernt werden? (W12)
