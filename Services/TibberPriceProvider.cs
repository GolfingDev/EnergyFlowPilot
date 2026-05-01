using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TibberVictronController.Web.Models;

namespace TibberVictronController.Web.Services;

public class TibberPriceProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TibberOptions _options;
    private IReadOnlyList<PricePoint>? _cache;
    private DateTime _cacheUtc;

    public TibberPriceProvider(IHttpClientFactory httpClientFactory, IOptions<TibberOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<PricePoint>> GetUpcomingPricesAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null && (DateTime.UtcNow - _cacheUtc).TotalSeconds < Math.Max(30, _options.CacheSeconds))
        {
            return _cache;
        }

        IReadOnlyList<PricePoint> result;
        if (string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            result = BuildDemoPrices();
        }
        else
        {
            result = await FetchTibberPricesAsync(cancellationToken);
        }

        _cache = result;
        _cacheUtc = DateTime.UtcNow;
        return result;
    }

    private async Task<IReadOnlyList<PricePoint>> FetchTibberPricesAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

        const string query = @"
        {
          viewer {
            homes {
              currentSubscription {
                priceInfo(resolution: QUARTER_HOURLY) {
                  today { total startsAt }
                  tomorrow { total startsAt }
                  current { total startsAt }
                }
              }
            }
          }
        }";

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.tibber.com/v1-beta/gql");
        request.Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var homes = doc.RootElement.GetProperty("data").GetProperty("viewer").GetProperty("homes");
        if (homes.GetArrayLength() == 0)
        {
            return BuildDemoPrices();
        }

        var priceInfo = homes[0].GetProperty("currentSubscription").GetProperty("priceInfo");
        var pricePoints = new List<PricePoint>();

        foreach (var bucket in new[] { "today", "tomorrow" })
        {
            if (!priceInfo.TryGetProperty(bucket, out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in arr.EnumerateArray())
            {
                if (!item.TryGetProperty("startsAt", out var startsAtProp) || !item.TryGetProperty("total", out var totalProp))
                {
                    continue;
                }

                if (!DateTimeOffset.TryParse(startsAtProp.GetString(), out var startsAt))
                {
                    continue;
                }

                if (!totalProp.TryGetDecimal(out var total))
                {
                    continue;
                }

                pricePoints.Add(new PricePoint(startsAt, total));
            }
        }

        return pricePoints
            .Where(x => x.StartsAt >= DateTimeOffset.Now.AddHours(-1))
            .OrderBy(x => x.StartsAt)
            .Take(Math.Max(24, _options.PriceLookaheadHours * 4))
            .ToList();
    }

    private static IReadOnlyList<PricePoint> BuildDemoPrices()
    {
        var now = DateTimeOffset.Now;
        return Enumerable.Range(0, 96)
            .Select(i =>
            {
                var time = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset).AddMinutes(i * 15);
                var basePrice = 0.17m + (decimal)(Math.Sin(i / 4.0) * 0.06 + 0.08);
                return new PricePoint(time, Math.Round(basePrice, 3));
            })
            .ToList();
    }
}
