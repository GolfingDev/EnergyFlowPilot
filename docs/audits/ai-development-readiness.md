# Audit: Eignung für KI-gestützte Weiterentwicklung

Stand: 2026-11-08, Commit `2029f15` (Branch `main`, Arbeitsverzeichnis sauber).
Art: Audit ohne Änderungen am Anwendungscode. Einzige neue Datei: dieser Bericht.

Begriffe: **bestätigt** = durch Ausführung oder eindeutigen Code-/Git-Befund belegt;
**noch zu prüfen** = plausibel, aber nicht vollständig nachgewiesen (meist Laufzeit auf dem Pi).

---

## A. Kurzbewertung

### Gesamturteil

**Bedingt gut geeignet.** Der Kern (Business-Schicht, Tests, Fachdokumentation) ist für Agenten
gut bearbeitbar: Business hat keine Framework-Abhängigkeiten, 791 Tests laufen lokal in rund
15 Sekunden ohne Hardware, und die Fachregeln sind ausführlich mit Begründung dokumentiert.
Schwach sind die **Einstiegspunkte** für einen Agenten: dokumentierte Befehle funktionieren nicht,
die Agentenanweisungen beschreiben Klassen, die es nicht gibt oder die nicht laufen, und es gibt
keine CI, obwohl ein Push über das Auto-Update reale Hardware erreicht.

### Stärken, die erhalten bleiben sollten

1. **Compiler-erzwungene Schichtgrenze.** `Business.csproj` hat keine Paket- oder
   Projektreferenzen; Business kann Api/Dal nicht referenzieren.
2. **Tests ohne Hardware.** Keine Sockets, MQTT-Clients oder echten HTTP-Aufrufe in Tests
   (Ausnahme: siehe F10). Hardwarezugriffe liegen hinter Schnittstellen mit Fakes.
3. **Sichere Voreinstellungen.** `victron.dryRun=true`, `jointBattery.executionEnabled=false`,
   `evCharging.controlEnabled` und `weishauptHotWater.enabled` standardmäßig aus;
   Wiederherstellungspflicht wird persistiert; systemd-Watchdog und `--joint-stop`-Helfer.
4. **Erklärbare Entscheidungen.** Regel-IDs und Gründe werden persistiert; GUI-Metadaten
   beschreiben Regeln.
5. **Authentifizierung zentral.** Fallback-Policy für alle Endpunkte, zentraler
   `AdminWriteGuardMiddleware`, Startabbruch bei halber Entra-Konfiguration.
6. **Kommentare mit Begründung und Datum** (z. B. `MqttVictronSetpointPublisher.cs:14-16`).
   Das hilft Agenten, Absichten nicht versehentlich zurückzudrehen.
7. **Fachlich benannte Ordner in allen Schichten** (`EvCharging`, `JointBattery`, `Weishaupt`,
   `Homematic`, `Energy`) – faktisch schon Vertical Slices über Schichtgrenzen hinweg.
8. **Deploy-Skript mit Staging, Sperrdatei, `--ff-only` und Prüfung auf lokale Änderungen.**
9. **Kleine, gut beschriebene Commits.**

### Die drei Hindernisse mit der größten praktischen Wirkung

1. **Falsche Orientierung durch veraltete Anweisungen und toten Code (F03, F06).**
   `CLAUDE.md` nennt `ITimeProvider` (existiert nicht) und `MqttTelemetryBackgroundService`
   (nicht registriert, läuft nie). Der Commit `18f4a75` von heute hat genau diese nicht
   registrierten Dienste mit geändert.
2. **Kein reproduzierbarer Prüfpfad (F01, F02).** `dotnet build`/`dotnet test` laut
   Anweisung schlagen fehl; die Solution-Datei ist nicht versioniert; ein Test hängt von ihr ab;
   es gibt keine CI. Ein Push nach `main` kann über das Auto-Update ungeprüft auf den Pi gehen.
3. **Hardware-Schreibschutz ist verteilt statt zentral (F04).** Vier verschiedene Schalter,
   der DryRun-Check liegt bei sechs Aufrufern statt im Adapter, und eine lokale Instanz mit
   konfigurierter Geräteadresse verbindet sich mit echter Hardware.

### Nicht geprüft

- Laufzeitzustand auf dem Raspberry Pi (Cron-Eintrag, verfolgter Branch, Dateirechte, Datenbankinhalt,
  nginx/Tailscale). Kein Zugriff laut Auftrag.
- Reales Geräteverhalten (Victron, E3/DC, Wallbox, Weishaupt, Homematic).
- Fachliche Korrektheit der Optimierer (dynamische Programmierung, Ersparnisrechnung) über die
  vorhandenen Tests hinaus.
- Frontend im Browser, End-to-End-Abläufe, Barrierefreiheit.
- Protokollimplementierungen (RSCP, OCPP, Modbus) im Detail.
- `dotnet build` in einem frischen Klon (statisch hergeleitet, siehe F01).
- `npm run build` (schreibt `dist/`), `dotnet run` (würde sich mit Geräten verbinden).

---

## B. Priorisierte Befunde

Keine Befunde der Stufe **kritisch**. Die Sicherheitsmechanismen für Hardware greifen nach
heutigem Stand; die Risiken unten betreffen künftige Änderungen und den Prüfpfad.

| ID | Priorität | Kurztitel | Sicherheit |
|---|---|---|---|
| F01 | hoch | Dokumentierte Build-/Testbefehle schlagen fehl | bestätigt |
| F02 | hoch | Keine CI; Auto-Deploy auf Hardware-Host ohne Tests und ohne Rollback | bestätigt (Repo), Pi noch zu prüfen |
| F03 | hoch | Agentenanweisungen veraltet und teils falsch | bestätigt |
| F04 | hoch | Hardware-Schreibschutz verteilt; kein lokaler Sicherheitsmodus | bestätigt |
| F05 | hoch | Schemaentwicklung ohne Migrationen, Doku widerspricht Code | bestätigt |
| F06 | hoch | Tote und parallele Implementierungen | bestätigt |
| F07 | mittel | Aktiver Entscheidungspfad wird zur Laufzeit in der Api-Schicht gewählt | bestätigt |
| F08 | mittel | Zentrale DI-Registrierung und Sammelordner `Api/Configuration` | bestätigt |
| F09 | mittel | Nur ausgewählte Geheimnisse verschlüsselt; README behauptet anderes | bestätigt (Code) |
| F10 | mittel | Live-Tests laufen implizit gegen Tibber, wenn eine Umgebungsvariable gesetzt ist | bestätigt |
| F11 | mittel | Systemzeit direkt statt `IUtcClock`, auch in Business | bestätigt |
| F12 | mittel | Frontend-Verträge und Validierung von Hand gespiegelt | bestätigt |
| F13 | mittel | Einstellungstabelle als allgemeiner Zustandsspeicher, stringbasiert | bestätigt |
| F14 | mittel | Viele parallele Dashboard-Varianten im Frontend | bestätigt |
| F15 | mittel | Betriebswissen (Pi, Backup) nur in KI-Anbieter-spezifischem Speicher | bestätigt |
| F16 | niedrig | Healthcheck deckt den aktiven gemeinsamen Steuerpfad nicht ab | noch zu prüfen |
| F17 | niedrig | Keine Formatierungs-/Analyseregeln; vorhandene Warnungen | bestätigt |
| F18 | niedrig | HttpClients ohne explizites Timeout | bestätigt |
| F19 | niedrig | Endpunkt-Tests umgehen die HTTP-Pipeline | bestätigt |
| F20 | niedrig | Doppelte Test-Doubles | bestätigt |
| F21 | niedrig (Stil) | Gemischte Doku-Sprache, Umlaut-Umschreibungen | bestätigt |

