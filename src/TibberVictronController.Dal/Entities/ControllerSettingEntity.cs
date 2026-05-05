using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Entities;

public sealed class ControllerSettingEntity
{
    public string Key { get; set; } = string.Empty;

    public string? Value { get; set; }

    public ControllerSettingSensitivity Sensitivity { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
