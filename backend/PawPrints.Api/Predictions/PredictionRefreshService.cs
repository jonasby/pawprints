namespace PawPrints.Api.Predictions;

public sealed class PredictionRefreshService(
    IServiceProvider services,
    ILogger<PredictionRefreshService> logger
) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Prediction refresh background service started with interval {RefreshIntervalMinutes} minutes",
            RefreshInterval.TotalMinutes
        );

        using var timer = new PeriodicTimer(RefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshAsync(stoppingToken);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = services.CreateScope();
            var evaluator = scope.ServiceProvider.GetRequiredService<PredictionEvaluator>();
            await evaluator.EvaluateRecentlyActiveUsersAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Prediction refresh background service iteration failed");
        }
    }
}