---

### F01 – Dokumentierte Build-/Testbefehle schlagen fehl

- **Priorität:** hoch · **Sicherheit:** bestätigt
- **Fundstellen:** `CLAUDE.md:14-17`, `README.md:157-170`, `.gitignore:58-60`,
  `tests/TibberVictronController.Business.Tests/DecisionAuditArtifactExportTests.cs:46`
- **Beobachtung:**
  - `dotnet build` im Repository-Wurzelverzeichnis endet mit
    `MSB1011: … mehrere Projekte oder Projektmappendateien` (ausgeführt). Ursache: lokal liegen im
    Wurzelverzeichnis die ignorierten Altdateien `TibberVictronController.Web.csproj`,
    `TibberVictronController.Web.sln` und `TibberVictronController.slnx`.
  - Versioniert ist **keine** Solution-Datei (`git ls-files` enthält weder `.sln` noch `.slnx`);
    `.gitignore` schließt `/*.slnx` aus. In einem frischen Klon findet `dotnet build` also gar kein
    Projekt.
  - `DecisionAuditArtifactExportTests` sucht das Repository-Wurzelverzeichnis über die Datei
    `TibberVictronController.slnx`. In einem frischen Klon (z. B. CI) wirft der Test
    `DirectoryNotFoundException`.
- **Auswirkung:** Ein Agent, der den Anweisungen folgt, scheitert sofort oder baut zufällig das
  Legacy-Projekt. CI ist ohne Korrektur nicht einrichtbar.
- **Empfehlung:** `TibberVictronController.slnx` versionieren (Ausnahme in `.gitignore`), Befehle in
  den Anweisungen explizit mit `TibberVictronController.slnx` angeben, Wurzelerkennung im Test auf
  eine versionierte Datei stützen. Die ignorierten Altdateien im lokalen Wurzelverzeichnis nur nach
  Rücksprache entfernen.
- **Aufwand/Risiko:** klein / gering.

### F02 – Keine CI; Auto-Deploy auf den Hardware-Host ohne Tests und ohne Rollback

- **Priorität:** hoch · **Sicherheit:** bestätigt für das Repository; verfolgter Branch auf dem Pi noch zu prüfen
- **Fundstellen:** `.github/workflows/` (leer, nichts versioniert), `scripts/deploy/update-linux.sh:342-354`
  (`main`), `:351` (`wait_for_healthcheck || true`), `scripts/deploy/auto-update-linux.sh:19`
  (`GIT_BRANCH` Standard `develop`), `docs/30-operations/security-and-deployment.md:44-56`
- **Beobachtung:**
  - Das Update-Skript führt `pull --ff-only`, `dotnet publish`, `npm run build`, Dateiaustausch und
    Neustart aus – **keine Tests**. Ein fehlgeschlagener Healthcheck wird nur gemeldet; das vorherige
    Release wird per `rsync --delete` überschrieben, es gibt keinen automatischen Rückweg.
  - Kompilierfehler sind abgefangen (`set -e` vor dem Dateiaustausch); Logikfehler nicht.
  - Gearbeitet wird direkt auf `main`; `origin/develop` steht seit 2026-10-19 still
    (98 Commits hinter `main`), `origin/master` seit 2026-06-29. Skripte und Doku nennen weiterhin
    `develop` als Beispiel und Standard.
- **Auswirkung:** Ein Agent, der auf Wunsch pusht, kann ungeprüften Code auf ein System bringen,
  das Batterien und Wallbox steuert. Veraltete Branches verwirren bei der Frage „wohin“.
- **Empfehlung:** (1) GitHub-Actions-Workflow mit Backend-Tests und Frontend-Typprüfung.
  (2) Später: Auto-Update nur von einem Stand, den CI freigegeben hat (z. B. Branch oder Tag, den
  der Workflow nach grünem Lauf setzt), und vorheriges Release als Verzeichnis behalten. (3) Doku
  und Skript-Standard auf den tatsächlich verwendeten Branch anpassen, veraltete Branches nach
  Rücksprache löschen.
- **Aufwand/Risiko:** CI klein / gering; Deploy-Gate mittel / mittel (Betriebsänderung).

### F03 – Agentenanweisungen veraltet und teils falsch

- **Priorität:** hoch · **Sicherheit:** bestätigt
- **Fundstellen und Belege:**

  | Aussage | Fundstelle | Tatsächlich |
  |---|---|---|
  | `ITimeProvider` wird überall injiziert | `CLAUDE.md:65` | Schnittstelle heißt `IUtcClock` (`Business/Abstractions/IUtcClock.cs`) |
  | `MqttTelemetryBackgroundService` liest Telemetrie | `CLAUDE.md:48` | Nicht registriert; Telemetrie liefert `VictronMqttClientService` (`ControllerServiceCollectionExtensions.cs:272`) |
  | Drei Hintergrunddienste | `CLAUDE.md:45-49` | 16 `AddHostedService`-Registrierungen |
  | Victron-MQTT-Adapter liegt in Dal | `CLAUDE.md:42` | Client und Setpoint-Publisher liegen in `Api/Configuration/` |
  | Zwei Entscheidungspfade | `CLAUDE.md:51-58` | Dritter, im Betrieb aktiver Pfad „gemeinsame Steuerung“ (`Api/JointBattery/`, `Business/Decisions/JointBattery*`) fehlt |
  | `dotnet build` / `dotnet test` | `CLAUDE.md:14-17` | Schlägt fehl (F01) |
  | API auf Port 5000 | `README.md:160` | `launchSettings.json` und Vite-Proxy nutzen 5094 |
  | „Codex acts as …“ | `docs/00-governance/working-agreement.md:5` | Anbieterbezogen und veraltet |
  | Leseliste | `docs/README.md:7-22` | 8 von 22 Dokumenten fehlen (u. a. `persistence-model.md`, `e3dc-rscp.md`, `ev-charging.md`, `authentication-and-remote-access.md`) |
  | „Dal: migrations“ | `docs/10-architecture/project-structure.md:21` | Keine Migrationen (F05) |

  Frontend-Prüfbefehle stehen nur versteckt in `docs/30-operations/configuration-and-access-data.md:31`.
  Es gibt keine `AGENTS.md`; die Regeln sind auf `CLAUDE.md` (Claude-spezifisch) und
  `working-agreement.md` (Codex-spezifisch) verteilt.
- **Praktischer Nachweis:** Commit `18f4a75` (2026-11-08) ändert
  `MqttTelemetryBackgroundService.cs` und `VictronMqttTelemetryBackgroundService.cs`, obwohl beide
  nie gestartet werden.
- **Auswirkung:** Agenten suchen an falschen Stellen, ändern toten Code mit und übersehen den aktiven
  Steuerpfad.
- **Empfehlung:** Eine kurze, anbieterneutrale `AGENTS.md` als einzige Quelle (Vorschlag in Abschnitt E),
  `CLAUDE.md` verweist nur darauf. Jede dort genannte Klasse und jeder Befehl muss existieren bzw.
  einmal erfolgreich gelaufen sein.
- **Aufwand/Risiko:** klein / gering.

### F04 – Hardware-Schreibschutz verteilt; kein lokaler Sicherheitsmodus

