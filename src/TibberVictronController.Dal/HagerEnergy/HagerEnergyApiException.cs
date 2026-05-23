namespace TibberVictronController.Dal.HagerEnergy;

public sealed class HagerEnergyApiException : Exception
{
    public HagerEnergyApiException(string message)
        : base(message)
    {
    }

    public HagerEnergyApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
