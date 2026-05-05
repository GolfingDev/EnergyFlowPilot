namespace TibberVictronController.Api.Diagnostics;

/// <summary>
/// Configures persistent API exception logging without adding an external logging package.
/// </summary>
public sealed class FileExceptionLogOptions
{
    public string LogDirectory { get; init; } = "logs/api-errors";

    public int RetentionDays { get; init; } = 14;
}