- **Priorität:** hoch · **Sicherheit:** bestätigt (Code); Wirkung je Gerät teilweise noch zu prüfen
- **Fundstellen:**
  - `Api/Configuration/MqttVictronSetpointPublisher.cs:44-48` – `PublishAsync` prüft Joint-Ownership,
    aber **nicht** `victron.dryRun`.
  - DryRun wird stattdessen bei den Aufrufern gelesen: `DecisionExecutionBackgroundService.cs:87-95,175-188`,
    `ManualCharge/ManualChargeEndpoints.cs:112-125`, `VictronSetpointRefreshBackgroundService.cs:59`,
    `JointBattery/JointBatteryExecutionBackgroundService.cs:105-107`, `JointBattery/JointManualChargeService.cs:21`,
    `Automations/AutomationEndpoints.cs:56`.
  - Vier Schalter mit unterschiedlicher Logik: `victron.dryRun` (invertiert, Standard true),
    `jointBattery.executionEnabled`, `evCharging.controlEnabled`, `weishauptHotWater.enabled`.
  - Der Endpunkt `POST /api/manual-charge` berechnet eine Entscheidung und schreibt direkt auf die
    Hardware (`ManualChargeEndpoints.cs:82,112-125`).
  - Die Schalter liegen nur in der Datenbank. Es gibt keinen Konfigurationsschalter je Umgebung.
    Die lokale Entwicklungsdatenbank (`src/TibberVictronController.Api/tibber-victron-controller.db`,
    nur lesend geprüft) hat `victron.host` und `mqtt.host` gesetzt, `victron.dryRun=true`.
- **Auswirkung:** Ein neuer Schreibpfad (Agent fügt z. B. eine „Sofort-Entladen“-Aktion hinzu) muss
  den richtigen Schalter selbst kennen. Ein lokales `dotnet run` verbindet sich mit dem echten
  Victron-MQTT-Broker im LAN, parallel zum Pi; mit einer Kopie der Produktivdatenbank wären auch
  Schreibpfade aktiv.
- **Empfehlung:** (1) Sofort: Tabelle aller Hardware-Schreibpfade mit Schalter, Standardwert und
  Adapter in die Doku und in `AGENTS.md`. (2) Strukturell: ein zentrales Schreib-Gate im Adapter
  (Victron-Publisher, E3/DC-Leistungssteuerung, Weishaupt-Modbus, OCPP-Ladeprofil), zusätzlich ein
  Konfigurationsschalter (z. B. `Hardware:WritesEnabled`, in `appsettings.Development.json` aus),
  den die Datenbank nicht überstimmen kann. Mit Tests je Adapter.
- **Aufwand/Risiko:** Doku klein / gering; Gate mittel / mittel (berührt Sicherheitslogik, braucht
  Hardware-Abnahme).

### F05 – Schemaentwicklung ohne Migrationen, Doku widerspricht Code

- **Priorität:** hoch · **Sicherheit:** bestätigt
- **Fundstellen:** `Dal/Persistence/ControllerDbInitializer.cs:26-33` (`EnsureCreatedAsync` und
  handgeschriebene `CREATE TABLE IF NOT EXISTS`/`ALTER TABLE ADD COLUMN`), `:39-520`;
  `src/TibberVictronController.Api/dotnet-tools.json` (`dotnet-ef` 10.0.7);
  `docs/10-architecture/project-structure.md:21`; `docs/30-operations/persistence-model.md`
  (beschreibt 6 Tabellen, das Initialisierungsskript legt über 20 an).
- **Beobachtung:** Das Schema entsteht aus dem EF-Modell (neue Datenbank) **und** aus Roh-SQL
  (bestehende Datenbank). Beide müssen von Hand übereinstimmen. Es gibt Einzeltests für einige
  nachträglich angelegte Tabellen (`SqlitePersistenceTests.cs:263,298`), aber keinen generellen
  Abgleich. Das Tool `dotnet-ef` ist installiert, Migrationen werden nicht genutzt.
- **Auswirkung:** Ein Agent ergänzt eine Spalte nur im EF-Modell → neue Datenbanken funktionieren,
  der Pi mit Bestandsdatenbank scheitert zur Laufzeit. Oder er erzeugt mit `dotnet ef migrations add`
  Migrationen, die mit `EnsureCreated` unverträglich sind.
- **Empfehlung:** Vorgehen in `persistence-model.md` und `AGENTS.md` festhalten („nur additive
  Änderungen; jede Tabelle/Spalte auch im Initialisierer; kein `dotnet ef migrations`“). Einen
  Abgleichtest ergänzen: Schema einer frisch erzeugten Datenbank gegen das Schema einer
  aktualisierten Altdatenbank (`PRAGMA table_info`). `dotnet-ef` entfernen oder Zweck dokumentieren.
- **Aufwand/Risiko:** Doku klein / gering; Test klein bis mittel / gering.

### F06 – Tote und parallele Implementierungen

- **Priorität:** hoch · **Sicherheit:** bestätigt (Referenzsuche über `src/`)
- **Fundstellen:**
  - `Api/Configuration/MqttTelemetryBackgroundService.cs` (366 Zeilen) – nicht registriert; nur eine
    statische Methode wird in `MqttTelemetryBackgroundServiceTests.cs:57` genutzt.
  - `Api/Configuration/VictronMqttTelemetryBackgroundService.cs` (144 Zeilen) – nicht registriert, nirgends referenziert.
  - `Dal/Victron/VictronBatteryStateProvider.cs`, `Dal/Victron/VictronCurrentSiteTelemetryProvider.cs`
    – nur von Tests referenziert; produktiv laufen `Dal/Mqtt/Mqtt*Provider` über `Resilient*`/`Selected*`.
  - `Business/Decisions/BatteryDecisionRuleEvaluator.cs` und `TibberPriceDecisionRule.cs` – nur von
    Tests (u. a. Live-Test, F10) referenziert.
- **Auswirkung:** Doppelte Namen (`Mqtt…`/`Victron…`) für dieselbe Aufgabe; Agenten ändern die
  falsche Kopie (Nachweis in F03); Tests schützen Code, der nicht läuft.
- **Empfehlung:** Je Klasse prüfen und entfernen oder als `[Obsolete]` mit Hinweis markieren.
  Pro Klasse ein eigener Commit, Tests mit entfernen. `BatteryDecisionRuleEvaluator` nur nach
  Rückfrage, falls er als Referenz für das Audit gedacht ist.
- **Aufwand/Risiko:** klein / gering.

### F07 – Aktiver Entscheidungspfad wird zur Laufzeit in der Api-Schicht gewählt

- **Priorität:** mittel · **Sicherheit:** bestätigt
- **Fundstellen:** `Api/JointBattery/JointAwareBatteryDecisionService.cs:14-35`,
  `JointAwareBatteryForecastService.cs`, `JointAwareDashboardSummaryService.cs`,
  `JointBatteryOwnership.cs:9-15`; DI: `ControllerServiceCollectionExtensions.cs:213,227,239`.
- **Beobachtung:** Ist `jointBattery.executionEnabled` oder `jointBattery.recoveryPending` gesetzt,
  wird `CurrentBatteryDecisionService` umgangen; der Dekorator in der Api-Schicht baut die
  Entscheidung aus dem Laufzeitzustand selbst zusammen (Fachlogik in Api, Service-Locator über
  `IServiceProvider`). Laut `docs/40-delivery/backlog.md` (Abschnitt Zentrale, Punkt 8) ist die
  gemeinsame Steuerung im Betrieb „always on“.
