namespace TibberVictronController.Business.EvCharging;

/// <summary>
/// Status texts of a wallbox command that the wallbox did not answer itself. They are no refusal: the command may or
/// may not have arrived, so the control repeats it soon instead of waiting as after a refusal.
/// </summary>
public static class EvChargerCommandStatus
{
    /// <summary>The wallbox stayed connected but sent no answer in time.</summary>
    public const string NoAnswer = "KeineAntwort";

    /// <summary>The connection ended before the answer arrived, or the command could not be sent at all.</summary>
    public const string Disconnected = "VerbindungGetrennt";

    public static bool IsUnanswered(string? status) => status is NoAnswer or Disconnected;
}
