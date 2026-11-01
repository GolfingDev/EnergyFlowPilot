using System.Globalization;
using TibberVictronController.Api.Authentication;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.EvCharging;

/// <summary>
/// The one fixed site address (batteries, PV and wallbox are always at the same place): it sets the PV forecast
/// coordinates and the start of every trip. Address suggestions for site and trip destinations come from Photon.
/// </summary>
public static class SiteEndpoints
{
    // Typing produces many similar queries; a short cache keeps the public service's load low.
    private static readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<PlaceSuggestion> Places)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    public static IEndpointRouteBuilder MapSiteEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/places", SuggestAsync).WithName("SuggestPlaces").WithTags("Site").RequireDriver();
        endpoints.MapGet("/api/site", GetSiteAsync).WithName("GetSite").WithTags("Site").RequireAdmin();
        endpoints.MapPut("/api/site", SetSiteAsync).WithName("SetSite").WithTags("Site");
        return endpoints;
    }

    private static async Task<IResult> SuggestAsync(string? q, IPlaceSearch search, IControllerSettingStore settings, IUtcClock clock,
        CancellationToken cancellationToken)
    {
        var query = q?.Trim() ?? string.Empty;
        if (query.Length is < 3 or > 200) return TypedResults.Ok(Array.Empty<PlaceSuggestionDto>());
        var now = clock.UtcNow;
        lock (Cache)
        {
            if (Cache.TryGetValue(query, out var cached) && now - cached.At < CacheLifetime) return TypedResults.Ok(Map(cached.Places));
        }

        try
        {
            var places = await search.SuggestAsync(query, await HomeAsync(settings, cancellationToken), cancellationToken);
            lock (Cache)
            {
                if (Cache.Count > 500) Cache.Clear();
                Cache[query] = (now, places);
            }

            return TypedResults.Ok(Map(places));
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Suggestions are a convenience; free text still works without them.
            return TypedResults.Ok(Array.Empty<PlaceSuggestionDto>());
        }
    }

    private static async Task<IResult> GetSiteAsync(IControllerSettingStore settings, CancellationToken cancellationToken)
    {
        var address = (await settings.GetSettingAsync(ControllerSettingDefaults.SiteAddressKey, cancellationToken))?.Value;
        var home = await HomeAsync(settings, cancellationToken);
        return TypedResults.Ok(new SiteDto(address == ControllerSettingDefaults.SiteAddressNotConfigured ? null : address, home?.Latitude, home?.Longitude));
    }

    private static async Task<IResult> SetSiteAsync(SiteRequestDto request, IControllerSettingStore settings, IUtcClock clock,
        IServiceScopeFactory scopes, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var address = request.Address?.Trim() ?? string.Empty;
        if (address.Length is < 3 or > 300)
            return TypedResults.BadRequest(new { message = "Die Adresse muss zwischen 3 und 300 Zeichen lang sein." });
        if (request.Latitude is < -90 or > 90 || request.Longitude is < -180 or > 180)
            return TypedResults.BadRequest(new { message = "Die Koordinaten der Adresse sind ungueltig; bitte einen Vorschlag aus der Liste waehlen." });

        var now = clock.UtcNow;
        await settings.SaveSettingAsync(new ControllerSetting(ControllerSettingDefaults.SiteAddressKey, address, ControllerSettingSensitivity.Normal, now), cancellationToken);
        await settings.SaveSettingAsync(new ControllerSetting(ControllerSettingDefaults.PvForecastLatitudeKey,
            Math.Round(request.Latitude, 5).ToString(CultureInfo.InvariantCulture), ControllerSettingSensitivity.Normal, now), cancellationToken);
        await settings.SaveSettingAsync(new ControllerSetting(ControllerSettingDefaults.PvForecastLongitudeKey,
            Math.Round(request.Longitude, 5).ToString(CultureInfo.InvariantCulture), ControllerSettingSensitivity.Normal, now), cancellationToken);

        // Trips are recalculated in the background: one route per second takes a while with many trips.
        var logger = loggers.CreateLogger("TibberVictronController.Api.EvCharging.Site");
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var count = await scope.ServiceProvider.GetRequiredService<EvTripPlanningService>().RecalculateRoutesAsync(CancellationToken.None);
                logger.LogInformation("Standort geaendert: {Count} Fahrten neu berechnet.", count);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Fahrten konnten nach der Standortaenderung nicht neu berechnet werden.");
            }
        }, CancellationToken.None);
        return TypedResults.NoContent();
    }

    private static async Task<GeoPoint?> HomeAsync(IControllerSettingStore settings, CancellationToken cancellationToken)
    {
        var latitude = (await settings.GetSettingAsync(ControllerSettingDefaults.PvForecastLatitudeKey, cancellationToken))?.Value;
        var longitude = (await settings.GetSettingAsync(ControllerSettingDefaults.PvForecastLongitudeKey, cancellationToken))?.Value;
        return double.TryParse(latitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) &&
               double.TryParse(longitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            ? new GeoPoint(lat, lon)
            : null;
    }

    private static PlaceSuggestionDto[] Map(IReadOnlyList<PlaceSuggestion> places) =>
        places.Select(place => new PlaceSuggestionDto(place.Label, place.Latitude, place.Longitude)).ToArray();
}

public sealed record PlaceSuggestionDto(string Label, double Latitude, double Longitude);

public sealed record SiteDto(string? Address, double? Latitude, double? Longitude);

public sealed record SiteRequestDto(string? Address, double Latitude, double Longitude);