- **Auswirkung:** Eine Änderung „am Entscheidungsalgorithmus“ in `CurrentBatteryDecisionService`
  kann im Betrieb wirkungslos sein. Ohne Hinweis bemerkt ein Agent das nicht.
- **Empfehlung:** Kurzfristig in `AGENTS.md` eine Karte „welcher Pfad ist wann aktiv“. Langfristig
  die Projektion (Entscheidung aus Ausführungszustand) als reine Funktion nach Business verschieben
  und testen; der Dekorator bleibt dünn.
- **Aufwand/Risiko:** Doku klein / gering; Verschiebung mittel / mittel.

### F08 – Zentrale DI-Registrierung und Sammelordner `Api/Configuration`

- **Priorität:** mittel · **Sicherheit:** bestätigt
- **Fundstellen:** `Api/Configuration/ControllerServiceCollectionExtensions.cs:49-273` (eine Methode,
  rund 220 Registrierungen für alle Fachbereiche); `Api/Configuration/` enthält 25 Dateien:
  Hintergrunddienste, MQTT-Client, Setpoint-Publisher, Mail-Benachrichtigung, Sicherheitsprüfungen.
  Leere Platzhalterordner `Api/BackgroundServices/`, `Api/Endpoints/`.
- **Auswirkung:** Fast jede Funktion ändert dieselbe Datei (Konfliktfläche, langer Kontext). Der
  Ordnername sagt nichts über den Inhalt; die Victron-Anbindung ist schwer zu finden.
- **Empfehlung:** Je Fachbereich eine Registrierungsmethode (`AddEvCharging()`, `AddJointBattery()`,
  `AddWeishaupt()`, `AddVictron()` …) im jeweiligen Feature-Ordner; die Hauptmethode ruft sie nur auf.
  Rein mechanisch, ohne Verhaltensänderung. Victron-Dateien beim nächsten fachlichen Anlass nach
  `Api/Victron/` verschieben, nicht als eigenes Großrefactoring.
- **Aufwand/Risiko:** klein bis mittel / gering (abgesichert durch einen DI-Auflösungstest, siehe P7).

### F09 – Nur ausgewählte Geheimnisse verschlüsselt; README behauptet anderes

- **Priorität:** mittel · **Sicherheit:** bestätigt im Code; Inhalt der Pi-Datenbank nicht geprüft
- **Fundstellen:** `Api/Configuration/ProtectedRscpSettingStore.cs:18-22` (feste Liste: RSCP, BMW,
  Octopus, Preisprognose-Token); `Business/Models/ControllerSettingDefaults.cs:327`
  (`tibber.accessToken` als `Sensitive`, aber nicht in der Liste); `README.md:47` („stored encrypted“).
- **Beobachtung:** Tibber-Token, MQTT- und SMTP-Passwort, Hager-Refresh-Token und Homematic-Token
  liegen im Klartext in SQLite. Ein neues Geheimnis wird nur verschlüsselt, wenn jemand die Liste
  erweitert. Die Maskierung in Lese-DTOs funktioniert und ist getestet (`SettingsEndpointTests.cs:20-30`).
- **Auswirkung:** Ein Agent, der der README glaubt, legt ein neues Geheimnis an und hält es für
  verschlüsselt.
- **Empfehlung:** Verschlüsselung an `ControllerSettingSensitivity.Sensitive` koppeln statt an eine
  Liste (Altwerte bleiben lesbar und werden beim nächsten Speichern verschlüsselt, wie heute). Klasse
  passend umbenennen. README korrigieren. Schlüsselbund-Sicherung dokumentieren (F15).
- **Aufwand/Risiko:** klein / mittel (Schlüsselbund-Verlust macht Werte unlesbar; Rückfrage nötig).

### F10 – Live-Tests laufen implizit gegen Tibber

- **Priorität:** mittel · **Sicherheit:** bestätigt
- **Fundstellen:** `tests/TibberVictronController.Dal.Tests/TibberLivePriceForecastProviderTests.cs:24,44,86`;
  `TestDoubles/EnvironmentVariableReader.cs:7-9` (liest Prozess-, **Benutzer-** und Maschinenvariablen).
- **Beobachtung:** Auf diesem Rechner ist `TIBBER_ACCESS_TOKEN` gesetzt. Ein normales `dotnet test`
  ruft daher die echte Tibber-API auf (nur lesend). Für dieses Audit wurden die Tests per Filter ausgeschlossen.
- **Auswirkung:** Ergebnis hängt von Netz und Tibber ab; ein Agent erkennt nicht, dass er externe
  Dienste aufruft.
- **Empfehlung:** Live-Tests mit `[Trait("Category", "Live")]` kennzeichnen und nur bei expliziter
  Freigabe ausführen (z. B. zusätzlich `RUN_LIVE_TESTS=1`); Standardbefehl schließt sie aus.
- **Aufwand/Risiko:** klein / gering.

### F11 – Systemzeit direkt statt `IUtcClock`

- **Priorität:** mittel · **Sicherheit:** bestätigt
- **Fundstellen:** `Business/Decisions/BatteryForecastService.cs:105` (`DateTimeOffset.UtcNow` in
  Business); außerdem u. a. `Api/Configuration/VictronMqttClientService.cs:145,156,289`,
  `Api/JointBattery/JointBatteryShadowCalculation.cs:58`, `Dal/Battery/SelectedCurrentSiteTelemetryProvider.cs:87`,
  `Dal/Repositories/EfEvTripRepository.cs:130`.
- **Beobachtung:** Messzeitpunkte werden teils mit Systemzeit gestempelt, Frischeprüfungen nutzen
  `IUtcClock` (z. B. `JointBatteryHardware.cs:36`). Im Betrieb gleich, in Tests mit fester Uhr nicht.
- **Auswirkung:** Tests mit kontrollierter Zeit können an diesen Stellen nicht greifen; ein Agent
  übernimmt das Muster.
- **Empfehlung:** In Business sofort auf `IUtcClock` umstellen; in Api/Dal beim nächsten Anfassen.
  Optional eine einfache Prüfung (Test oder Grep in CI), die `DateTimeOffset.UtcNow` in Business verbietet.
- **Aufwand/Risiko:** klein / gering.

### F12 – Frontend-Verträge und Validierung von Hand gespiegelt

- **Priorität:** mittel · **Sicherheit:** bestätigt
- **Fundstellen:** `Frontend/src/pages/settingsPresentation.ts:40-66` („Keep the same field-level
  boundaries as the settings service“) gegenüber `Business/Services/ControllerSettingValueValidation.cs:49-90`;
  handgeschriebene TypeScript-Typen (`dashboardTypes.ts`, `zentraleTypes.ts`, `energyTypes.ts` …);
  kein OpenAPI-Dokument. Fehlerantworten meist `{ message }` (40 Stellen), aber auch reiner Text
  (`Decision/CurrentDecisionEndpoints.cs:76,81`) und ProblemDetails (2 Stellen).
- **Auswirkung:** Eine Änderung an DTO oder Grenzwert muss an zwei Stellen erfolgen; Abweichungen
  fallen erst im Browser auf.
- **Empfehlung:** Grenzwerte über die vorhandene GUI-Metadaten-API ausliefern statt doppelt pflegen.
  Fehlerform `{ message }` als Regel dokumentieren. OpenAPI mit generierten Typen erst, wenn
  Vertragsfehler tatsächlich auftreten (Abwägung, kein Muss).
