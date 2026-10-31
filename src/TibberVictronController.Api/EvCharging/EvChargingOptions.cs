namespace TibberVictronController.Api.EvCharging;

/// <summary>Host-level wallbox settings; changing the port needs a restart, so it is not a runtime setting.</summary>
public sealed class EvChargingOptions
{
    public const string SectionName = "EvCharging";

    /// <summary>Separate LAN port for OCPP; 0 disables the listener. Tailscale Funnel only publishes port 80.</summary>
    public int OcppListenPort { get; init; }

    public static EvChargingOptions Read(IConfiguration configuration)
    {
        var options = configuration.GetSection(SectionName).Get<EvChargingOptions>() ?? new EvChargingOptions();
        if (options.OcppListenPort is < 0 or > 65535)
            throw new InvalidOperationException("EvCharging:OcppListenPort muss zwischen 0 und 65535 liegen.");
        return options;
    }
}
