namespace TibberVictronController.Api.Configuration;

public interface IVictronMqttControlClient
{
    Task PublishValueAsync(string topic, int value, CancellationToken cancellationToken = default);

    bool TryGetLatestValue(string topic, out decimal value);

    bool TryGetLatestSample(string topic, out decimal value, out DateTimeOffset measuredAtUtc)
    { value = 0; measuredAtUtc = default; return false; }

    Task RequestValueAsync(string readTopic, CancellationToken token = default) => Task.CompletedTask;
}
