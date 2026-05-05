namespace TibberVictronController.Dal.Weather;

/// <summary>
/// Represents a Forecast.Solar API or mapping failure.
/// </summary>
public sealed class ForecastSolarApiException : Exception
{
    public ForecastSolarApiException(string message)
        : base(message)
    {
    }

    public ForecastSolarApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
