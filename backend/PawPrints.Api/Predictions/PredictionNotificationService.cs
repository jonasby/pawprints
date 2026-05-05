using Microsoft.EntityFrameworkCore;
using PawPrints.Api.Data;

namespace PawPrints.Api.Predictions;

public sealed class PredictionNotificationService(
    PawPrintsDbContext db,
    TimeProvider timeProvider,
    ILogger<PredictionNotificationService> logger
)
{
    public async Task<IReadOnlyCollection<NotificationResponse>> ClaimDueNotificationsAsync(
        string email,
        CancellationToken cancellationToken
    )
    {
        var dataUser = await GetDataUserAsync(email, cancellationToken);
        if (dataUser is null)
        {
            logger.LogInformation(
                "Prediction notification due load completed with outcome {Outcome} email {Email} notification count {NotificationCount}",
                "NoProfile",
                email,
                0
            );
            return [];
        }

        var now = timeProvider.GetUtcNow();
        var due = await db.NotificationOutbox
            .Include(notification => notification.Prediction)
            .Where(notification =>
                notification.UserId == dataUser.Id
                && notification.SentAtUtc == null
                && notification.CancelledAtUtc == null
                && notification.SendAfterUtc <= now
                && notification.Prediction.Status == PredictionConstants.StatusActive)
            .OrderBy(notification => notification.SendAfterUtc)
            .ThenBy(notification => notification.Id)
            .ToArrayAsync(cancellationToken);

        var response = due
            .Select(notification => new NotificationResponse(
                notification.Id,
                notification.PredictionId,
                notification.Type,
                notification.Title,
                notification.Body,
                notification.SendAfterUtc
            ))
            .ToArray();

        foreach (var notification in due)
        {
            notification.SentAtUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Prediction notification due load completed with outcome {Outcome} email {Email} owner user id {OwnerUserId} notification count {NotificationCount}",
            due.Length > 0 ? "DueNotificationsDispatched" : "NoDueNotifications",
            email,
            dataUser.Id,
            due.Length
        );

        return response;
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
