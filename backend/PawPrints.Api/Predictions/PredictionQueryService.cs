using Microsoft.EntityFrameworkCore;
using PawPrints.Api.Data;

namespace PawPrints.Api.Predictions;

public sealed class PredictionQueryService(
    PawPrintsDbContext db,
    ILogger<PredictionQueryService> logger
)
{
    public async Task<IReadOnlyCollection<PredictionResponse>> GetActivePredictionsAsync(
        string email,
        CancellationToken cancellationToken
    )
    {
        var dataUser = await GetDataUserAsync(email, cancellationToken);
        if (dataUser is null)
        {
            logger.LogInformation(
                "Prediction query completed with outcome {Outcome} email {Email} active prediction count {ActivePredictionCount}",
                "NoProfile",
                email,
                0
            );
            return [];
        }

        var predictions = await db.Predictions
            .AsNoTracking()
            .Where(prediction =>
                prediction.UserId == dataUser.Id
                && prediction.Status == PredictionConstants.StatusActive)
            .OrderBy(prediction => prediction.WindowStartUtc)
            .ThenBy(prediction => prediction.Id)
            .Select(prediction => new PredictionResponse(
                prediction.Id,
                prediction.Type,
                prediction.Status,
                prediction.TriggerEventClientId,
                prediction.PredictedAtUtc,
                prediction.WindowStartUtc,
                prediction.BestGuessAtUtc,
                prediction.WindowEndUtc,
                prediction.Confidence,
                prediction.ExplanationJson ?? "{}",
                prediction.LastEvaluatedAtUtc
            ))
            .ToArrayAsync(cancellationToken);

        logger.LogInformation(
            "Prediction query completed with outcome {Outcome} email {Email} owner user id {OwnerUserId} active prediction count {ActivePredictionCount}",
            "ActivePredictionsLoaded",
            email,
            dataUser.Id,
            predictions.Length
        );

        return predictions;
    }

    private async Task<PawPrintsUser?> GetDataUserAsync(string email, CancellationToken cancellationToken)
    {
        var actor = await db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(storedUser => storedUser.Email == email, cancellationToken);
        if (actor?.CollaboratesWithUserId is long ownerId)
        {
            return await db.Users
                .AsNoTracking()
                .SingleOrDefaultAsync(storedUser => storedUser.Id == ownerId, cancellationToken);
        }

        return actor;
    }
}
