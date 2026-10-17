using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Entities;

public sealed class HomematicClimateSampleEntity
{
    public long Id { get; set; }

    public DateTimeOffset PolledAtUtc { get; set; }

    public DateTimeOffset? LastChangedAtUtc { get; set; }

    public string ChannelAddress { get; set; } = string.Empty;

    public string DeviceType { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public string ChannelName { get; set; } = string.Empty;

    public HomematicClimateSampleKind Kind { get; set; }

    public decimal? ActualTemperatureCelsius { get; set; }

    public decimal? SetPointTemperatureCelsius { get; set; }

    public HomematicSetPointMode? SetPointMode { get; set; }

    public decimal? ValveOpeningPercent { get; set; }

    public decimal? RelativeHumidityPercent { get; set; }

    public bool? IsWindowOpen { get; set; }

    public bool IsReachable { get; set; }
}
