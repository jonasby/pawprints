using Microsoft.EntityFrameworkCore;
using PawPrints.Api.Data;

namespace PawPrints.Api;

/// <summary>
/// Applies EF migrations without blocking Kestrel — Linux App Service warmup must get HTTP 200 from / within ~230s.
/// </summary>
public sealed class DatabaseMigrationHostedService(
    IServiceProvider services,
    ILogger<DatabaseMigrationHostedService> logger,
    DatabaseMigrationGate gate
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PawPrintsDbContext>();
            logger.LogInformation(
                "Applying database migrations in background for provider {DatabaseProvider}",
                db.Database.ProviderName ?? "unknown"
            );
            await db.Database.MigrateAsync(stoppingToken);
            logger.LogInformation("Database migrations finished successfully");
            gate.MarkReady();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database migration failed; API requests will return 503 until this is resolved");
            gate.MarkFailed(ex);
        }
    }
}
