namespace TibberVictronController.Dal.Tibber;

/// <summary>
/// Represents an explicit Tibber API failure that must not be handled as a silent fallback.
/// </summary>
public sealed class TibberApiException : Exception
{
    public TibberApiException(string message)
        : base(message)
    {
    }

    public TibberApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
