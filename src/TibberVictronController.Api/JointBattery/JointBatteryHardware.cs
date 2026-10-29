using System.Globalization;
using TibberVictronController.Api.Configuration;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Api.JointBattery;

/// <summary>Explicit command readback and fresh AC-side measurements for the shared execution coordinator.</summary>
public sealed class JointBatteryHardware(IServiceProvider services) : IJointBatteryHardware
{
    // Fits the 4 s execution cycle: write, then up to ~1.5 s of re-reads, each read itself bounded to 1.5 s.
    private static readonly TimeSpan ReadbackDeadline = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SampleMaximumAge = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReadRequestTimeout = TimeSpan.FromSeconds(2.5);

    private IVictronMqttControlClient Client => services.GetRequiredService<IVictronMqttControlClient>();
    private IUtcClock Clock => services.GetRequiredService<IUtcClock>();

    private async Task<VictronMqttSettings> Settings(CancellationToken token)
    {
        var settings = await services.GetRequiredService<DatabaseVictronMqttSettingsProvider>().GetSettingsAsync(token);
        if (settings.ControlMode != VictronControlMode.ExternalEss)
            throw new InvalidOperationException("Gemeinsame Steuerung benoetigt den eingerichteten External-ESS-Modus.");
        return settings;
    }

    /// <summary>
    /// Makes sure every topic has a recent value. Powers are published by the Victron on every change and are
    /// normally seconds old; requesting all ~13 values every second overloaded dbus-mqtt (about 200 read timeouts
    /// per day, 28.10.2026). Only stale values are requested, lost requests are repeated once per second.
    /// </summary>
    private async Task RefreshAsync(IReadOnlyList<string> topics, CancellationToken token)
    {
        bool Fresh(string t) => Client.TryGetLatestSample(t, out _, out var at) &&
            Clock.UtcNow - at <= SampleMaximumAge && at <= Clock.UtcNow.AddSeconds(2);
        var stale = topics.Where(t => !Fresh(t)).Distinct().ToArray();
        if (stale.Length == 0) return;

        var requested = Clock.UtcNow;
        foreach (var topic in stale) await Client.RequestValueAsync(topic, token);
        var until = requested + ReadRequestTimeout;
        var nextRetry = requested.AddSeconds(1);
        bool Missing(string t) => !Client.TryGetLatestSample(t, out _, out var at) || at < requested;
        while (stale.Any(Missing))
        {
            if (Clock.UtcNow >= until)
                throw new TimeoutException("Victron beantwortet Leseanfragen nicht: " + string.Join(", ", stale.Where(Missing).Select(DevicePath)) + ".");
            if (Clock.UtcNow >= nextRetry)
            {
                foreach (var topic in stale.Where(Missing)) await Client.RequestValueAsync(topic, token);
                nextRetry = Clock.UtcNow.AddSeconds(1);
            }
            await Task.Delay(50, token);
        }
    }

    private decimal RawValue(string topic)
    {
        // Values accepted as fresh by RefreshAsync may be up to 3 s old plus the time of this step.
        if (!Client.TryGetLatestSample(topic, out var value, out var at) || Clock.UtcNow - at > TimeSpan.FromSeconds(5) || at > Clock.UtcNow.AddSeconds(2))
            throw new InvalidOperationException($"Ein aktueller Victron-Wert fehlt: {DevicePath(topic)}.");
        return value;
    }

    // Diagnostics show the dbus path without the "N/<portal id>/" prefix: the portal id is not needed to locate the value.
    private static string DevicePath(string topic) => string.Join('/', topic.Split('/').Skip(2));
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
        var (e3dcCharge, e3dcDischarge) = await E3dcLimitPolicy.ActiveLimitsAsync(store, local, token);
        return new(at, [new("victron", primary, new(RawValue(topics.BatterySocTopic), at), Value(topics.BatteryPowerTopic), chargeLimit),
            new("e3dc", secondary, new(local.SocPercent, local.MeasuredAtUtc), local.BatteryPowerWatts, e3dcCharge, e3dcDischarge)],
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

        // Confirmation without flooding dbus-mqtt: a changed value is published by the Victron on its own, and an
        // unchanged one is already in the cache (changes are always published, so the cache is current). Only values
        // still not matching after 0.4 s are requested explicitly. A read answer may still carry the value from before
        // the write (seen: wrote 218, read 216), hence repeated requests until the deadline.
        var deadline = Clock.UtcNow.Add(ReadbackDeadline);
        var nextRequest = Clock.UtcNow.AddSeconds(0.4);
        int? Current(string write) => Client.TryGetLatestSample(ReadTopic(write), out var value, out _) ? (int)Math.Round(value) : null;
        while (true)
        {
            var mismatches = expected.Select(p => (p.Write, p.Value, Actual: Current(p.Write))).Where(p => p.Actual != p.Value).ToArray();
            if (mismatches.Length == 0) return;
            if (Clock.UtcNow >= deadline)
                throw new InvalidOperationException("Victron bestaetigt die gemeinsamen Sollwerte nicht: " +
                    string.Join(", ", mismatches.Select(m => $"{DevicePath(m.Write)} soll {m.Value}, ist {m.Actual?.ToString(CultureInfo.InvariantCulture) ?? "unbekannt"}")) + ".");
            if (Clock.UtcNow >= nextRequest)
            {
                foreach (var mismatch in mismatches) await Client.RequestValueAsync(ReadTopic(mismatch.Write), token);
                nextRequest = Clock.UtcNow.AddSeconds(0.5);
            }
            await Task.Delay(50, token);
        }
    }

    public Task<E3dcPowerAcknowledgement> SetE3dcAsync(E3dcPowerCommand command, CancellationToken token) =>
        services.GetRequiredService<IE3dcPowerControl>().ApplyAsync(command, token);
}