- **Aufwand/Risiko:** mittel / gering.

### F13 – Einstellungstabelle als allgemeiner Zustandsspeicher

- **Priorität:** mittel · **Sicherheit:** bestätigt
- **Fundstellen:** `Business/Models/ControllerSettingDefaults.cs` (173 Schlüssel, u. a.
  `manualCharge.expiresAtUtc`, `jointBattery.recoveryPending`, `jointBattery.manualCharge.*`,
  `hagerEnergy.oauthState`, Refresh-Tokens); 105 Aufrufe von `GetSettingAsync` in 63 Dateien mit
  eigener Auswertung, z. B. `JointBatteryHardware.cs:76` (`decimal.Parse(… ?? "0")`);
  `IControllerSettingStore` speichert einzeln ohne Transaktion (`ManualChargeEndpoints.cs:170-191`).
- **Auswirkung:** Standardwerte und Fehlerverhalten bei ungültigen Werten unterscheiden sich je
  Aufrufer; zusammengehörige Werte werden nicht atomar geschrieben.
- **Empfehlung:** Für neue Zustände eigene Tabellen bzw. typisierte Provider nach Vorbild
  `DatabaseVictronMqttSettingsProvider` verwenden; bestehende Zugriffe nur beim fachlichen Anlass umstellen.
  Optional `SaveSettingsAsync(IEnumerable<…>)` mit einer Transaktion.
- **Aufwand/Risiko:** schrittweise / gering je Schritt.

### F14 – Viele parallele Dashboard-Varianten

- **Priorität:** mittel · **Sicherheit:** bestätigt
- **Fundstellen:** `Frontend/src/themeRegistry.ts` (8 Themes), `pages/DashboardPage.vue` (1656 Zeilen;
  Umschaltung `:130-136,1029-1050`), Komponenten in `components/{dashboard,leitstand,orbit,horizon,workspace,zentrale}`.
- **Beobachtung:** Commit `18f4a75` musste dieselbe Funktion in Zentrale, Automationen,
  Einstellungen, klassischem Dashboard und Prognose-Panel umsetzen.
- **Auswirkung:** Jede UI-Funktion vervielfacht sich; Agenten wissen nicht, welche Variante gepflegt
  werden muss.
- **Empfehlung:** Produktentscheidung: gepflegte Varianten festlegen (vermutlich Zentrale hell/dunkel)
  und in `frontend-architecture.md` vermerken; übrige einfrieren („keine neuen Funktionen“) oder
  entfernen.
- **Aufwand/Risiko:** Entscheidung klein; Entfernen mittel / gering.

### F15 – Betriebswissen nur im KI-Anbieter-spezifischen Speicher

- **Priorität:** mittel · **Sicherheit:** bestätigt
- **Beobachtung:** Wichtige Betriebsfakten stehen nur im lokalen Claude-Code-Speicher, nicht im
  Repository: Pi-Host und Zugriffsweg, Dienstname, Datenbankpfad (`/var/lib/energyflowpilot/…`,
  ca. 1,25 GB), Sicherungsverzeichnis und die Regel, auf dem laufenden Pi **keine** Vollsicherung zu
  machen (`sqlite3 .backup` löste am 2026-11-04 Watchdog-Neustarts aus). Im Repo gibt es keine
  Backup-/Restore-Anleitung; dass der Data-Protection-Schlüsselbund zusammen mit der Datenbank
  gesichert werden muss, steht nur im Code (`ControllerServiceCollectionExtensions.cs:65-68`).
- **Auswirkung:** Ein anderer Agent oder Mensch kennt diese Regeln nicht und kann den Betrieb stören
  oder verschlüsselte Zugangsdaten unbrauchbar machen.
- **Empfehlung:** `docs/30-operations/operations-runbook.md` mit Pfaden (ohne Geheimnisse),
  Sicherungsregeln, Wiederherstellung, Rollback und Prüfschritten nach einem Update.
- **Aufwand/Risiko:** klein / gering.

### F16 – Healthcheck deckt den aktiven gemeinsamen Steuerpfad nicht ab

- **Priorität:** niedrig · **Sicherheit:** noch zu prüfen
- **Fundstelle:** `Api/Health/HealthEndpoints.cs:23-41` – bewertet MQTT-Status und den
  Decision-Worker; Joint-Ausführung, E3/DC und OCPP fließen nicht direkt ein.
- **Empfehlung:** Prüfen, ob ein Ausfall der gemeinsamen Ausführung `/health` beeinflusst; sonst
  eine Komponente ergänzen (der Deploy-Healthcheck nutzt `/health`).

### F17 – Keine Formatierungs-/Analyseregeln; vorhandene Warnungen

- **Priorität:** niedrig · **Sicherheit:** bestätigt
- **Beobachtung:** Kein `.editorconfig`, kein `Directory.Build.props`, keine Warnung-als-Fehler-Regel,
  kein Linter im Frontend. Build-Warnungen: `EF1002` (3×, `ControllerDbInitializer.cs:76,99,128`, Werte
  sind Konstanten – faktisch kein Injektionsrisiko), `xUnit1031` (`JointBatteryExecutionTests.cs:103`),
  `xUnit2017` (`CalendarTripTests.cs:92`).
- **Empfehlung:** Minimale `.editorconfig` mit den heute gelebten Regeln; Warnungen bereinigen oder
  begründet unterdrücken. Kein striktes Regelwerk einführen (Stilpräferenz).

### F18 – HttpClients ohne explizites Timeout

- **Priorität:** niedrig · **Sicherheit:** bestätigt (Auswirkung auf Entscheidungszyklus noch zu prüfen)
- **Fundstellen:** `ControllerServiceCollectionExtensions.cs:199` (Hager), `:207` (Open-Meteo),
  `:211` (PriceDriver), `:241` (Tibber), `:263` (Forecast.Solar). Standard sind 100 s; andere Clients
  setzen 8–30 s.
- **Empfehlung:** Einheitlich Timeouts setzen; prüfen, ob ein hängender Abruf den Entscheidungszyklus
  verzögert (Caches mildern das).

### F19 – Endpunkt-Tests umgehen die HTTP-Pipeline

- **Priorität:** niedrig · **Sicherheit:** bestätigt
- **Beobachtung:** Tests rufen statische Handler direkt auf (z. B. `CurrentDecisionEndpointTests.cs:32`);
  Routing, Model-Binding und Middleware werden nur in `AuthenticationTests.cs` teilweise geprüft. Die
  globale Fallback-Policy schützt neue Endpunkte trotzdem.
- **Empfehlung:** Einen schmalen Pipeline-Test (z. B. `WebApplicationFactory` mit ersetzten
  Hardwarediensten), der prüft: ohne Rolle 401, Viewer darf nicht schreiben, Hosted Services
  starten ohne Hardware. Nur wenige Fälle.

### F20 – Doppelte Test-Doubles

- **Priorität:** niedrig · **Sicherheit:** bestätigt
- **Beobachtung:** 13 Definitionen von `FakeControllerSettingStore`/`FakeUtcClock`/`Clock` in den
  Testprojekten.
- **Empfehlung:** Beim nächsten Anfassen auf die vorhandenen `TestDoubles/`-Klassen umstellen.

### F21 – Gemischte Doku-Sprache (Stilpräferenz)

