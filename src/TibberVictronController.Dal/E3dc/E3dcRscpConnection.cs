using System.Net.Sockets;
using System.Text;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.E3dc;

internal sealed class E3dcRscpConnection(TcpClient client, RscpProtocol protocol) : IDisposable
{
    internal RscpProtocol Protocol => protocol;
    public void Dispose() => client.Dispose();

    internal static async Task<E3dcRscpConnection> OpenAsync(IControllerSettingStore settings, CancellationToken token)
    {
        async Task<string> Required(string key) => (await settings.GetSettingAsync(key, token))?.Value is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Der lokale E3/DC-Zugang ist nicht vollstaendig konfiguriert.");
        var host = await Required(ControllerSettingDefaults.E3dcRscpHostKey);
        var user = await Required(ControllerSettingDefaults.E3dcRscpUserKey);
        var password = await Required(ControllerSettingDefaults.E3dcRscpPasswordKey);
        var key = await Required(ControllerSettingDefaults.E3dcRscpKeyKey);
        var port = int.Parse(await Required(ControllerSettingDefaults.E3dcRscpPortKey));
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, token);
            var protocol = new RscpProtocol(client.GetStream(), key);
            var auth = await protocol.ExchangeAsync(RscpValue.Container(1,
                new(2, 13, Encoding.UTF8.GetBytes(user)), new(3, 13, Encoding.UTF8.GetBytes(password))), 0x00800001, token);
            if (auth.Number() <= 0) throw new InvalidDataException("Die RSCP-Anmeldung wurde abgelehnt.");
            return new(client, protocol);
        }
        catch { client.Dispose(); throw; }
    }
}
