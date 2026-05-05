namespace TibberVictronController.Business.Models;

/// <summary>
/// Represents a calculated Decision Engine forecast with slotwise projected battery state.
/// </summary>
public sealed record BatteryForecastResult(
    BatteryState InitialBatteryState,
    BatteryConfiguration BatteryConfiguration,
    IReadOnlyList<BatteryForecastEntry> Entries);
