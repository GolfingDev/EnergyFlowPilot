using System.Xml.Linq;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Homematic;

/// <summary>
/// Reads climate values from the OpenCCU XML-API add-on. Strictly read-only: only *list.cgi
/// endpoints are called, never any endpoint that changes device states.
/// </summary>
public sealed class HomematicXmlApiClient : IHomematicClimateSource
{
    private readonly HttpClient httpClient;
    private readonly DatabaseHomematicSettingsProvider settingsProvider;

    public HomematicXmlApiClient(HttpClient httpClient, DatabaseHomematicSettingsProvider settingsProvider)
    {
        this.httpClient = httpClient;
        this.settingsProvider = settingsProvider;
    }

    public async Task<HomematicClimateSnapshot> GetCurrentSnapshotAsync(DateTimeOffset polledAtUtc, CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);

        if (!settings.IsReadyForPolling)
        {
            throw new InvalidOperationException("Die Homematic-Erfassung ist nicht aktiviert oder Basis-URL bzw. API-Token fehlen.");
        }

        // The device list is fetched every cycle (about 50 KB in the LAN) so renames apply immediately.
        var stateList = await LoadListAsync(settings, "statelist.cgi", cancellationToken);
        var deviceList = await LoadListAsync(settings, "devicelist.cgi", cancellationToken);

        return HomematicXmlParser.Parse(stateList, deviceList, polledAtUtc);
    }

    private async Task<XDocument> LoadListAsync(HomematicSettings settings, string listName, CancellationToken cancellationToken)
    {
        var requestUri = $"{settings.BaseUrl.TrimEnd('/')}/config/xmlapi/{listName}?sid={Uri.EscapeDataString(settings.ApiToken!)}";

        using var response = await httpClient.GetAsync(requestUri, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HomematicApiException($"Die Homematic-XML-API hat {listName} mit HTTP {(int)response.StatusCode} beantwortet.");
        }

        // Load from the stream so the XML declaration (ISO-8859-1) decides the encoding; reading as
        // string first would garble umlauts in device names.
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        try
        {
            return await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new HomematicApiException($"Die Antwort der Homematic-XML-API fuer {listName} ist kein gueltiges XML.", exception);
        }
    }
}
