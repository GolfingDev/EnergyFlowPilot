namespace TibberVictronController.Web.Services;

public class ControllerOptions
{
    public double MinSocPercent { get; set; } = 20;
    public double MaxSocPercent { get; set; } = 95;
    public double DischargeSocPercent { get; set; } = 35;
    public decimal BaseCheapPriceTolerance { get; set; } = 0.02m;
    public decimal BaseExpensivePriceMargin { get; set; } = 0.05m;
    public double MaxChargePowerWatts { get; set; } = 2500;
    public double MaxDischargePowerWatts { get; set; } = 2500;
    public double ReserveBufferKwh { get; set; } = 0.75;
    public double BatteryUsableCapacityKwh { get; set; } = 10.0;
    public int DecisionLoopSeconds { get; set; } = 10;

    public int LoopSeconds
    {
        get => DecisionLoopSeconds;
        set => DecisionLoopSeconds = value;
    }

    public decimal CheapPriceTolerance
    {
        get => BaseCheapPriceTolerance;
        set => BaseCheapPriceTolerance = value;
    }

    public decimal ExpensivePriceMargin
    {
        get => BaseExpensivePriceMargin;
        set => BaseExpensivePriceMargin = value;
    }
}

public class TibberOptions
{
    public string AccessToken { get; set; } = string.Empty;
    public string HomeId { get; set; } = string.Empty;
}

public class VictronOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 1883;
    public string PortalId { get; set; } = string.Empty;
    public bool DryRun { get; set; } = true;
    public int KeepAliveSeconds { get; set; } = 30;
    public int StaleAfterSeconds { get; set; } = 30;
    public VictronTopicOptions Topics { get; set; } = new();
    public VictronWriteTopicOptions WriteTopics { get; set; } = new();
}

public class VictronTopicOptions
{
    public string GridPower { get; set; } = string.Empty;
    public string BatterySoc { get; set; } = string.Empty;
    public string BatteryPower { get; set; } = string.Empty;
    public string HouseConsumption { get; set; } = string.Empty;
    public string PvPower { get; set; } = string.Empty;
}

public class VictronWriteTopicOptions
{
    public string Mode { get; set; } = string.Empty;
    public string ChargeState { get; set; } = string.Empty;
    public string ChargeDischargeSetpoint { get; set; } = string.Empty;
}
