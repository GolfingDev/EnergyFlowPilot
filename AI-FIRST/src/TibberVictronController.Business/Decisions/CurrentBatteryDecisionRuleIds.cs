namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Contains stable rule identifiers used by the live direct decision path.
/// </summary>
public static class CurrentBatteryDecisionRuleIds
{
    public const string StaleBatteryState = "STALE_BATTERY_STATE";
    public const string StaleSiteTelemetry = "STALE_SITE_TELEMETRY";
    public const string InvalidSiteTelemetry = "INVALID_SITE_TELEMETRY";
    public const string MissingCurrentPrice = "MISSING_CURRENT_PRICE";
    public const string AbsorbGridExport = "ABSORB_GRID_EXPORT";
    public const string NoGridImportForDischarge = "NO_GRID_IMPORT_FOR_DISCHARGE";
    public const string BatteryFull = "BATTERY_FULL";
}
