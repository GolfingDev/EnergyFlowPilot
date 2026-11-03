using TibberVictronController.Api.Authentication;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Push;

public static class PushEndpoints
{
    public static IEndpointRouteBuilder MapPushEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/push/public-key", async (WebPushService push, CancellationToken cancellationToken) =>
            TypedResults.Ok(new PushPublicKeyDto(await push.GetPublicKeyAsync(cancellationToken))))
            .WithName("GetPushPublicKey").WithTags("Push").RequireDriver();
        endpoints.MapPut("/api/push/subscription", SubscribeAsync).WithName("SubscribePush").WithTags("Push").RequireDriver();
        endpoints.MapDelete("/api/push/subscription", UnsubscribeAsync).WithName("UnsubscribePush").WithTags("Push").RequireDriver();
        endpoints.MapPost("/api/push/test", TestAsync).WithName("TestPush").WithTags("Push").RequireDriver();
        // Answer from a notification button: authorised by the token in the message, not by a browser session.
        endpoints.MapPost("/api/push/base-choice", AnswerAsync).WithName("AnswerBaseChoiceFromPush").WithTags("Push").AllowAnonymous();
        return endpoints;
    }

    private static async Task<IResult> SubscribeAsync(HttpContext http, PushSubscriptionDto request, IWebPushRepository repository, IUtcClock clock,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps || request.Endpoint!.Length > 1000 ||
            string.IsNullOrWhiteSpace(request.P256dh) || request.P256dh.Length > 200 || string.IsNullOrWhiteSpace(request.Auth) || request.Auth.Length > 100)
            return TypedResults.BadRequest(new { message = "Das Push-Abo des Browsers ist unvollstaendig." });
        var actor = AdminOnlyEndpoints.Actor(http);
        await repository.SaveSubscriptionAsync(new WebPushSubscription(request.Endpoint, request.P256dh, request.Auth, actor.UserId, actor.Name, clock.UtcNow),
            cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> UnsubscribeAsync(HttpContext http, string endpoint, IWebPushRepository repository, CancellationToken cancellationToken)
    {
        var actor = AdminOnlyEndpoints.Actor(http);
        await repository.DeleteSubscriptionAsync(endpoint, actor.IsAdmin ? null : actor.UserId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> TestAsync(HttpContext http, WebPushService push, CancellationToken cancellationToken)
    {
        var actor = AdminOnlyEndpoints.Actor(http);
        var (delivered, failed) = await push.SendAsync(new PushMessage("test", "EnergyFlowPilot", "Benachrichtigungen funktionieren.", "efp-test", "/car"),
            actor.UserId, TimeSpan.FromMinutes(10), cancellationToken);
        return delivered > 0
            ? TypedResults.Ok(new PushTestResultDto(delivered, failed))
            : TypedResults.BadRequest(new { message = failed > 0 ? "Der Push-Dienst hat die Testnachricht abgelehnt." : "Auf keinem Geraet sind Benachrichtigungen eingeschaltet." });
    }

    private static async Task<IResult> AnswerAsync(PushBaseChoiceAnswerDto request, BaseChoiceAnswerToken tokens, IEvChargingSessionRepository sessions,
        CancellationToken cancellationToken)
    {
        if (tokens.Read(request.Token) is not { } answer)
            return TypedResults.Json(new { message = "Die Benachrichtigung ist abgelaufen; bitte auf der Seite Auto waehlen." }, statusCode: StatusCodes.Status403Forbidden);
        if (!Enum.TryParse<EvBaseChargeMode>(request.Mode, out var mode) || !Enum.IsDefined(mode))
            return TypedResults.BadRequest(new { message = "Unbekannte Auswahl." });
        return await sessions.SetBaseChargeModeAsync(answer.TransactionId, mode, answer.UserName, cancellationToken)
            ? TypedResults.NoContent()
            : TypedResults.Conflict(new { message = "Dieser Ladevorgang ist schon beendet." });
    }
}

public sealed record PushPublicKeyDto(string PublicKey);

public sealed record PushSubscriptionDto(string? Endpoint, string? P256dh, string? Auth);

public sealed record PushTestResultDto(int Delivered, int Failed);

public sealed record PushBaseChoiceAnswerDto(string? Token, string? Mode);
