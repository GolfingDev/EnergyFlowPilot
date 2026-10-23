using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Metadata;

public static class GuiMetadataCatalog
{
    public static GuiMetadataResponseDto Create()
    {
        return new GuiMetadataResponseDto(
            ControllerSettingDefaults.GetDefinitions()
                .Select(MapSetting)
                .ToArray(),
            CreateDecisionRules());
    }

    private static SettingMetadataDto MapSetting(ControllerSettingDefinition definition)
    {
        return definition.Key switch
        {
            ControllerSettingDefaults.E3dcRscpEnabledKey => CreateSetting(definition, "Lokales E3/DC-Lesen aktiv", "Liest RSCP-Messwerte. Dieser Zugang sendet keine Steuerbefehle.", "E3dc", "boolean", null),
            ControllerSettingDefaults.E3dcRscpHostKey => CreateSetting(definition, "Lokale E3/DC-Adresse", "IP-Adresse oder Hostname im Heimnetz.", "E3dc", "text", null),
            ControllerSettingDefaults.E3dcRscpPortKey => CreateSetting(definition, "RSCP-Port", "Normalerweise 5033; getrennt vom Modbus-Port.", "E3dc", "number", null),
            ControllerSettingDefaults.E3dcRscpUserKey => CreateSetting(definition, "RSCP-Benutzer", "Lokaler Benutzerzugang. Wird verschluesselt gespeichert.", "E3dc", "password", null),
            ControllerSettingDefaults.E3dcRscpPasswordKey => CreateSetting(definition, "RSCP-Anmeldepasswort", "Passwort des Benutzerzugangs. Wird verschluesselt gespeichert.", "E3dc", "password", null),
            ControllerSettingDefaults.E3dcRscpKeyKey => CreateSetting(definition, "RSCP-Schluessel", "Am Geraet gesetztes RSCP-Passwort; maximal 32 UTF-8-Bytes. Wird verschluesselt gespeichert.", "E3dc", "password", null),
            ControllerSettingDefaults.JointBatteryEnabledKey => CreateSetting(definition, "Gemeinsame Akku-Vorschau", "Berechnet getrennte Ziele fuer beide Akkus. Fuehrt auch bei ausgeschaltetem Victron-Shadow-Modus keine gemeinsamen Hardwarebefehle aus.", "E3dc", "boolean", null),
            ControllerSettingDefaults.JointBatteryExecutionEnabledKey => CreateSetting(definition, "Beide Akkus gemeinsam steuern", "Aktiviert die koordinierte Ausfuehrung. Globaler Shadow-Modus hat Vorrang. Benoetigt bestaetigte Anschlussgrenzen, lokalen RSCP-Zugang und Linux-Watchdog; unabhaengige Victron-Regelung wird dann gesperrt.", "E3dc", "boolean", null),
            ControllerSettingDefaults.JointBatteryRecoveryPendingKey => CreateSetting(definition, "Ruecknahme ausstehend", "Interner Wiederherstellungsstatus; wird automatisch gesetzt und kann nicht manuell geloescht werden.", "E3dc", "text", null),
            ControllerSettingDefaults.JointBatteryPhase1LimitKey => CreateSetting(definition, "Maximaler Netzbezug L1", "Bestaetigte Anschlussgrenze in Watt. 0 bedeutet unbekannt und sperrt die Hardwareausfuehrung.", "E3dc", "number", "W"),
            ControllerSettingDefaults.JointBatteryPhase2LimitKey => CreateSetting(definition, "Maximaler Netzbezug L2", "Bestaetigte Anschlussgrenze in Watt. Kein automatischer Rueckschluss aus der Akkuleistung.", "E3dc", "number", "W"),
            ControllerSettingDefaults.JointBatteryPhase3LimitKey => CreateSetting(definition, "Maximaler Netzbezug L3", "Bestaetigte Anschlussgrenze in Watt. Kein automatischer Rueckschluss aus der Akkuleistung.", "E3dc", "number", "W"),
            ControllerSettingDefaults.JointBatteryVictronInputLimitKey => CreateSetting(definition, "Victron AC-Zuleitung je Phase", "Bestaetigte zulaessige AC-Eingangsleistung je Victron-Phase einschliesslich AC-out-Verbrauch. 0 sperrt die Hardwareausfuehrung.", "E3dc", "number", "W"),
            ControllerSettingDefaults.JointBatteryGridLimitKey => CreateSetting(definition, "Zusaetzliches Netzleistungsbudget", "Optionale Gesamtgrenze fuer Haus und beide Akkus. 0 bedeutet volle verfuegbare Geraeteleistung; die separaten Anschluss- und Phasengrenzen bleiben zwingend.", "E3dc", "number", "W"),
            ControllerSettingDefaults.E3dcPlanningMaximumSocKey => CreateSetting(definition, "E3/DC-Planungsmaximum", "Ladeziel fuer geplante Netzladung; PV kann den restlichen Platz nutzen. Die ausgelesene Notstromreserve bleibt erhalten.", "E3dc", "number", "%"),
            ControllerSettingDefaults.E3dcEfficiencyKey => CreateSetting(definition, "E3/DC-Wirkungsgrad (Annahme)", "Geschaetzter Gesamtwirkungsgrad fuer die Vorschau; kein ausgelesener Messwert.", "E3dc", "number", "%"),
            ControllerSettingDefaults.WeishauptBiblockModbusEnabledKey => CreateSetting(definition, "Biblock-Erfassung aktiv", "Aktiviert den lesenden Modbus-Collector. Auch Voraussetzung fuer die Warmwasserautomatik.", "Weishaupt", "boolean", null),
            ControllerSettingDefaults.WeishauptBiblockModbusHostKey => CreateSetting(definition, "Biblock-Adresse", "IP-Adresse der Waermepumpe. Bei aktiver Anhebung erst die Ruecknahme abwarten, bevor die Adresse geaendert wird.", "Weishaupt", "text", null),
            ControllerSettingDefaults.WeishauptBiblockModbusPortKey => CreateSetting(definition, "Modbus-Port", "TCP-Port der Biblock, normalerweise 502.", "Weishaupt", "number", null),
            ControllerSettingDefaults.WeishauptBiblockModbusSlaveIdKey => CreateSetting(definition, "Modbus-Slave-ID", "Modbus-Geraeteadresse, normalerweise 1.", "Weishaupt", "number", null),
            ControllerSettingDefaults.WeishauptBiblockModbusPollingIntervalSecondsKey => CreateSetting(definition, "Biblock-Abfrageintervall", "Abstand der lesenden Telemetrieabfragen: 60 bis 3600 Sekunden, damit Stromzaehler mindestens stuendlich gespeichert werden.", "Weishaupt", "number", "s"),
            ControllerSettingDefaults.WeishauptBiblockModbusSampleRetentionDaysKey => CreateSetting(definition, "Biblock-Aufbewahrung", "Aufbewahrungsdauer der Temperatur-, Betriebs- und Stromzaehlerdaten; Standard 400 Tage.", "Weishaupt", "number", "days"),
            ControllerSettingDefaults.WeishauptHotWaterEnabledKey => CreateSetting(definition, "Warmwasser automatisch anheben", "Hebt im guenstigsten geeigneten Preisfenster oder bei PV-Ueberschuss um 5 K an (max. 55 Grad). Ruecknahme bei Zielerreichung/Fensterende, Sicherheitslimit 4 Stunden. Vorhandenes Zeitprogramm bleibt gueltig. Bei Verbindungsausfall erfolgt die Ruecknahme erst nach Wiederverbindung.", "Weishaupt", "boolean", null),
            ControllerSettingDefaults.WeishauptHotWaterCheapPriceKey => CreateSetting(definition, "Alte absolute Preisschwelle (ungenutzt)", "Durch relative Preisplanung ersetzt. Wird nicht mehr ausgewertet.", "Weishaupt", "number", "EUR/kWh"),
            ControllerSettingDefaults.WeishauptHotWaterMinimumAdvantageKey => CreateSetting(definition, "Mindest-Preisvorteil", "Erforderlicher Vorteil gegenueber dem Nachheizen zur Bedarfszeit, nach geschaetztem Mehrverbrauch (0,01 bis 1 EUR/kWh). Keine gemessene Ersparnis.", "Weishaupt", "number", "EUR/kWh"),
            ControllerSettingDefaults.WeishauptHotWaterExtraEnergyKey => CreateSetting(definition, "Geschaetzter Mehrverbrauch", "Modellannahme fuer hoehere Ladetemperatur und Speicherverluste (0 bis 100 Prozent), kein gemessener Wirkungsgrad.", "Weishaupt", "number", "%"),
            ControllerSettingDefaults.WeishauptHotWaterDemandHourKey => CreateSetting(definition, "Warmwasserbedarf ab", "Stunde in Europe/Berlin (0 bis 23). Ausgangsannahme fuer den taeglichen Bedarf, nicht automatisch aus Zapfungen erkannt.", "Weishaupt", "number", "Uhr"),
            ControllerSettingDefaults.WeishauptHotWaterInitialHoldHoursKey => CreateSetting(definition, "Anfaengliche Ueberbrueckung", "Zeit zwischen Ladeende und Bedarf (1 bis 12 Stunden). Die Ladedauer kommt bei der Fenstersuche hinzu. Nach mindestens drei brauchbaren Verlaeufen konservativ verkuerzbar.", "Weishaupt", "number", "h"),
            ControllerSettingDefaults.WeishauptHotWaterInitialChargeMinutesKey => CreateSetting(definition, "Anfaengliche Ladedauer", "Zusammenhaengende Ladezeit (15 bis 180 Minuten). Nach mindestens drei Zielerreichungen aus den Verlaeufen abgeschaetzt.", "Weishaupt", "number", "min"),
            ControllerSettingDefaults.WeishauptHotWaterPvSurplusKey => CreateSetting(definition, "PV-Ueberschuss ab", "Netzeinspeisung nach Abzug von Batterieentladung (500 bis 20000 W). Beim Halten genuegen 200 W. PV-Erzeugung allein genuegt nicht.", "Weishaupt", "number", "W"),
            ControllerSettingDefaults.BatteryTotalCapacityKwhKey => CreateSetting(definition, "Batteriekapazitaet", "Gesamtkapazitaet des Akkus.", "Battery", "number", "kWh"),
            ControllerSettingDefaults.BatteryMinimumStateOfChargePercentKey => CreateSetting(definition, "Minimaler SoC", "Harte Untergrenze zum Schutz des Akkus.", "Battery", "number", "%"),
            ControllerSettingDefaults.BatteryMaximumChargePowerWattsKey => CreateSetting(definition, "Maximale Ladeleistung", "Hoechste Ladeleistung, die die Decision Engine einplanen darf.", "Battery", "number", "W"),
            ControllerSettingDefaults.BatteryMaximumDischargePowerWattsKey => CreateSetting(definition, "Maximale Entladeleistung", "Hoechste Entladeleistung, die die Decision Engine einplanen darf.", "Battery", "number", "W"),
            ControllerSettingDefaults.BatteryRoundTripEfficiencyPercentKey => CreateSetting(definition, "Wirkungsgrad", "Gesamter Lade- und Entladewirkungsgrad des Akkus.", "Battery", "number", "%"),
            ControllerSettingDefaults.BatteryTargetEndStateOfChargePercentKey => CreateSetting(definition, "Endreserve (nicht verwendet)", "Wird vom Planer nicht mehr verwendet. Fehlende Preise werden mit einem Vergleichstag geschaetzt; den Akku schuetzt die Planungsreserve.", "Battery", "number", "%"),
            ControllerSettingDefaults.BatteryPlanningMinimumStateOfChargePercentKey => CreateSetting(definition, "Planungs-Minimum", "Weichere Reserve oberhalb der harten Akku-Untergrenze.", "Battery", "number", "%"),
            ControllerSettingDefaults.BatteryPlanningMaximumStateOfChargePercentKey => CreateSetting(definition, "Planungs-Maximum", "Begrenzt Netzladen, damit noch Platz fuer moeglichen PV-Ueberschuss bleibt.", "Battery", "number", "%"),
            ControllerSettingDefaults.BatteryWearCostPerKwhKey => CreateSetting(definition, "Verschleisskosten", "Alterungskosten je kWh, die eine zusaetzliche Netzladung den Akku kostet (Akkupreis / (nutzbare kWh x Zyklen)). Netzladung lohnt sich nur, wenn der Preisabstand Verluste und Verschleiss deckt.", "Battery", "number", "EUR/kWh"),
            ControllerSettingDefaults.DecisionMinimumSavingsPerKwhKey => CreateSetting(definition, "Mindestvorteil", "Mindestersparnis je kWh, ab der Energie zurueckgehalten oder aus dem Netz geladen wird. Kleinere Unterschiede liegen im Rauschen der Prognosen.", "Decision", "number", "EUR/kWh"),
            ControllerSettingDefaults.BatteryTemporaryStateOfChargePercentKey => CreateSetting(definition, "Temporarer SoC", "Ersatzwert fuer den Akkustand, solange noch keine echte Victron-Telemetrie genutzt wird.", "TelemetryFallback", "number", "%"),
            ControllerSettingDefaults.TelemetryTemporaryGridImportWattsKey => CreateSetting(definition, "Temporarer Netzbezug", "Ersatzwert fuer Live-Netzbezug, solange noch keine echte Victron-Telemetrie genutzt wird.", "TelemetryFallback", "number", "W"),
            ControllerSettingDefaults.TelemetryTemporaryPvProductionWattsKey => CreateSetting(definition, "Temporare PV-Leistung", "Ersatzwert fuer Live-PV-Leistung, solange noch keine echte Victron-Telemetrie genutzt wird.", "TelemetryFallback", "number", "W"),
            ControllerSettingDefaults.TelemetryGridPowerDeadbandWattsKey => CreateSetting(definition, "Netzleistungs-Puffer", "Kleine positive oder negative Netzleistungen innerhalb dieses Bereichs werden ignoriert, damit Messrauschen keine Lade- oder Entladewechsel ausloest.", "RealtimeControl", "number", "W"),
            ControllerSettingDefaults.TelemetryLiveSampleRetentionDaysKey => CreateSetting(definition, "Live-Sample-Aufbewahrung", "Anzahl Tage, fuer die rohe Live-Messwerte fuer Forecast und Ersparnisberechnung vorgehalten werden.", "RealtimeControl", "number", "days"),
            ControllerSettingDefaults.TelemetryGridImportSourceKey => CreateSetting(definition, "Quelle Netzbezug", "Waehlt die Quelle fuer den Live-Netzbezug.", "EnergyDevice", "select", null),
            ControllerSettingDefaults.TelemetryPvProductionSourceKey => CreateSetting(definition, "Quelle PV-Leistung", "Waehlt die Quelle fuer die Live-PV-Leistung.", "EnergyDevice", "select", null),
            ControllerSettingDefaults.TelemetryBatterySocSourceKey => CreateSetting(definition, "Quelle Akku-SoC", "Waehlt die Quelle fuer den Live-Akkuladestand.", "EnergyDevice", "select", null),
            ControllerSettingDefaults.GridFeedInCompensationPricePerKwhKey => CreateSetting(definition, "Einspeiseverguetung", "Verguetung je eingespeister Kilowattstunde. Dient auch fuer PV-Puffer-Entscheidungen.", "Economy", "number", "EUR/kWh"),
            ControllerSettingDefaults.ForecastHorizonHoursKey => CreateSetting(definition, "Forecast-Horizont", "Zeitraum, fuer den der Forecast im Voraus berechnet wird.", "Forecast", "number", "h"),
            ControllerSettingDefaults.DecisionLogRetentionDaysKey => CreateSetting(definition, "Log-Aufbewahrung", "Anzahl Tage, fuer die Direktentscheidungen gespeichert bleiben.", "Observability", "number", "days"),
            ControllerSettingDefaults.DecisionWorkerIntervalSecondsKey => CreateSetting(definition, "Worker-Intervall", "Intervall fuer den Hintergrund-Worker, der zyklisch neue Entscheidungen berechnet.", "Observability", "number", "s"),
            ControllerSettingDefaults.DecisionDirectionChangeHoldCyclesKey => CreateSetting(definition, "Richtungswechsel-Wartezyklen", "Anzahl Worker-Zyklen, fuer die kleine Gegenentscheidungen nach einer starken Gegenrichtung zu Idle geglaettet werden.", "RealtimeControl", "number", "cycles"),
            ControllerSettingDefaults.DecisionDirectionChangeMinimumPreviousPowerWattsKey => CreateSetting(definition, "Richtungswechsel Altleistung", "Mindestleistung der vorherigen Gegenrichtung, ab der die Richtungswechsel-Glaettung greift.", "RealtimeControl", "number", "W"),
            ControllerSettingDefaults.DecisionDirectionChangeMaximumNewPowerWattsKey => CreateSetting(definition, "Richtungswechsel Neuleistung", "Maximale Leistung der neuen Gegenentscheidung, die nach einer starken Gegenrichtung noch zu Idle geglaettet wird.", "RealtimeControl", "number", "W"),
            ControllerSettingDefaults.DashboardAutoRefreshIntervalSecondsKey => CreateSetting(definition, "Dashboard-Aktualisierung", "Intervall fuer die automatische Dashboard-Aktualisierung.", "Observability", "number", "s"),
            ControllerSettingDefaults.WorkerFailureEmailEnabledKey => CreateSetting(definition, "Fehlermails aktivieren", "Versendet bei Worker-Fehlern automatisch eine E-Mail an den Betreiber.", "Notifications", "boolean", null),
            ControllerSettingDefaults.WorkerFailureEmailSmtpHostKey => CreateSetting(definition, "SMTP-Host", "Hostname des SMTP-Servers fuer Fehlermails.", "Notifications", "text", null),
            ControllerSettingDefaults.WorkerFailureEmailSmtpPortKey => CreateSetting(definition, "SMTP-Port", "Port des SMTP-Servers fuer Fehlermails.", "Notifications", "number", null),
            ControllerSettingDefaults.WorkerFailureEmailSmtpUsernameKey => CreateSetting(definition, "SMTP-Benutzername", "Benutzername fuer den SMTP-Versand von Fehlermails.", "Notifications", "password", null),
            ControllerSettingDefaults.WorkerFailureEmailSmtpPasswordKey => CreateSetting(definition, "SMTP-Passwort", "Passwort fuer den SMTP-Versand von Fehlermails.", "Notifications", "password", null),
            ControllerSettingDefaults.WorkerFailureEmailFromAddressKey => CreateSetting(definition, "Absenderadresse", "Absenderadresse fuer automatische Fehlermails.", "Notifications", "text", null),
            ControllerSettingDefaults.WorkerFailureEmailToAddressKey => CreateSetting(definition, "Empfaengeradresse", "Empfaengeradresse fuer automatische Fehlermails.", "Notifications", "text", null),
            ControllerSettingDefaults.WorkerFailureEmailEnableSslKey => CreateSetting(definition, "SMTP mit SSL", "Aktiviert SSL/TLS fuer den SMTP-Versand.", "Notifications", "boolean", null),
            ControllerSettingDefaults.WorkerFailureEmailSubjectPrefixKey => CreateSetting(definition, "Betreff-Praefix", "Praefix fuer den Betreff automatischer Fehlermails.", "Notifications", "text", null),
            ControllerSettingDefaults.TibberAccessTokenKey => CreateSetting(definition, "Tibber Access Token", "Zugangsdaten fuer die Tibber API.", "Tibber", "password", null),
            ControllerSettingDefaults.ElectricityPriceProviderKey => CreateSetting(definition, "Strompreisquelle", "Ausgewaehlter Anbieter fuer Batterie, Warmwasser und Dashboard. Kein automatischer Anbieter-Fallback.", "Strompreise", "text", null),
            ControllerSettingDefaults.OctopusApiKeyKey => CreateSetting(definition, "Octopus API-Key", "Persoenlicher API-Key fuer Octopus Deutschland. Kein Passwort und kein kurzlebiges Zugriffstoken.", "Octopus", "password", null),
            ControllerSettingDefaults.OctopusAccountNumberKey => CreateSetting(definition, "Octopus Kundennummer", "Kundennummer A-... mit dynamicOctopus-Vertrag; vor Aktivierung konfigurieren.", "Octopus", "text", null),
            ControllerSettingDefaults.TibberApiEndpointKey => CreateSetting(definition, "Tibber API Endpoint", "GraphQL-Endpunkt der Tibber API.", "Tibber", "text", null),
            ControllerSettingDefaults.TibberHomeSelectionKey => CreateSetting(definition, "Tibber Home Selection", "Waehlt den zu nutzenden Tibber-Hausdatensatz.", "Tibber", "text", null),
            ControllerSettingDefaults.PvForecastProviderKey => CreateSetting(definition, "PV-Forecast Provider", "Technischer Provider fuer die PV-Ertragsprognose.", "Forecast", "text", null),
            ControllerSettingDefaults.PvForecastApiEndpointKey => CreateSetting(definition, "PV-Forecast Endpoint", "API-Endpunkt des PV-Prognose-Providers.", "Forecast", "text", null),
            ControllerSettingDefaults.PvForecastApiKeyKey => CreateSetting(definition, "Forecast.Solar API Key", "Optionaler API-Key fuer bezahlte Forecast.Solar Plaene mit laengerem Horizont und hoeherer Aufloesung.", "Forecast", "password", null),
            ControllerSettingDefaults.PvForecastLatitudeKey => CreateSetting(definition, "Breitengrad", "Standort der PV-Anlage.", "Forecast", "number", "deg"),
            ControllerSettingDefaults.PvForecastLongitudeKey => CreateSetting(definition, "Laengengrad", "Standort der PV-Anlage.", "Forecast", "number", "deg"),
            ControllerSettingDefaults.PvForecastPeakPowerKwpKey => CreateSetting(definition, "PV-Peakleistung", "Installierte Modulleistung je Dachflaeche, mehrere Flaechen kommagetrennt (z. B. 8,8).", "Forecast", "text", "kWp"),
            ControllerSettingDefaults.PvForecastDeclinationDegreesKey => CreateSetting(definition, "Modulneigung", "Neigung der PV-Module je Dachflaeche, kommagetrennt; ein Wert gilt fuer alle Flaechen.", "Forecast", "text", "deg"),
            ControllerSettingDefaults.PvForecastAzimuthDegreesKey => CreateSetting(definition, "Modulausrichtung", "Azimut je Dachflaeche, kommagetrennt (0 = Sued, -90 = Ost, 90 = West; z. B. -75,105).", "Forecast", "text", "deg"),
            ControllerSettingDefaults.PvForecastTimeZoneKey => CreateSetting(definition, "PV-Zeitzone", "Zeitzone fuer die PV-Prognose.", "Forecast", "text", null),
            ControllerSettingDefaults.ConsumptionForecastAverageDailyConsumptionKwhKey => CreateSetting(definition, "Tagesverbrauch", "Durchschnittlicher Tagesverbrauch fuer den Verbrauchsforecast.", "Forecast", "number", "kWh"),
            ControllerSettingDefaults.ConsumptionForecastTimeZoneKey => CreateSetting(definition, "Verbrauchs-Zeitzone", "Zeitzone fuer die Verbrauchsprofile.", "Forecast", "text", null),
            ControllerSettingDefaults.VictronHostKey => CreateSetting(definition, "Victron Host", "MQTT-Host des Victron-Systems.", "VictronMqtt", "text", null),
            ControllerSettingDefaults.VictronPortKey => CreateSetting(definition, "Victron Port", "MQTT-Port des Victron-Systems.", "VictronMqtt", "number", null),
            ControllerSettingDefaults.VictronPortalIdKey => CreateSetting(definition, "Victron Portal ID", "Portal-ID fuer die Topic-Aufloesung.", "VictronMqtt", "text", null),
            ControllerSettingDefaults.VictronKeepAliveSecondsKey => CreateSetting(definition, "Victron KeepAlive", "KeepAlive-Intervall fuer die MQTT-Verbindung.", "VictronMqtt", "number", "s"),
            ControllerSettingDefaults.VictronStaleAfterSecondsKey => CreateSetting(definition, "Victron Stale-Grenze", "Nach dieser Zeit gelten Live-Daten als veraltet.", "VictronMqtt", "number", "s"),
            ControllerSettingDefaults.VictronDryRunKey => CreateSetting(definition, "Victron DryRun", "Schaltet spaetere Schreiboperationen auf reine Simulation.", "VictronMqtt", "boolean", null),
            ControllerSettingDefaults.VictronControlModeKey => CreateSetting(definition, "Victron Steuermodus", "Waehlt, ob der Controller normales ESS per CGwacs-Setpoint oder ESS Externe Steuerung per Hub4-Phasensetpoints nutzt.", "VictronMqtt", "select", null),
            ControllerSettingDefaults.VictronTopicGridPowerKey => CreateSetting(definition, "Topic Netzleistung", "MQTT-Topic fuer Netzleistung.", "VictronMqtt", "text", null),
            ControllerSettingDefaults.VictronTopicBatterySocKey => CreateSetting(definition, "Topic Akku-SoC", "MQTT-Topic fuer den Akkuladestand.", "VictronMqtt", "text", null),
            ControllerSettingDefaults.VictronTopicBatteryPowerKey => CreateSetting(definition, "Topic Akku-Leistung", "MQTT-Topic fuer Akku-Lade- oder Entladeleistung.", "VictronMqtt", "text", null),
            ControllerSettingDefaults.VictronTopicHouseConsumptionKey => CreateSetting(definition, "Topic Hausverbrauch", "MQTT-Topics fuer den Hausverbrauch, mehrere Phasen kommagetrennt (Werte werden summiert). Negative Werte koennen PV-Ueberschuss bedeuten.", "VictronMqtt", "text", null),
            ControllerSettingDefaults.VictronWriteTopicChargeDischargeSetpointKey => CreateSetting(definition, "Write-Topic Setpoint", "MQTT-Topic fuer spaetere Lade- und Entladevorgaben.", "VictronMqtt", "text", null),
            ControllerSettingDefaults.VictronWriteTopicHub4ModeKey => CreateSetting(definition, "Write-Topic Hub4Mode", "MQTT-Topic fuer den ESS-Modus. Wert 3 aktiviert Externe Steuerung.", "VictronMqtt", "text", null),
            ControllerSettingDefaults.VictronExternalEssPhaseCountKey => CreateSetting(definition, "External ESS Phasen", "Anzahl der Phasen, auf die der Hub4-AcPowerSetpoint verteilt wird.", "VictronMqtt", "number", null),
            ControllerSettingDefaults.VictronExternalEssSwitchModeViaMqttKey => CreateSetting(definition, "ESS-Modus per MQTT setzen", "Setzt beim Veroeffentlichen den Cerbo-ESS-Modus passend zum Victron-Steuermodus.", "VictronMqtt", "boolean", null),
            ControllerSettingDefaults.VictronExternalEssL1AcPowerSetpointTopicKey => CreateSetting(definition, "External ESS Topic L1", "Hub4-Write-Topic fuer den L1-AcPowerSetpoint im ESS-Modus Externe Steuerung.", "VictronMqtt", "text", null),
            ControllerSettingDefaults.VictronExternalEssL2AcPowerSetpointTopicKey => CreateSetting(definition, "External ESS Topic L2", "Hub4-Write-Topic fuer den L2-AcPowerSetpoint im ESS-Modus Externe Steuerung.", "VictronMqtt", "text", null),
            ControllerSettingDefaults.VictronExternalEssL3AcPowerSetpointTopicKey => CreateSetting(definition, "External ESS Topic L3", "Hub4-Write-Topic fuer den L3-AcPowerSetpoint im ESS-Modus Externe Steuerung.", "VictronMqtt", "text", null),
            ControllerSettingDefaults.VictronWriteTopicDisableChargeKey => CreateSetting(definition, "Write-Topic DisableCharge", "MQTT-Topic fuer das Hub4-Ladesperrflag.", "VictronMqtt", "text", null),
            ControllerSettingDefaults.VictronWriteTopicDisableFeedInKey => CreateSetting(definition, "Write-Topic DisableFeedIn", "MQTT-Topic fuer das Hub4-Entlade-/FeedIn-Sperrflag.", "VictronMqtt", "text", null),
            ControllerSettingDefaults.VictronBatteryIdleThresholdWattsKey => CreateSetting(definition, "Akku-Stillstandsschwelle", "Innerhalb dieser Zielleistung setzt der Controller DisableCharge und DisableFeedIn fuer echten Akku-Stillstand.", "VictronMqtt", "number", "W"),
            ControllerSettingDefaults.HagerEnergyApiBaseUrlKey => CreateSetting(definition, "E3/DC API Base URL", "Basis-URL der Hager Energy Cloud API.", "E3dc", "text", null),
            ControllerSettingDefaults.HagerEnergyTokenEndpointKey => CreateSetting(definition, "E3/DC Token Endpoint", "Token-Endpunkt fuer den Client Credentials Flow.", "E3dc", "text", null),
            ControllerSettingDefaults.HagerEnergyClientIdKey => CreateSetting(definition, "E3/DC Client ID", "Client-ID aus dem E3/DC-API-Onboarding.", "E3dc", "password", null),
            ControllerSettingDefaults.HagerEnergyClientSecretKey => CreateSetting(definition, "E3/DC Client Secret", "Client-Secret aus dem E3/DC-API-Onboarding.", "E3dc", "password", null),
            ControllerSettingDefaults.HagerEnergyInstallationIdKey => CreateSetting(definition, "E3/DC Installation ID", "Installation-ID der Anlage. Unter /api/e3dc/installations abrufbar.", "E3dc", "text", null),
            ControllerSettingDefaults.HagerEnergyGridImportJsonPathKey => CreateSetting(definition, "JSON-Pfad Netzbezug", "JSON-Pfad zum Netzbezugswert in Watt. Leer lassen fuer automatische Erkennung.", "E3dc", "text", null),
            ControllerSettingDefaults.HagerEnergyPvProductionJsonPathKey => CreateSetting(definition, "JSON-Pfad PV-Leistung", "JSON-Pfad zur PV-Leistung in Watt. Leer lassen fuer automatische Erkennung.", "E3dc", "text", null),
            ControllerSettingDefaults.HagerEnergyBatterySocJsonPathKey => CreateSetting(definition, "JSON-Pfad Akku-SoC", "JSON-Pfad zum Akkuladestand in Prozent. Leer lassen fuer automatische Erkennung.", "E3dc", "text", null),
            ControllerSettingDefaults.HagerEnergyPollingIntervalSecondsKey => CreateSetting(definition, "E3/DC Polling-Intervall", "Abfrageintervall fuer E3/DC-Telemetrie. Minimum 10 Sekunden.", "E3dc", "number", "s"),
            ControllerSettingDefaults.HomematicEnabledKey => CreateSetting(definition, "Homematic-Erfassung aktiv", "Speichert Raumtemperaturen, Sollwerte und Ventilstellungen aus der CCU fuer die Heizkurven-Auswertung. Nur lesend.", "Homematic", "boolean", null),
            ControllerSettingDefaults.HomematicBaseUrlKey => CreateSetting(definition, "CCU-Adresse", "Basis-URL der OpenCCU mit installierter XML-API, z. B. http://homematic-raspi.", "Homematic", "text", null),
            ControllerSettingDefaults.HomematicApiTokenKey => CreateSetting(definition, "XML-API-Token", "Token aus tokenregister.cgi der XML-API. Wird nie im Klartext angezeigt.", "Homematic", "password", null),
            ControllerSettingDefaults.HomematicPollingIntervalSecondsKey => CreateSetting(definition, "Homematic Abfrageintervall", "Abstand zwischen zwei Abrufen. Minimum 60 Sekunden.", "Homematic", "number", "s"),
            ControllerSettingDefaults.HomematicSampleRetentionDaysKey => CreateSetting(definition, "Homematic-Aufbewahrung", "Anzahl Tage, fuer die Homematic-Klimadaten gespeichert bleiben. 400 Tage erlauben den Vergleich mit der Vorjahres-Heizperiode.", "Homematic", "number", "days"),
            ControllerSettingDefaults.MqttHostKey => CreateSetting(definition, "Allgemeiner MQTT Host", "Allgemeine MQTT-Grundeinstellung des Controllers.", "Infrastructure", "text", null),
            ControllerSettingDefaults.MqttPortKey => CreateSetting(definition, "Allgemeiner MQTT Port", "Allgemeine MQTT-Port-Einstellung des Controllers.", "Infrastructure", "number", null),
            ControllerSettingDefaults.MqttUsernameKey => CreateSetting(definition, "Allgemeiner MQTT Benutzer", "Allgemeiner MQTT-Benutzername des Controllers.", "Infrastructure", "text", null),
            ControllerSettingDefaults.MqttPasswordKey => CreateSetting(definition, "Allgemeines MQTT Passwort", "Allgemeines MQTT-Passwort des Controllers.", "Infrastructure", "password", null),
            _ => CreateSetting(definition, definition.Key, "Noch keine Endnutzerbeschreibung gepflegt.", "General", "text", null)
        };
    }

