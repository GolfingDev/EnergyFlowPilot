using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Tibber;

/// <summary>
/// Loads Tibber price data through the Tibber GraphQL API and maps it to Decision Engine forecast slots.
/// </summary>
public sealed class TibberPriceForecastProvider : ITibberPriceForecastProvider
{
    private const string FirstHomeSelection = "first";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient httpClient;
    private readonly IControllerSettingStore controllerSettingStore;

    public TibberPriceForecastProvider(
        HttpClient httpClient,
        IControllerSettingStore controllerSettingStore)
    {
        this.httpClient = httpClient;
        this.controllerSettingStore = controllerSettingStore;
    }

    /// <summary>
    /// Loads future Tibber prices and verifies that they match the 15-minute grid required by the forecast.
    /// </summary>
    public async Task<IReadOnlyList<TibberPriceForecastSlot>> GetPriceForecastAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateUtcRange(startsAtUtc, endsAtUtc);

        var apiEndpoint = await GetRequiredSettingValueAsync(
            ControllerSettingDefaults.TibberApiEndpointKey,
            "Der Tibber API-Endpunkt ist nicht konfiguriert.",
            cancellationToken);
        var accessToken = await GetRequiredSettingValueAsync(
            ControllerSettingDefaults.TibberAccessTokenKey,
            "Der Tibber Access Token ist nicht konfiguriert.",
            cancellationToken);
        var homeSelection = await GetRequiredSettingValueAsync(
            ControllerSettingDefaults.TibberHomeSelectionKey,
            "Die Tibber Home-Auswahl ist nicht konfiguriert.",
            cancellationToken);

        var response = await ExecuteGraphQlRequestAsync(apiEndpoint, accessToken, cancellationToken);
        var home = SelectHome(response, homeSelection);
        var priceEntries = CollectPriceEntries(home);

