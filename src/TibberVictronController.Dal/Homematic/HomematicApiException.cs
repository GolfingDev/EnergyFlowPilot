namespace TibberVictronController.Dal.Homematic;

public sealed class HomematicApiException : Exception
{
    public HomematicApiException(string message)
        : base(message)
    {
    }

    public HomematicApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
