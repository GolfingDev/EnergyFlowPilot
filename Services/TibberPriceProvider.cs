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

    public TibberPriceProvider(IHttpClientFactory httpClientFactory, IOptions<TibberOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<PricePoint>> GetUpcomingPricesAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            return BuildDemoPrices();
        }

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
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { query }),
            Encoding.UTF8,
            "application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var pricePoints = new List<PricePoint>();

        var homes = doc.RootElement
            .GetProperty("data")
            .GetProperty("viewer")
            .GetProperty("homes");

        if (homes.GetArrayLength() == 0)
        {
            return BuildDemoPrices();
        }

        var priceInfo = homes[0]
            .GetProperty("currentSubscription")
            .GetProperty("priceInfo");

        foreach (var bucket in new[] { "today", "tomorrow" })
        {
            if (!priceInfo.TryGetProperty(bucket, out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in arr.EnumerateArray())
            {
                if (!item.TryGetProperty("startsAt", out var startsAtProp) ||
                    !item.TryGetProperty("total", out var totalProp))
                {
                    continue;
                }

                if (!DateTime.TryParse(startsAtProp.GetString(), out var startsAt))
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
            .ToList();
    }

    private static IReadOnlyList<PricePoint> BuildDemoPrices()
    {
        var now = DateTime.Now;
        return Enumerable.Range(0, 36)
            .Select(i =>
            {
                var price = 0.20m + (decimal)(Math.Sin(i / 3.0) * 0.08 + 0.1);
                return new PricePoint(new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Kind).AddHours(i), Math.Round(price, 3));
            })
            .ToList();
    }
}
