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
            ControllerSettingDefaults.BatteryTotalCapacityKwhKey => CreateSetting(definition, "Batteriekapazitaet", "Gesamtkapazitaet des Akkus.", "Battery", "number", "kWh"),
            ControllerSettingDefaults.BatteryMinimumStateOfChargePercentKey => CreateSetting(definition, "Minimaler SoC", "Harte Untergrenze zum Schutz des Akkus.", "Battery", "number", "%"),
            ControllerSettingDefaults.BatteryMaximumChargePowerWattsKey => CreateSetting(definition, "Maximale Ladeleistung", "Hoechste Ladeleistung, die die Decision Engine einplanen darf.", "Battery", "number", "W"),
            ControllerSettingDefaults.BatteryMaximumDischargePowerWattsKey => CreateSetting(definition, "Maximale Entladeleistung", "Hoechste Entladeleistung, die die Decision Engine einplanen darf.", "Battery", "number", "W"),
            ControllerSettingDefaults.BatteryRoundTripEfficiencyPercentKey => CreateSetting(definition, "Wirkungsgrad", "Gesamter Lade- und Entladewirkungsgrad des Akkus.", "Battery", "number", "%"),
            ControllerSettingDefaults.BatteryTargetEndStateOfChargePercentKey => CreateSetting(definition, "Endreserve", "SoC, der am Ende des Planungshorizonts mindestens erhalten bleiben soll.", "Battery", "number", "%"),
            ControllerSettingDefaults.BatteryPlanningMinimumStateOfChargePercentKey => CreateSetting(definition, "Planungs-Minimum", "Weichere Reserve oberhalb der harten Akku-Untergrenze.", "Battery", "number", "%"),
            ControllerSettingDefaults.BatteryPlanningMaximumStateOfChargePercentKey => CreateSetting(definition, "Planungs-Maximum", "Begrenzt Netzladen, damit noch Platz fuer moeglichen PV-Ueberschuss bleibt.", "Battery", "number", "%"),
            ControllerSettingDefaults.BatteryTemporaryStateOfChargePercentKey => CreateSetting(definition, "Temporarer SoC", "Ersatzwert fuer den Akkustand, solange noch keine echte Victron-Telemetrie genutzt wird.", "TelemetryFallback", "number", "%"),
            ControllerSettingDefaults.TelemetryTemporaryGridImportWattsKey => CreateSetting(definition, "Temporarer Netzbezug", "Ersatzwert fuer Live-Netzbezug, solange noch keine echte Victron-Telemetrie genutzt wird.", "TelemetryFallback", "number", "W"),
            ControllerSettingDefaults.TelemetryTemporaryPvProductionWattsKey => CreateSetting(definition, "Temporare PV-Leistung", "Ersatzwert fuer Live-PV-Leistung, solange noch keine echte Victron-Telemetrie genutzt wird.", "TelemetryFallback", "number", "W"),
            ControllerSettingDefaults.TelemetryGridPowerDeadbandWattsKey => CreateSetting(definition, "Netzleistungs-Puffer", "Kleine positive oder negative Netzleistungen innerhalb dieses Bereichs werden ignoriert, damit Messrauschen keine Lade- oder Entladewechsel ausloest.", "RealtimeControl", "number", "W"),
            ControllerSettingDefaults.TelemetryGridImportSourceKey => CreateSetting(definition, "Quelle Netzbezug", "Waehlt die Quelle fuer den Live-Netzbezug.", "EnergyDevice", "select", null),
            ControllerSettingDefaults.TelemetryPvProductionSourceKey => CreateSetting(definition, "Quelle PV-Leistung", "Waehlt die Quelle fuer die Live-PV-Leistung.", "EnergyDevice", "select", null),
            ControllerSettingDefaults.TelemetryBatterySocSourceKey => CreateSetting(definition, "Quelle Akku-SoC", "Waehlt die Quelle fuer den Live-Akkuladestand.", "EnergyDevice", "select", null),
            ControllerSettingDefaults.GridFeedInCompensationPricePerKwhKey => CreateSetting(definition, "Einspeiseverguetung", "Verguetung je eingespeister Kilowattstunde. Dient auch fuer PV-Puffer-Entscheidungen.", "Economy", "number", "EUR/kWh"),
            ControllerSettingDefaults.ForecastHorizonHoursKey => CreateSetting(definition, "Forecast-Horizont", "Zeitraum, fuer den der Forecast im Voraus berechnet wird.", "Forecast", "number", "h"),
            ControllerSettingDefaults.DecisionLogRetentionDaysKey => CreateSetting(definition, "Log-Aufbewahrung", "Anzahl Tage, fuer die Direktentscheidungen gespeichert bleiben.", "Observability", "number", "days"),
            ControllerSettingDefaults.DecisionWorkerIntervalSecondsKey => CreateSetting(definition, "Worker-Intervall", "Intervall fuer den Hintergrund-Worker, der zyklisch neue Entscheidungen berechnet.", "Observability", "number", "s"),
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
            ControllerSettingDefaults.TibberApiEndpointKey => CreateSetting(definition, "Tibber API Endpoint", "GraphQL-Endpunkt der Tibber API.", "Tibber", "text", null),
            ControllerSettingDefaults.TibberHomeSelectionKey => CreateSetting(definition, "Tibber Home Selection", "Waehlt den zu nutzenden Tibber-Hausdatensatz.", "Tibber", "text", null),
            ControllerSettingDefaults.PvForecastProviderKey => CreateSetting(definition, "PV-Forecast Provider", "Technischer Provider fuer die PV-Ertragsprognose.", "Forecast", "text", null),
            ControllerSettingDefaults.PvForecastApiEndpointKey => CreateSetting(definition, "PV-Forecast Endpoint", "API-Endpunkt des PV-Prognose-Providers.", "Forecast", "text", null),
            ControllerSettingDefaults.PvForecastApiKeyKey => CreateSetting(definition, "Forecast.Solar API Key", "Optionaler API-Key fuer bezahlte Forecast.Solar Plaene mit laengerem Horizont und hoeherer Aufloesung.", "Forecast", "password", null),
            ControllerSettingDefaults.PvForecastLatitudeKey => CreateSetting(definition, "Breitengrad", "Standort der PV-Anlage.", "Forecast", "number", "deg"),
            ControllerSettingDefaults.PvForecastLongitudeKey => CreateSetting(definition, "Laengengrad", "Standort der PV-Anlage.", "Forecast", "number", "deg"),
            ControllerSettingDefaults.PvForecastPeakPowerKwpKey => CreateSetting(definition, "PV-Peakleistung", "Installierte Modulleistung der PV-Anlage.", "Forecast", "number", "kWp"),
            ControllerSettingDefaults.PvForecastDeclinationDegreesKey => CreateSetting(definition, "Modulneigung", "Neigung der PV-Module.", "Forecast", "number", "deg"),
            ControllerSettingDefaults.PvForecastAzimuthDegreesKey => CreateSetting(definition, "Modulausrichtung", "Azimut der PV-Module.", "Forecast", "number", "deg"),
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
            ControllerSettingDefaults.VictronTopicHouseConsumptionKey => CreateSetting(definition, "Topic Hausverbrauch", "MQTT-Topic fuer den Hausverbrauch. Negative Werte koennen PV-Ueberschuss bedeuten.", "VictronMqtt", "text", null),
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
            ControllerSettingDefaults.HagerEnergyApiBaseUrlKey => CreateSetting(definition, "Hager Energy API Base URL", "Basis-URL fuer Hager Energy API Requests.", "HagerEnergy", "text", null),
            ControllerSettingDefaults.HagerEnergyAuthorizationEndpointKey => CreateSetting(definition, "Hager Energy Discovery URL", "Discovery-URL aus der Hager-Doku. Daraus werden Authorization- und Token-Endpunkt automatisch gelesen.", "HagerEnergy", "text", null),
            ControllerSettingDefaults.HagerEnergyTokenEndpointKey => CreateSetting(definition, "Hager Energy Token Endpoint", "Interner Fallback, falls keine Discovery-URL verwendet wird.", "HagerEnergy", "text", null),
            ControllerSettingDefaults.HagerEnergyRedirectUriKey => CreateSetting(definition, "Hager Energy Redirect URI", "Redirect URI, die beim OAuth-Client registriert ist.", "HagerEnergy", "text", null),
            ControllerSettingDefaults.HagerEnergyPostLoginRedirectUrlKey => CreateSetting(definition, "Hager Energy Ruecksprung URL", "Frontend-URL, zu der nach erfolgreichem Hager-Login zurueck navigiert wird.", "HagerEnergy", "text", null),
            ControllerSettingDefaults.HagerEnergyScopeKey => CreateSetting(definition, "Hager Energy Scope", "OAuth-Scopes fuer Hager Energy. Der Default ist read:storage fuer lesenden Speicherzugriff.", "HagerEnergy", "text", null),
            ControllerSettingDefaults.HagerEnergyOAuthStateKey => CreateSetting(definition, "Hager Energy OAuth State", "Interner Schutzwert fuer den laufenden OAuth-Login.", "HagerEnergy", "password", null),
            ControllerSettingDefaults.HagerEnergyApiKeyKey => CreateSetting(definition, "Hager Energy API Key", "Optionaler API-Key fuer den api_key Header, falls dein Hager-Energy-Client einen erhalten hat.", "HagerEnergy", "password", null),
            ControllerSettingDefaults.HagerEnergyClientIdKey => CreateSetting(definition, "Hager Energy Client ID", "OAuth-Client-ID aus dem Hager-Energy-Onboarding.", "HagerEnergy", "password", null),
            ControllerSettingDefaults.HagerEnergyClientSecretKey => CreateSetting(definition, "Hager Energy Client Secret", "OAuth-Client-Secret, falls dein Hager-Energy-Client eines nutzt.", "HagerEnergy", "password", null),
            ControllerSettingDefaults.HagerEnergyRefreshTokenKey => CreateSetting(definition, "Hager Energy Refresh Token", "Intern gespeichertes Refresh Token fuer dauerhafte API-Zugriffe.", "HagerEnergy", "password", null),
            ControllerSettingDefaults.HagerEnergyAccessTokenKey => CreateSetting(definition, "Hager Energy Access Token", "Intern gespeichertes kurzlebiges Access Token.", "HagerEnergy", "password", null),
            ControllerSettingDefaults.HagerEnergyInstallationIdKey => CreateSetting(definition, "Hager Energy Installation ID", "Installation-ID der E3/DC-Anlage in der Hager Energy API.", "HagerEnergy", "password", null),
            ControllerSettingDefaults.HagerEnergyGridImportJsonPathKey => CreateSetting(definition, "JSON-Pfad Netzbezug", "JSON-Pfad zum Netzbezugswert in Watt in der /energy/current Antwort.", "HagerEnergy", "text", null),
            ControllerSettingDefaults.HagerEnergyPvProductionJsonPathKey => CreateSetting(definition, "JSON-Pfad PV-Leistung", "JSON-Pfad zur PV-Leistung in Watt in der /energy/current Antwort.", "HagerEnergy", "text", null),
            ControllerSettingDefaults.HagerEnergyBatterySocJsonPathKey => CreateSetting(definition, "JSON-Pfad Akku-SoC", "JSON-Pfad zum Akkuladestand in Prozent in der /energy/current Antwort.", "HagerEnergy", "text", null),
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
            CreateRule(BatteryForecastRuleIds.NegativePriceGridCharge, "Negativpreis laden", "Bei negativem Tibber-Preis wird aus dem Netz geladen.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.PlannedGridCharge, "Geplanter Netz-Ladeslot", "Der Slot gehoert zu den guenstigsten geplanten Netz-Ladefenstern.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.DischargeBeforeNegativePriceWindow, "Vor Negativpreisfenster entladen", "Vor einem spaeteren sehr guenstigen Ladefenster wird Akkuenergie genutzt, um Platz zu schaffen.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.DischargeForFuturePvHeadroom, "Entladen fuer PV-Puffer", "Vor erwartetem PV-Ueberschuss wird Last aus dem Akku gedeckt, wenn Netzstrom teurer als die Einspeiseverguetung ist.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.ExpensivePriceDischarge, "Teuren Preis entladen", "Bei hohem Preis wird Last aus dem Akku gedeckt.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.MinimumSocReserve, "Mindestreserve schuetzen", "Weitere Entladung wuerde die Planungs- oder Mindestreserve verletzen.", "Forecast"),
            CreateRule(BatteryForecastRuleIds.EndSocReserve, "Endreserve schuetzen", "Weitere Entladung wuerde die konfigurierte Endreserve verletzen.", "Forecast"),
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
            CreateRule(CurrentBatteryDecisionRuleIds.MissingCurrentPrice, "Aktueller Preis fehlt", "Fuer den aktuellen Zeitpunkt liegt kein verwendbarer Tibber-Preis vor.", "Realtime"),
            CreateRule(CurrentBatteryDecisionRuleIds.GridPowerDeadband, "Netzleistung im Puffer", "Kleine Netzbezugs- oder Einspeisewerte werden ignoriert, damit die Regelung nicht wegen weniger Watt pendelt.", "Realtime"),
            CreateRule(CurrentBatteryDecisionRuleIds.AbsorbGridExport, "Netzexport aufnehmen", "Aktueller Export wird in die Batterie geladen, damit moeglichst nicht eingespeist wird.", "Realtime"),
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
