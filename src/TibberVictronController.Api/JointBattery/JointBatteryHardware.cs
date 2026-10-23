using System.Globalization;
using TibberVictronController.Api.Configuration;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Api.JointBattery;

/// <summary>Explicit command readback and fresh AC-side measurements for the shared execution coordinator.</summary>
public sealed class JointBatteryHardware(IServiceProvider services) : IJointBatteryHardware
{
    private IVictronMqttControlClient Client => services.GetRequiredService<IVictronMqttControlClient>();
    private IUtcClock Clock => services.GetRequiredService<IUtcClock>();

    private async Task<VictronMqttSettings> Settings(CancellationToken token)
    {
        var settings = await services.GetRequiredService<DatabaseVictronMqttSettingsProvider>().GetSettingsAsync(token);
        if (settings.ControlMode != VictronControlMode.ExternalEss)
            throw new InvalidOperationException("Gemeinsame Steuerung benoetigt den eingerichteten External-ESS-Modus.");
        return settings;
    }

    private async Task RefreshAsync(IReadOnlyList<string> topics, CancellationToken token)
    {
        var requested = Clock.UtcNow;
        foreach (var topic in topics) await Client.RequestValueAsync(topic, token);
        var until = Clock.UtcNow.AddSeconds(1.5);
        while (topics.Any(t => !Client.TryGetLatestSample(t, out _, out var at) || at < requested))
        {
            if (Clock.UtcNow >= until) throw new TimeoutException("Victron bestaetigt die aktuellen Leseanfragen nicht.");
            await Task.Delay(50, token);
        }
    }
    private decimal RawValue(string topic)
    {
        if (!Client.TryGetLatestSample(topic, out var value, out var at) || Clock.UtcNow - at > TimeSpan.FromSeconds(3) || at > Clock.UtcNow.AddSeconds(2))
            throw new InvalidOperationException("Ein aktueller Victron-Wert fehlt.");
        return value;
    }
    private int Value(string topic) => checked((int)Math.Round(RawValue(topic)));
    private static string ReadTopic(string write) => write.StartsWith("W/", StringComparison.Ordinal) ? "N/" + write[2..] :
        throw new InvalidOperationException("Ein Victron-Schreibtopic ist ungueltig.");