- **Priorität:** niedrig · **Sicherheit:** bestätigt
- **Beobachtung:** Vier Dokumente überwiegend Deutsch (`e3dc-rscp.md`, `weishaupt-warmwasser.md`,
  `electricity-price-providers.md`, `authentication-and-remote-access.md`), andere Englisch, einige
  gemischt. Deutsche Fehlermeldungen teils mit Umlauten, teils umschrieben („benoetigt“, „Aenderungen“).
  Eine Regel zur Doku-Sprache steht nicht im Repository.
- **Empfehlung:** Eine Zeile in `AGENTS.md` festlegen; Bestand nicht umschreiben.

---

## C. Architekturvorschlag

Gerechtfertigt durch F03, F04, F06, F07, F08. Kein Umbau der Schichten.

### Fachliche Grenzen (bereits als Ordner vorhanden)

| Slice | Api | Business | Dal | Frontend |
|---|---|---|---|---|
| Akku-Steuerung (Einzel + gemeinsam) | `Decision/`, `ManualCharge/`, `JointBattery/`, `Forecast/` | `Decisions/`, `Auditing/` | `Battery/`, `Victron/`, `Mqtt/` | `zentrale/`, `dashboard/` |
| Victron-Anbindung | heute `Configuration/` (Victron*-Dateien) | Schnittstellen | `Victron/`, `Mqtt/` | – |
| E3/DC | `HagerEnergy/E3dcEndpoints.cs` | `Decisions/E3dc*` | `E3dc/`, `HagerEnergy/` | `E3dcSupplementPanel.vue` |
| E-Auto | `EvCharging/` | `EvCharging/` | `Ocpp/`, `BmwCarData/`, `Calendar/`, `Routing/` | `EvChargingPage.vue`, `dashboard/*Ev*`, `Trip*` |
| Wärmepumpe/Warmwasser | `Weishaupt/` | `Services/Weishaupt*`, `Decisions/Weishaupt*` | `Weishaupt/` | `heating/` |
| Raumklima | `Homematic/` | `Homematic/` | `Homematic/` | `HeatingPage.vue` |
| Preise | `Prices/` | `Services/DynamicTariff*`, `PriceForecastAccuracy` | `Tibber/`, `Octopus/`, `Prices/` | `settings/*Tariff*`, `PriceForecastAccuracy.vue` |
| Energiekosten/Historie | `Energy/`, `Savings/` | `Energy/`, `Decisions/BatterySavings*` | `Repositories/` | `energy/`, `EnergyCostsPage.vue` |
| Einstellungen/Metadaten | `Settings/`, `Metadata/` | `Services/ControllerSetting*` | `Persistence/` | `SettingsPage.vue`, `settings/` |
| Querschnitt | `Authentication/`, `Diagnostics/`, `Health/`, `Push/`, `Dashboard/` | `Abstractions/IUtcClock`, `IOperationalEventRepository` | `Persistence/`, `WebPush/` | `App.vue`, `auth.ts` |

Die Dateien bleiben in ihren Schichten. Was fehlt, ist eine **sichtbare Karte** je Slice und eine
**eigene DI-Registrierung** je Slice.

### Zulässige Abhängigkeiten

- Bestehende Schichtregel bleibt: `Api → Business ← Dal`, Business ohne Framework.
- Slices dürfen Querschnitt nutzen: Einstellungen, Uhr, Betriebsereignisse, Preis-Schnittstellen.
- `Dashboard`/Zentrale ist ein Lesemodell: liest aus Slices, kein Slice liest aus `Dashboard`.
- E-Auto darf den Akkuplan nur über Business-Schnittstellen lesen (heute schon über den gemeinsamen Plan).
- **Hardware-Schreibzugriffe nur über Adapter, die ein zentrales Schreib-Gate prüfen** (P8).

### Zuständigkeit gemeinsamer Infrastruktur

- `ControllerDbContext` und `ControllerDbInitializer` bleiben zentral (eine SQLite-Datei). Regel: jede
  Schemaänderung additiv und im Initialisierer; der Abgleichtest (P6) sichert das.
- `ControllerSettingDefaults` bleibt zentral; neue Zustände außerhalb der Einstellungstabelle (F13).
- Kein neues `Shared`/`Common`-Projekt (entspricht `project-structure.md`).

### Schrittweiser Übergang

1. Karte und Regeln in `AGENTS.md` (P2) – sofort nutzbar, ohne Codeänderung.
2. Toten Code entfernen (P3).
3. DI-Registrierung je Slice (P7), mechanisch.
4. Zentrales Schreib-Gate (P8) mit Hardware-Abnahme.
5. Dateien nur bei fachlichem Anlass verschieben (z. B. Victron aus `Api/Configuration/`).

### Unverändert lassen

- Business-Entscheidungscode und Decision-Audit-Harness.
- Authentifizierung und `AdminWriteGuardMiddleware`.
- Watchdog, `--joint-stop`, Wiederherstellungspflicht.
- DTO-Mapping an der Api-Grenze.
- Sperrlogik und Staging im Deploy-Skript.

---

## D. Umsetzungspakete

### Dringend (Fehlerbehebung, ohne Verhaltensänderung am Steuerverhalten)

#### P1 – Reproduzierbarer Build und Testlauf (F01)
- **Ziel/Nutzen:** Jeder frische Klon baut und testet mit einem Befehl; Grundlage für CI.
- **Bereiche:** `.gitignore`, `TibberVictronController.slnx` (neu versioniert),
  `DecisionAuditArtifactExportTests.cs`, `README.md`, `CLAUDE.md`.
- **Grenzen:** Lokale Altdateien im Wurzelverzeichnis nicht löschen (nur nach Rückfrage).
- **Akzeptanz:** In einem frischen Klon läuft `dotnet test TibberVictronController.slnx` grün; der
  Audit-Export-Test findet das Wurzelverzeichnis.
- **Prüfungen:** frischer Klon in temporärem Verzeichnis, ein Testlauf.
- **Abhängigkeiten:** keine.

#### P2 – Anbieterneutrale `AGENTS.md`, korrigierte Anweisungen (F03, F07, F15 teilweise)
- **Ziel/Nutzen:** Agenten finden den richtigen Bereich und die richtigen Befehle.
- **Bereiche:** `AGENTS.md` (neu), `CLAUDE.md` (Verweis), `docs/README.md` (Leseliste),
  `docs/00-governance/working-agreement.md` („Codex“ → neutral), `README.md` (Port, Verschlüsselung).
- **Grenzen:** Nur Dokumentation.
- **Akzeptanz:** Jede genannte Klasse existiert und ist registriert; jeder Befehl lief einmal erfolgreich;
  Leseliste enthält alle Dokumente.
- **Prüfungen:** Befehle einmal ausführen; Klassennamen per Suche prüfen.
- **Abhängigkeiten:** P1 (Befehle).

#### P3 – Toten Code entfernen (F06)
- **Ziel/Nutzen:** Eine Implementierung je Aufgabe.
- **Bereiche:** die fünf genannten Klassen und ihre Tests.
- **Grenzen:** Ein Commit je Klasse; `BatteryDecisionRuleEvaluator` nur nach Rückfrage.
- **Akzeptanz:** Build grün, keine verbleibenden Referenzen, Testzahl sinkt nur um die entfernten Tests.
- **Prüfungen:** `dotnet test TibberVictronController.slnx`.
- **Abhängigkeiten:** P1.

