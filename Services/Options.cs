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
    public double ChargeEfficiency { get; set; } = 0.96;
    public double DischargeEfficiency { get; set; } = 0.96;
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

public class PlanningOptions
{
    public int PlanHorizonHours { get; set; } = 24;
    public int SlotResolutionMinutes { get; set; } = 15;
    public double MinChargePowerWatts { get; set; } = 500;
    public double MinDischargePowerWatts { get; set; } = 300;
    public double CheapQuantile { get; set; } = 0.25;
    public double ExpensiveQuantile { get; set; } = 0.75;
    public decimal MaxGridChargePrice { get; set; } = 0.24m;
    public double BaseReserveKwh { get; set; } = 0.75;
    public double MorningReserveKwh { get; set; } = 1.25;
    public double EveningReserveKwh { get; set; } = 1.75;
    public double PVPrefillHeadroomKwh { get; set; } = 1.5;
    public double ReserveAggressiveness { get; set; } = 1.0;
    public double CheapChargeAggressiveness { get; set; } = 1.15;
    public double ExpensiveDischargeAggressiveness { get; set; } = 1.0;
    public bool EnableGridCharging { get; set; } = true;
    public bool PreferPvOverGrid { get; set; } = true;

    public int PlanHorizonSlots => Math.Max(1, PlanHorizonHours * SlotsPerHour);
    public int SlotsPerHour => Math.Max(1, 60 / Math.Max(1, SlotResolutionMinutes));
    public double SlotDurationHours => SlotResolutionMinutes / 60.0;
}

public class ForecastOptions
{
    public double DefaultHouseConsumptionWatts { get; set; } = 500;
    public double HistoricalLoadWeight { get; set; } = 0.55;
    public double DefaultPvScaling { get; set; } = 1.0;
    public List<ForecastLoadBlock> LoadBlocks { get; set; } = new();
    public List<ForecastPvBlock> PvBlocks { get; set; } = new();
}

public class ForecastLoadBlock
{
    public int StartHour { get; set; }
    public int EndHour { get; set; }
    public double HouseConsumptionWatts { get; set; }
}

public class ForecastPvBlock
{
    public int StartHour { get; set; }
    public int EndHour { get; set; }
    public double PvWatts { get; set; }
}

public class WeatherForecastOptions
{
    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = "OpenMeteo";
    public string BaseUrl { get; set; } = "https://api.open-meteo.com";
    public string Model { get; set; } = "dwd_icon";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int ForecastHours { get; set; } = 48;
    public int CacheMinutes { get; set; } = 15;
    public int RequestTimeoutSeconds { get; set; } = 20;
    public string Timezone { get; set; } = "UTC";
    public double PanelTiltDegrees { get; set; } = 35;
    public double PanelAzimuthDegrees { get; set; } = 0;
    public double PvSystemKwPeak { get; set; } = 18.0;
    public double PerformanceRatio { get; set; } = 0.82;
    public double AdditionalLossFactor { get; set; } = 1.0;
    public double MaxAcPowerWatts { get; set; } = 15000;
    public double MinimumUsefulIrradianceWattsPerSquareMeter { get; set; } = 25;
}

public class TibberOptions
{
    public string AccessToken { get; set; } = string.Empty;
    public string HomeId { get; set; } = string.Empty;
    public int PriceLookaheadHours { get; set; } = 36;
    public int CacheSeconds { get; set; } = 300;
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
