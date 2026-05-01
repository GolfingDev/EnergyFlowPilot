namespace TibberVictronController.Business.Models;

/// <summary>
/// Summarizes the controller state that can be shown in the frontend without exposing secrets.
/// </summary>
public sealed record ControllerStatusSnapshot(
    string Status,
    int KnownSettingsCount,
    int PersistedSettingsCount,
    int ConfiguredSensitiveSettingsCount,
    DateTimeOffset GeneratedAtUtc);
