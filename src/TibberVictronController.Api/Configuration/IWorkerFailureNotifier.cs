namespace TibberVictronController.Api.Configuration;

public interface IWorkerFailureNotifier
{
    Task NotifyAsync(Exception exception, CancellationToken cancellationToken);
}
