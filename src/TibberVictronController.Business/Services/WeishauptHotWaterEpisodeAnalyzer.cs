using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Services;

/// <summary>Observational evidence only: one sensor cannot measure stored energy or prove counterfactual savings.</summary>
public static class WeishauptHotWaterEpisodeAnalyzer
{
    public static IReadOnlyList<WeishauptHotWaterEpisodeAnalysis> Analyze(
        IReadOnlyList<WeishauptHotWaterEpisode> episodes, IReadOnlyList<WeishauptBiblockTelemetrySample> telemetry,
        DateTimeOffset nowUtc)
    {
        var ordered = episodes.OrderBy(e => e.StartedAtUtc).ToArray();
        return ordered.Select((episode, index) =>
        {
            var limit = episode.Plan.NeedByUtc.AddHours(2);
            if (index + 1 < ordered.Length && ordered[index + 1].StartedAtUtc < limit) limit = ordered[index + 1].StartedAtUtc;
            if (nowUtc < limit) limit = nowUtc;
            var nextStart = index + 1 < ordered.Length ? ordered[index + 1].StartedAtUtc : DateTimeOffset.MaxValue;
            var samples = telemetry.Where(s => s.MeasuredAtUtc >= episode.StartedAtUtc && s.MeasuredAtUtc <= limit &&
                    s.MeasuredAtUtc < nextStart && s.HotWaterTemperatureCelsius is >= 0m and <= 95m)
                .OrderBy(s => s.MeasuredAtUtc).ToArray();
            var targetDeadline = (episode.EndedAtUtc ?? episode.Plan.EndsAtUtc ?? episode.StartedAtUtc.AddHours(4)).AddMinutes(15);
            var target = samples.FirstOrDefault(s => s.MeasuredAtUtc <= targetDeadline &&
                s.MeasuredAtUtc <= episode.Plan.NeedByUtc && s.HotWaterTemperatureCelsius >= episode.RaisedCelsius - 0.5m);
            // A subsequent 20 transition is a new warm-water charge, not the end of the initial one.
            DateTimeOffset? nextHeating = null;
            for (var i = 1; i < samples.Length; i++)
                if (target is not null && samples[i].MeasuredAtUtc > target.MeasuredAtUtc &&
                    samples[i].OperatingStatusCode == 20 && samples[i - 1].OperatingStatusCode != 20)
                { nextHeating = samples[i].MeasuredAtUtc; break; }
            var below = target is null ? null : samples.FirstOrDefault(s => s.MeasuredAtUtc > target.MeasuredAtUtc &&
                s.HotWaterTemperatureCelsius < episode.OriginalCelsius)?.MeasuredAtUtc;
            var lost = below is null ? nextHeating : nextHeating is null ? below : below < nextHeating ? below : nextHeating;
            var checkUntil = nowUtc < episode.Plan.NeedByUtc ? nowUtc : episode.Plan.NeedByUtc;
            var points = samples.Where(s => s.MeasuredAtUtc <= checkUntil).ToArray();
            var gaps = points.Length == 0 || points[0].MeasuredAtUtc - episode.StartedAtUtc > TimeSpan.FromMinutes(15) ||
                checkUntil - points[^1].MeasuredAtUtc > TimeSpan.FromMinutes(15) ||
                points.Zip(points.Skip(1), (a, b) => b.MeasuredAtUtc - a.MeasuredAtUtc).Any(d => d > TimeSpan.FromMinutes(15));
            var atNeed = nowUtc >= episode.Plan.NeedByUtc && limit >= episode.Plan.NeedByUtc && points.Length > 0 &&
                episode.Plan.NeedByUtc - points[^1].MeasuredAtUtc <= TimeSpan.FromMinutes(15)
                ? points[^1].HotWaterTemperatureCelsius : (decimal?)null;
            bool? bridged = !episode.WriteConfirmed || episode.Interrupted || target is null || gaps || atNeed is null
                ? null : atNeed >= episode.OriginalCelsius && (lost is null || lost > episode.Plan.NeedByUtc);
            var text = episode.Interrupted ? "Durch manuellen Eingriff oder unbestaetigten Schreibvorgang unterbrochen."
                : !episode.WriteConfirmed ? "Schreibbestaetigung fehlt; keine belastbare Auswertung."
                : gaps ? "Messluecken; Nutzen nicht bewertbar."
                : target is null ? "Erhoehte Temperatur bisher nicht nachgewiesen."
                : bridged == true ? "Bis zur Bedarfszeit ausreichend warm; keine erneute Ladung erkannt."
                : bridged == false ? "Reserve reichte nicht bis zum Bedarf oder erneute Ladung erkannt."
                : "Beobachtung bis zur Bedarfszeit laeuft.";
            return new WeishauptHotWaterEpisodeAnalysis(episode, target?.MeasuredAtUtc, nextHeating, lost, atNeed, bridged, gaps, text);
        }).ToArray();
    }
}