#### P4 – CI-Workflow (F02)
- **Ziel/Nutzen:** Jeder Push wird geprüft, bevor er den Pi erreicht.
- **Bereiche:** `.github/workflows/ci.yml` (neu).
- **Grenzen:** Kein Deploy aus CI; Live-Tests ausgeschlossen.
- **Akzeptanz:** Workflow läuft bei Push/PR: .NET-Tests, `vue-tsc --noEmit`, Node-Test; ein
  absichtlich roter Test macht ihn rot.
- **Prüfungen:** ein Workflow-Lauf.
- **Abhängigkeiten:** P1, P5 (Live-Test-Ausschluss).

#### P5 – Live-Tests nur auf ausdrücklichen Wunsch (F10)
- **Ziel/Nutzen:** Standardtestlauf ohne externe Dienste.
- **Bereiche:** `TibberLivePriceForecastProviderTests.cs`, `RequiresEnvironmentVariableFactAttribute.cs`.
- **Akzeptanz:** `dotnet test` ohne Filter ruft Tibber auch bei gesetztem Token nicht auf; mit
  `RUN_LIVE_TESTS=1` schon.
- **Prüfungen:** ein Testlauf mit und ohne Schalter.
- **Abhängigkeiten:** keine.

#### P6 – Schemaregeln dokumentieren und absichern (F05)
- **Ziel/Nutzen:** Neue Spalten/Tabellen kommen auch auf der Bestandsdatenbank an.
- **Bereiche:** `docs/30-operations/persistence-model.md`, neuer Test in `Dal.Tests`,
  `dotnet-tools.json` (entfernen oder begründen).
- **Grenzen:** Kein Wechsel auf EF-Migrationen.
- **Akzeptanz:** Test vergleicht Tabellen und Spalten einer neuen Datenbank mit einer aus
  Altschema aktualisierten Datenbank und schlägt fehl, wenn eine Spalte nur im EF-Modell existiert.
- **Prüfungen:** Testlauf Dal.Tests.
- **Abhängigkeiten:** P1.

#### P7a – Inventar der Hardware-Schreibpfade (F04, nur Doku)
- **Ziel/Nutzen:** Jeder Agent kennt Schalter und Standardwerte.
- **Bereiche:** `docs/20-domain/` oder `docs/30-operations/` (neue Tabelle), `AGENTS.md`.
- **Akzeptanz:** Tabelle nennt je Gerät: Adapterklasse, Aufrufer, Schalter, Standard, Rücknahme.
- **Abhängigkeiten:** keine.

#### P7b – Betriebshandbuch (F15)
- **Ziel/Nutzen:** Betriebswissen unabhängig vom KI-Werkzeug.
- **Bereiche:** `docs/30-operations/operations-runbook.md`.
- **Grenzen:** Keine Geheimnisse, keine Hostnamen, die nicht ohnehin dokumentiert sein sollen (Rückfrage).
- **Akzeptanz:** Enthält Dienstname, Datenpfad, Sicherungsregel (keine Vollsicherung im laufenden
  Betrieb), Schlüsselbund mitsichern, Rollback-Weg, Prüfschritte nach Update.
- **Abhängigkeiten:** keine.

### Optional (Strukturverbesserungen, jeweils einzeln)

| Paket | Ziel | Bereiche | Akzeptanz | Abhängig von |
|---|---|---|---|---|
| P8 – DI je Slice (F08) | kleinere Änderungsflächen | `ControllerServiceCollectionExtensions.cs`, je Feature eine Registrierungsdatei | Hauptmethode nur Aufrufe; neuer Test löst alle registrierten Dienste und Hosted Services mit In-Memory-Datenbank auf | P1 |
| P9 – Zentrales Hardware-Schreib-Gate (F04) | Schutz am Adapter statt beim Aufrufer | Victron-Publisher, E3/DC-, Weishaupt-, OCPP-Steuerung, `appsettings.Development.json` | Je Adapter ein Test: bei Gate aus kein Schreibaufruf; lokal standardmäßig aus; Hardware-Abnahme auf dem Pi | P7a, Rückfrage |
| P10 – Verschlüsselung nach Sensitivität (F09) | neue Geheimnisse automatisch geschützt | `ProtectedRscpSettingStore.cs`, README | Alle `Sensitive`-Werte werden beim Speichern verschlüsselt; Altwerte lesbar; Test | P7b (Schlüsselbund-Sicherung), Rückfrage |
| P11 – Uhr in Business (F11) | Zeit testbar | `BatteryForecastService.cs` | kein `DateTimeOffset.UtcNow` in Business; Test mit fester Uhr | – |
| P12 – Deploy-Gate und Rollback (F02) | nur geprüfte Stände auf den Pi | `scripts/deploy/*`, CI | Auto-Update zieht nur CI-freigegebene Stände; vorheriges Release bleibt; fehlgeschlagener Healthcheck stellt es wieder her | P4, Rückfrage |
| P13 – Gepflegte Dashboard-Varianten (F14) | weniger UI-Duplikate | `themeRegistry.ts`, `frontend-architecture.md` | Entscheidung dokumentiert; ungepflegte Varianten eingefroren oder entfernt | Produktentscheidung |
| P14 – Grenzwerte aus Metadaten (F12) | eine Quelle für Validierung | `GuiMetadataCatalog.cs`, `settingsPresentation.ts` | Frontend liest Grenzen aus `/api/metadata`; bestehende Tests grün | – |
| P15 – Joint-Projektion nach Business (F07) | Fachlogik aus Api | `JointAwareBatteryDecisionService.cs` | reine Funktion in Business mit Tests; Dekorator ohne Fachlogik | P8 |

---

## E. Vorschlag für Repository-Anweisungen (`AGENTS.md`)

Noch **nicht** angelegt. Englisch, passend zu `CLAUDE.md` und Code. `CLAUDE.md` würde danach nur auf
`AGENTS.md` verweisen. Befehle mit ✅ liefen in diesem Audit erfolgreich; ⚠️ = nicht ausgeführt.

