namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Contains stable rule identifiers used by the live direct decision path.
/// </summary>
public static class CurrentBatteryDecisionRuleIds
{
    public const string MissingBatteryState = "MISSING_BATTERY_STATE";
    public const string MissingSiteTelemetry = "MISSING_SITE_TELEMETRY";
    public const string StaleBatteryState = "STALE_BATTERY_STATE";
    public const string StaleSiteTelemetry = "STALE_SITE_TELEMETRY";
    public const string InvalidSiteTelemetry = "INVALID_SITE_TELEMETRY";
    public const string MissingCurrentPrice = "MISSING_CURRENT_PRICE";
    public const string GridPowerDeadband = "GRID_POWER_DEADBAND";
    public const string AbsorbGridExport = "ABSORB_GRID_EXPORT";
    public const string ManualGridCharge = "MANUAL_GRID_CHARGE";
    public const string NoGridImportForDischarge = "NO_GRID_IMPORT_FOR_DISCHARGE";
    public const string BatteryFull = "BATTERY_FULL";
}
