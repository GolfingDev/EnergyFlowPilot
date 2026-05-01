using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Describes the cheapest forecast slots required to fill the battery.
/// </summary>
public sealed record BatteryChargeWindowPlan(
    decimal RequiredEnergyKwh,
    TimeSpan RequiredChargeDuration,
    IReadOnlyList<ForecastTimeSlot> PlannedChargeSlots);
