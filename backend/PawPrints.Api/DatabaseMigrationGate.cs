namespace PawPrints.Api;

/// <summary>
/// Signals when EF migrations have finished so /api/* can run without racing schema setup.
/// </summary>
public sealed class DatabaseMigrationGate
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void MarkReady() => _ready.TrySetResult();

    public void MarkFailed(Exception exception) => _ready.TrySetException(exception);

    public Task WaitForApiAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _ready.Task.WaitAsync(timeout, cancellationToken);
}