```markdown
# AGENTS.md

EnergyFlowPilot controls real home batteries, a wallbox and a heat pump on a Raspberry Pi.
Treat every hardware write path as safety-critical.

## Layout
- `src/TibberVictronController.Business` – domain logic, all boundary interfaces. No framework references (compiler-enforced).
- `src/TibberVictronController.Dal` – EF Core/SQLite, external API clients, device protocols (RSCP, OCPP, Modbus, Homematic).
- `src/TibberVictronController.Api` – endpoints, hosted services, DI. Victron MQTT client and setpoint publisher live in `Api/Configuration/`.
- `src/TibberVictronController.Frontend` – Vue 3 + Vuetify. Display only.
- Features use the same folder name in every layer (`EvCharging`, `JointBattery`, `Weishaupt`, `Homematic`, `Energy`, `Prices`).
- Requirements: `docs/` (see `docs/README.md`). Docs win over existing behaviour; report conflicts first.

## Which battery decision path is active
- `jointBattery.executionEnabled=true` or `jointBattery.recoveryPending=true`: joint control
  (`Business/Decisions/JointBattery*`, `Api/JointBattery/`). `JointAware*` decorators bypass `CurrentBatteryDecisionService`.
- Otherwise: `CurrentBatteryDecisionService` (live) and `BatteryForecastSimulator` (forecast).
- Time: inject `IUtcClock`. Do not call `DateTimeOffset.UtcNow` in Business.

## Commands (run from repository root)
- ✅ Backend tests: `dotnet test TibberVictronController.slnx --filter "FullyQualifiedName!~TibberLivePriceForecastProviderTests"`
  (requires the .slnx to be tracked, see audit P1; the filter skips tests that call the real Tibber API)
- ✅ One test class: `dotnet test TibberVictronController.slnx --filter "FullyQualifiedName~<ClassName>"`
- ✅ Frontend type check: `cd src/TibberVictronController.Frontend && npx vue-tsc --noEmit`
- ✅ Frontend unit tests: `cd src/TibberVictronController.Frontend && node --test tests/settingsPresentation.test.mjs` (Node ≥ 22.18; older: add `--experimental-strip-types`)
- ⚠️ Frontend build: `npm run build` (writes `dist/`)
- ⚠️ Run locally: `dotnet run --project src/TibberVictronController.Api` (port 5094) – connects to the devices configured in the local database. Check `victron.dryRun=true` first.

## Checks by change type
- Business/Dal/Api code: backend tests.
- Frontend: type check + frontend unit tests.
- Settings: update `ControllerSettingDefaults`, `ControllerSettingValueValidation`, `GuiMetadataCatalog` and, for numeric inputs, `settingsPresentation.ts`.
- Database: additive changes only. Add every new table/column to the EF model **and** to `ControllerDbInitializer`. Do not run `dotnet ef migrations`.
- Hardware write paths: never add a write without the device switch (`victron.dryRun`, `jointBattery.executionEnabled`, `evCharging.controlEnabled`, `weishauptHotWater.enabled`). Add a test with a fake adapter. Real-device tests only by the operator.

## Rules
- Error messages (logs, exceptions, API, UI) in German; code, comments, commits and docs in English.
- API errors: `{ "message": "<German text>" }`.
- Secrets: never in the repository, logs or read DTOs. Only the keys listed in `ProtectedRscpSettingStore` are encrypted at rest.
- No business logic in endpoints. DTOs for every frontend exchange.
- Generated or local-only, do not edit: `bin/`, `obj/`, `dist/`, `node_modules/`, `artifacts/`, `*.db`, ignored legacy files in the repository root.
- Commits go to `main`. Push only when asked: pushes reach the Raspberry Pi through auto-update.
- Do not touch the running Pi (service restarts, database backups, settings) without explicit permission.

## Done means
- Relevant checks above ran and passed; output reported.
- Docs in `docs/` updated when behaviour or requirements changed.
- Hand-over lists what was not verified (especially hardware behaviour).
```

Zusätzlich vorgeschlagene Änderungen an bestehenden Anweisungen:
- `CLAUDE.md`: auf `AGENTS.md` verweisen; Claude-spezifisches nur dort ergänzen.
- `docs/00-governance/working-agreement.md:5`: „Codex acts as …“ neutral formulieren.
- Offene Frage an den Nutzer: Die Regel „Run at most one build or test pass per task“ stammt aus
  Token-Sparsamkeit. Bei rund 15 s Testlaufzeit wäre „gezielte Tests während der Arbeit, ein
  vollständiger Lauf vor der Übergabe“ für Agenten sicherer.

---

## F. Prüfprotokoll

### Ausgeführte Befehle

| Befehl | Ergebnis | Grenzen |
|---|---|---|
| `git status`, `git ls-files`, `git log`, `git branch -a`, `git rev-list` | sauber; 253 Commits; Branches `main`, `develop` (veraltet), `master` (veraltet) | – |
| `dotnet --list-sdks` | 8.0.427, 9.0.320, 10.0.303, 10.0.401; `global.json` verlangt 10.0.100 mit `latestFeature` | – |
| `dotnet build` (Wurzel, ohne Argument) | **Fehler MSB1011** | nur Projektauswahl, kein Build |
| `dotnet test TibberVictronController.slnx --filter "FullyQualifiedName!~TibberLivePriceForecastProviderTests"` | **791 Tests bestanden** (Business 475, Dal 172, Api 144), 0 Fehler, rund 15 s (inkrementeller Build) | Solution-Datei lokal, nicht versioniert; Live-Tests ausgeschlossen |
| Build-Warnungen aus demselben Lauf | `EF1002` ×3, `xUnit1031` ×1, `xUnit2017` ×1 | – |
| `npx vue-tsc --noEmit` (Frontend) | erfolgreich, Exit 0 | keine Laufzeitprüfung |
| `node --test tests/` | fehlgeschlagen: Node 22 akzeptiert hier kein Verzeichnis – Bedienfehler, kein Projektfehler | – |
| `node --test tests/settingsPresentation.test.mjs` | **5/5 bestanden** | einzige Frontend-Testdatei |
| `dotnet list TibberVictronController.slnx package --vulnerable --include-transitive` | keine anfälligen Pakete | Stand NuGet-Feed 2026-11-08 |
| `dotnet list … package --outdated` | kleinere Updates verfügbar (EF Core 10.0.11→10.0.14, OpenIdConnect 10.0.13→10.0.14, Testpakete) | – |
| `npm audit` | 0 Schwachstellen | – |
| Lesende SQLite-Abfrage der lokalen Entwicklungsdatenbank (`mode=ro`) | `victron.dryRun=true`, `victron.controlMode=normalEss`, `victron.host`/`mqtt.host` gesetzt (Werte nicht ausgegeben) | nur nicht geheime Schlüssel; Pi-Datenbank nicht geprüft |
| Referenzsuchen (`git grep`) für tote Klassen, Zeitnutzung, DryRun-Prüfungen, Fehlerformen | siehe Befunde | Textsuche; Reflection oder dynamische Nutzung nicht erfasst |

### Nicht ausgeführte Prüfungen

| Prüfung | Grund |
|---|---|
| Live-Tests gegen Tibber | externe API mit echtem Token; per Filter ausgeschlossen |
| `npm run build` | schreibt `dist/`; Typprüfung separat ausgeführt |
| `dotnet run` / `npm run dev` | würde sich mit realen Geräten im LAN verbinden |
| Build/Test in frischem Klon | laut Projektregel höchstens ein Build-/Testlauf je Aufgabe; Befund F01 statisch belegt |
| Zugriff auf den Pi (Cron, Branch, Rechte, Logs, Datenbank) | Auftrag: keine Eingriffe in produktive Systeme |
| Sicherheitstests gegen laufende Instanz | Auftrag: nicht invasiv |
| Browser-/End-to-End-Tests | kein Werkzeug im Repository; würde laufende Instanz erfordern |

### Offene Fragen und Unsicherheiten

1. Welchen Branch verfolgt das Auto-Update auf dem Pi (`main` oder `develop`)? Davon hängt ab, ob ein
   Push nach `main` sofort ausgerollt wird (F02).
2. Sind die Klartext-Geheimnisse in der Pi-Datenbank (F09) und die Dateirechte wie in der Doku verlangt gesetzt?
3. Soll `BatteryDecisionRuleEvaluator` als Referenz erhalten bleiben (F06)?
4. Welche Dashboard-Varianten sollen weiter gepflegt werden (F14)?
5. Darf das Betriebshandbuch Hostnamen und Pfade des Pi enthalten (F15)?
6. Soll die Regel „ein Build-/Testlauf je Aufgabe“ gelockert werden (Abschnitt E)?
7. Wirkt ein Ausfall der gemeinsamen Ausführung auf `/health` (F16)?
8. Sprache dieses Berichts: Deutsch, weil Auftrag und Gliederung deutsch vorgegeben waren; der
   `AGENTS.md`-Vorschlag ist Englisch wie die übrigen Agentenanweisungen.
