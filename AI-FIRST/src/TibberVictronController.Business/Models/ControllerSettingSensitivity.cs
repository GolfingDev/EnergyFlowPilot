namespace TibberVictronController.Business.Models;

/// <summary>
/// Defines whether a persisted controller setting may be exposed to frontend read contracts.
/// </summary>
public enum ControllerSettingSensitivity
{
    Normal = 1,
    Sensitive = 2
}