    private static IReadOnlyList<DecisionRuleMetadataDto> CreateDecisionRules()
    {
        return
        [
            CreateRule(BatteryForecastRuleIds.PvSurplusCharge, "PV-Ueberschuss laden", "PV-Ueberschuss wird bevorzugt in den Akku geladen.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.BatteryFullPvSurplus, "Akku voll bei PV-Ueberschuss", "PV-Ueberschuss ist vorhanden, aber der Akku ist bereits voll.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.PreserveHeadroomForNegativePrice, "Kapazitaet fuer negative Preise freihalten", "PV-Ladung wird verworfen, weil spaeteres Laden zu negativen Preisen wirtschaftlich wertvoller ist.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.NegativePriceGridCharge, "Negativpreis laden", "Bei negativem Strompreis wird aus dem Netz geladen.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.PlannedGridCharge, "Bedarfsgerechte Netzladung", "Es wird nur so viel aus dem Netz geladen, wie spaeter in einem deutlich teureren Slot fehlen wuerde, und nur ohne PV-Ueberschuss zu verdraengen.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.HoldForExpensiveSlot, "Akkuenergie fuer teureren Slot halten", "Der Akku deckt den Verbrauch jetzt nicht, weil die Energie spaeter einen deutlich teureren Netzbezug vermeidet.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.SelfConsumptionDischarge, "Verbrauch aus dem Akku decken", "Der Akku deckt den erwarteten Verbrauch, solange er ueber der Planungsreserve liegt.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.DischargeBeforeNegativePriceWindow, "Vor Negativpreisfenster entladen", "Vor einem spaeteren sehr guenstigen Ladefenster wird Akkuenergie genutzt, um Platz zu schaffen.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.DischargeForFuturePvHeadroom, "Entladen fuer PV-Puffer", "Vor erwartetem PV-Ueberschuss wird Last aus dem Akku gedeckt, wenn Netzstrom teurer als die Einspeiseverguetung ist.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.ExpensivePriceDischarge, "Teuren Preis entladen", "Bei hohem Preis wird Last aus dem Akku gedeckt.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.MinimumSocReserve, "Mindestreserve schuetzen", "Weitere Entladung wuerde die Planungs- oder Mindestreserve verletzen.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.EndSocReserve, "Endreserve (entfallen)", "Regel wird nicht mehr erzeugt; kann nur in alten Protokolleintraegen vorkommen.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.PlanningMaximumGridChargeLimit, "Netzladen durch Planungs-Maximum begrenzt", "Netzladen wird begrenzt, damit noch Platz fuer moeglichen PV-Ueberschuss bleibt.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.PlanningMaximumSocHeadroom, "Netzladen durch Planungs-Maximum verhindert", "Netzladen wird ganz verworfen, weil das Planungs-Maximum bereits erreicht ist.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.WaitForNegativePriceWindow, "Auf besseres Preisfenster warten", "Es wird nicht geladen, weil spaeter ein besseres Preisfenster erwartet wird.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.BatteryFullIdle, "Akku voll", "Der Akku ist voll und kann nicht weiter geladen werden.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.NeutralIdle, "Keine wirtschaftliche Aktion", "Der Slot loest keine bessere Batterieaktion aus.", "Forecast"),
            CreateRule(CurrentBatteryDecisionRuleIds.MissingBatteryState, "Live-SoC fehlt", "Es liegt noch kein verwendbarer Live-Akkuladestand fuer eine sichere Direktentscheidung vor.", "Realtime"),
            CreateRule(CurrentBatteryDecisionRuleIds.MissingSiteTelemetry, "Live-Telemetrie fehlt", "Netzbezug, Hausverbrauch oder PV-Ableitung fehlen fuer eine sichere Direktentscheidung.", "Realtime"),
            CreateRule(CurrentBatteryDecisionRuleIds.StaleBatteryState, "Live-SoC veraltet", "Die aktuelle SoC-Messung ist zu alt fuer eine sichere Direktentscheidung.", "Realtime"),
            CreateRule(CurrentBatteryDecisionRuleIds.StaleSiteTelemetry, "Live-Telemetrie veraltet", "Netz- oder PV-Daten sind zu alt fuer eine sichere Direktentscheidung.", "Realtime"),
            CreateRule(CurrentBatteryDecisionRuleIds.InvalidSiteTelemetry, "Unplausible Live-Telemetrie", "Die eingehenden Leistungswerte passen nicht zu plausiblen Live-Daten.", "Realtime"),
            CreateRule(CurrentBatteryDecisionRuleIds.MissingCurrentPrice, "Aktueller Preis fehlt", "Fuer den aktuellen Zeitpunkt liegt kein verwendbarer Strompreis vor.", "Realtime"),
            CreateRule(CurrentBatteryDecisionRuleIds.GridPowerDeadband, "Netzleistung im Puffer", "Kleine Netzbezugs- oder Einspeisewerte werden ignoriert, damit die Regelung nicht wegen weniger Watt pendelt.", "Realtime"),
            CreateRule(CurrentBatteryDecisionRuleIds.AbsorbGridExport, "Netzexport aufnehmen", "Aktueller Export wird in die Batterie geladen, damit moeglichst nicht eingespeist wird.", "Realtime"),
            CreateRule(CurrentBatteryDecisionRuleIds.SupplementaryDischargeExport, "Export aus Zweitspeicher ignorieren", "Einspeisung, die vom entladenden E3/DC-Akku stammt, wird nicht in den Victron-Akku geladen.", "Realtime"),
            CreateRule(CurrentBatteryDecisionRuleIds.NoGridImportForDischarge, "Kein Entladen ohne Netzbezug", "Ohne Netzbezug wird nicht entladen, um Einspeisung zu vermeiden.", "Realtime"),
            CreateRule(CurrentBatteryDecisionRuleIds.BatteryFull, "Akku voll", "Der Akku ist fuer die aktuelle Aktion bereits voll.", "Realtime")
        ];
    }

    private static SettingMetadataDto CreateSetting(
        ControllerSettingDefinition definition,
        string displayName,
        string description,
        string group,
        string inputKind,
        string? unit)
    {
        return new SettingMetadataDto(
            definition.Key,
            displayName,
            description,
            group,
            inputKind,
            unit,
            definition.Sensitivity == ControllerSettingSensitivity.Sensitive,
            definition.Sensitivity == ControllerSettingSensitivity.Sensitive ? null : definition.DefaultValue);
    }

    private static DecisionRuleMetadataDto CreateRule(string ruleId, string displayName, string description, string category)
    {
        return new DecisionRuleMetadataDto(ruleId, displayName, description, category);
    }
}