        return MapToForecastSlots(priceEntries, startsAtUtc, endsAtUtc);
    }

    private async Task<string> GetRequiredSettingValueAsync(
        string settingKey,
        string missingMessage,
        CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(settingKey, cancellationToken);

        if (setting is null || !setting.IsConfigured)
        {
            throw new InvalidOperationException(missingMessage);
        }

        return setting.Value!;
    }

    private async Task<TibberGraphQlResponse> ExecuteGraphQlRequestAsync(
        string apiEndpoint,
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(apiEndpoint, UriKind.Absolute, out var endpointUri))
        {
            throw new InvalidOperationException("Der Tibber API-Endpunkt ist keine gueltige absolute URL.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpointUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new TibberGraphQlRequest(TibberGraphQlQueries.PriceForecastQuery), JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new TibberApiException($"Tibber hat den Request mit HTTP {(int)response.StatusCode} beantwortet.");
        }

        try
        {
            var graphQlResponse = JsonSerializer.Deserialize<TibberGraphQlResponse>(responseBody, JsonOptions)
                ?? throw new TibberApiException("Die Tibber-Antwort ist leer.");

            if (graphQlResponse.Errors is { Count: > 0 })
            {
                var errorMessages = string.Join("; ", graphQlResponse.Errors.Select(error => error.Message));
                throw new TibberApiException($"Tibber hat Fehler zurueckgegeben: {errorMessages}");
            }

            return graphQlResponse;
        }
        catch (JsonException exception)
        {
            throw new TibberApiException("Die Tibber-Antwort konnte nicht als JSON gelesen werden.", exception);
        }
    }

    private static TibberHomeResponse SelectHome(TibberGraphQlResponse response, string homeSelection)
    {
        var homes = response.Data?.Viewer?.Homes;

        if (homes is null || homes.Count == 0)
        {
            throw new TibberApiException("Tibber hat kein Zuhause mit Preisdaten geliefert.");
        }

        var selectedHome = string.Equals(homeSelection, FirstHomeSelection, StringComparison.OrdinalIgnoreCase)
            ? homes[0]
            : homes.SingleOrDefault(home => string.Equals(home.Id, homeSelection, StringComparison.OrdinalIgnoreCase));

        if (selectedHome is null)
        {
            throw new TibberApiException($"Tibber hat kein Zuhause fuer die konfigurierte Home-Auswahl '{homeSelection}' geliefert.");
        }

        return selectedHome;
    }

    private static IReadOnlyList<TibberPriceEntry> CollectPriceEntries(TibberHomeResponse home)
    {
        var priceInfo = home.CurrentSubscription?.PriceInfo;

        if (priceInfo is null)
        {
            throw new TibberApiException("Tibber hat keine Preisinfo fuer das ausgewaehlte Zuhause geliefert.");
        }

        var priceEntries = new List<TibberPriceEntry>();

        if (priceInfo.Current is not null)
        {
            priceEntries.Add(priceInfo.Current);
        }

        if (priceInfo.Today is not null)
        {
            priceEntries.AddRange(priceInfo.Today);
        }

        if (priceInfo.Tomorrow is not null)
        {
            priceEntries.AddRange(priceInfo.Tomorrow);
        }

        if (priceEntries.Count == 0)
        {
            throw new TibberApiException("Tibber hat keine Preise fuer heute oder morgen geliefert.");
        }

        return priceEntries;
    }

    private static IReadOnlyList<TibberPriceForecastSlot> MapToForecastSlots(
        IReadOnlyList<TibberPriceEntry> priceEntries,
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc)
    {
        var orderedEntries = priceEntries
            .GroupBy(priceEntry => priceEntry.StartsAt.ToUniversalTime())
            .Select(group => group.First())
            .OrderBy(priceEntry => priceEntry.StartsAt)
            .ToArray();

        var forecastSlots = new List<TibberPriceForecastSlot>();

        for (var index = 0; index < orderedEntries.Length; index++)
        {
            var currentEntry = orderedEntries[index];
            var slotStartUtc = currentEntry.StartsAt.ToUniversalTime();
            var slotEndUtc = index < orderedEntries.Length - 1
                ? orderedEntries[index + 1].StartsAt.ToUniversalTime()
                : slotStartUtc.AddMinutes(15);

            if (slotEndUtc - slotStartUtc != TimeSpan.FromMinutes(15))
            {
                throw new TibberApiException("Tibber-Preise muessen im 15-Minuten-Raster vorliegen.");
            }

            if (slotStartUtc >= endsAtUtc || slotEndUtc <= startsAtUtc)
            {
                continue;
            }

            forecastSlots.Add(new TibberPriceForecastSlot(
                new ForecastTimeSlot(slotStartUtc, slotEndUtc),
                currentEntry.Total,
                currentEntry.Currency));
        }

        return forecastSlots;
    }

    private static void ValidateUtcRange(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc)
    {
        if (startsAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Start des Tibber-Preiszeitraums muss in UTC angegeben sein.", nameof(startsAtUtc));
        }

        if (endsAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Das Ende des Tibber-Preiszeitraums muss in UTC angegeben sein.", nameof(endsAtUtc));
        }

        if (endsAtUtc <= startsAtUtc)
        {
            throw new ArgumentException("Das Ende des Tibber-Preiszeitraums muss nach dem Start liegen.", nameof(endsAtUtc));
        }
    }

    private static class TibberGraphQlQueries
    {
        public const string PriceForecastQuery = """
            query PriceForecast {
              viewer {
                homes {
                  id
                  currentSubscription {
                    priceInfo(resolution: QUARTER_HOURLY) {
                      current {
                        total
                        startsAt
                        currency
                      }
                      today {
                        total
                        startsAt
                        currency
                      }
                      tomorrow {
                        total
                        startsAt
                        currency
                      }
                    }
                  }
                }
              }
            }
            """;
    }

    private sealed record TibberGraphQlRequest([property: JsonPropertyName("query")] string Query);

    private sealed record TibberGraphQlResponse(
        TibberDataResponse? Data,
        IReadOnlyList<TibberGraphQlError>? Errors);

    private sealed record TibberGraphQlError(string Message);

    private sealed record TibberDataResponse(TibberViewerResponse? Viewer);

    private sealed record TibberViewerResponse(IReadOnlyList<TibberHomeResponse>? Homes);

    private sealed record TibberHomeResponse(
        string Id,
        TibberSubscriptionResponse? CurrentSubscription);

    private sealed record TibberSubscriptionResponse(TibberPriceInfoResponse? PriceInfo);

    private sealed record TibberPriceInfoResponse(
        TibberPriceEntry? Current,
        IReadOnlyList<TibberPriceEntry>? Today,
        IReadOnlyList<TibberPriceEntry>? Tomorrow);

    private sealed record TibberPriceEntry(
        decimal Total,
        DateTimeOffset StartsAt,
        string Currency);
}
