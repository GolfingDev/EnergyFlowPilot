namespace TibberVictronController.Dal.Entities;

public sealed class WeishauptHotWaterControlStateEntity
{
    public int Id { get; set; }
    public string StateJson { get; set; } = string.Empty;
}
