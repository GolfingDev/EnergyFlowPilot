namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Signals the decision worker when fresh input data makes an immediate recalculation worthwhile.
/// </summary>
public sealed class DecisionCalculationTrigger
{
    private readonly SemaphoreSlim semaphore = new(0, 1);
    private readonly object syncRoot = new();
    private bool isSignalPending;

    public void Signal()
    {
        lock (syncRoot)
        {
            if (isSignalPending)
            {
                return;
            }

            isSignalPending = true;
            semaphore.Release();
        }
    }

    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var wasSignaled = await semaphore.WaitAsync(timeout, cancellationToken);
        if (wasSignaled)
        {
            lock (syncRoot)
            {
                isSignalPending = false;
            }
        }

        return wasSignaled;
    }
}