    public async Task<JointBatteryLiveSnapshot> ReadAsync(CancellationToken token)
    {
        var config = await Settings(token);
        var store = services.GetRequiredService<IControllerSettingStore>();
        async Task<decimal> Number(string key) => decimal.Parse((await store.GetSettingAsync(key, token))?.Value ?? "0", CultureInfo.InvariantCulture);
        var limits = new JointBatteryElectricalLimits([
            (int)await Number(ControllerSettingDefaults.JointBatteryPhase1LimitKey), (int)await Number(ControllerSettingDefaults.JointBatteryPhase2LimitKey),
            (int)await Number(ControllerSettingDefaults.JointBatteryPhase3LimitKey)], (int)await Number(ControllerSettingDefaults.JointBatteryVictronInputLimitKey),
            config.ExternalEssPhaseCount, (int)await Number(ControllerSettingDefaults.JointBatteryGridLimitKey));
        var primary = await services.GetRequiredService<IBatteryConfigurationProvider>().GetBatteryConfigurationAsync(token);
        var maxSoc = await Number(ControllerSettingDefaults.E3dcPlanningMaximumSocKey);
        var efficiency = await Number(ControllerSettingDefaults.E3dcEfficiencyKey);
        var topics = VictronMqttTopicFactory.Create(config);
        if (!topics.GridPowerTopic.EndsWith("/Ac/Power", StringComparison.Ordinal))
            throw new InvalidOperationException("Die phasenweise Netzleistung kann aus dem konfigurierten Topic nicht ermittelt werden.");
        var phaseTopics = Enumerable.Range(1, 3).Select(i => topics.GridPowerTopic[..^"/Ac/Power".Length] + $"/Ac/L{i}/Power").ToArray();
        var acIn = topics.AcOutPowerTopics.Select(topic => topic.Replace("/Ac/Out/", "/Ac/ActiveIn/", StringComparison.Ordinal)).ToArray();
        var requested = phaseTopics.Concat(acIn).Concat(topics.AcOutPowerTopics).Concat([
            topics.BatteryPowerTopic, topics.BatterySocTopic, topics.Hub4ModeReadTopic, topics.VebusMaxChargeCurrentTopic, topics.BatteryVoltageTopic]).ToArray();
        var at = Clock.UtcNow;
        var local = await services.GetRequiredService<IE3dcTelemetrySource>().ReadAsync(token);
        await RefreshAsync(requested, token);
        if (Value(topics.Hub4ModeReadTopic) != 3) throw new InvalidOperationException("Victron bestaetigt External ESS nicht.");
        var chargeLimit = Math.Max(0, (int)(RawValue(topics.VebusMaxChargeCurrentTopic) * RawValue(topics.BatteryVoltageTopic) * .97m));
        var secondary = new BatteryConfiguration(new BatteryConfigurationValues {
            TotalCapacityKwh = local.UsableCapacityKwh, MinimumStateOfChargePercent = local.ReserveKwh / local.UsableCapacityKwh * 100,
            PlanningMinimumStateOfChargePercent = local.ReserveKwh / local.UsableCapacityKwh * 100, PlanningMaximumStateOfChargePercent = maxSoc,
            MaximumChargePowerWatts = local.MaximumChargeWatts, MaximumDischargePowerWatts = local.MaximumDischargeWatts,
            RoundTripEfficiencyPercent = efficiency });
        return new(at, [new("victron", primary, new(RawValue(topics.BatterySocTopic), at), Value(topics.BatteryPowerTopic), chargeLimit),
            new("e3dc", secondary, new(local.SocPercent, local.MeasuredAtUtc), local.BatteryPowerWatts, local.EffectiveChargeLimitWatts, local.EffectiveDischargeLimitWatts)],
            phaseTopics.Select(Value).ToArray(), topics.AcOutPowerTopics.Select(Value).ToArray(), acIn.Sum(Value) - topics.AcOutPowerTopics.Sum(Value), local.OperatingMode, limits);
    }

    public async Task SetVictronAsync(int signedWatts, CancellationToken token)
    {
        var config = await Settings(token);
        var topics = VictronMqttTopicFactory.Create(config);
        var charge = signedWatts > 0 ? 0 : 1; var feed = signedWatts < 0 ? 0 : 1;
        var expected = new List<(string Write, int Value)> { (topics.DisableChargeTopic, charge), (topics.DisableFeedInTopic, feed) };
        if (signedWatts != 0) await RefreshAsync(topics.AcOutPowerTopics, token);
        var count = topics.ExternalEssAcPowerSetpointTopics.Count;
        for (var i = 0; i < count; i++)
        {
            // At stop, inhibit both directions first. Missing load data is no reason to skip the inhibit writes.
            var load = Client.TryGetLatestValue(topics.AcOutPowerTopics[i], out var watts) ? (int)watts : 0;
            var share = signedWatts / count + (i == 0 ? signedWatts % count : 0);
            expected.Add((topics.ExternalEssAcPowerSetpointTopics[i], share + load));
        }
        foreach (var item in expected) await Client.PublishValueAsync(item.Write, item.Value, token);
        await RefreshAsync(expected.Select(p => ReadTopic(p.Write)).ToArray(), token);
        if (expected.Any(p => Value(ReadTopic(p.Write)) != p.Value))
            throw new InvalidOperationException("Victron bestaetigt die gemeinsamen Sollwerte nicht.");
    }

    public Task<E3dcPowerAcknowledgement> SetE3dcAsync(E3dcPowerCommand command, CancellationToken token) =>
        services.GetRequiredService<IE3dcPowerControl>().ApplyAsync(command, token);
}
