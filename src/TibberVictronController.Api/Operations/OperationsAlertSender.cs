using TibberVictronController.Api.Push;

namespace TibberVictronController.Api.Operations;

/// <summary>Delivers one alert message; the seam that lets the alert service be tested without a push service.</summary>
public interface IOperationsAlertSender
{
    Task<(int Delivered, int Failed)> SendAsync(PushMessage message, TimeSpan timeToLive, CancellationToken cancellationToken);
}

/// <summary>
/// Sends to every device with notifications switched on. A push subscription stores the user's id and name but not
/// the role (roles exist only in the sign-in session), so administrators cannot be told apart here.
/// </summary>
public sealed class WebPushOperationsAlertSender(WebPushService push) : IOperationsAlertSender
{
    public Task<(int Delivered, int Failed)> SendAsync(PushMessage message, TimeSpan timeToLive, CancellationToken cancellationToken) =>
        push.SendAsync(message, null, timeToLive, cancellationToken);
}
