namespace TibberVictronController.Api.Configuration;

public interface IVictronMqttControlClient
{
    Task PublishValueAsync(string topic, int value, CancellationToken cancellationToken = default);
}
