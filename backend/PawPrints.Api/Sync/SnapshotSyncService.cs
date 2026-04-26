using Microsoft.EntityFrameworkCore;
using PawPrints.Api.Contracts;
using PawPrints.Api.Data;

namespace PawPrints.Api.Sync;

public sealed class SnapshotSyncService(
    PawPrintsDbContext db,
    ILogger<SnapshotSyncService> logger
)
{
    public async Task SyncAsync(
        string email,
        string externalSubject,
        SyncSnapshotRequest snapshot,
        CancellationToken cancellationToken
    )
    {
        logger.LogInformation(
            "Syncing PawPrints snapshot for {Email} with {EventCount} events.",
            email,
            snapshot.Events.Count
        );

        var arrivalDate = DateOnly.Parse(snapshot.Settings.ArrivalDate);
        var birthDate = DateOnly.Parse(snapshot.Settings.BirthDate);
        var now = DateTimeOffset.UtcNow;
        var user = await db.Users
            .Include(storedUser => storedUser.Events)
            .SingleOrDefaultAsync(storedUser => storedUser.Email == email, cancellationToken);

        if (user is null)
        {
            user = new PawPrintsUser
            {
                Email = email,
                ExternalSubject = externalSubject,
                ArrivalDate = arrivalDate,
                BirthDate = birthDate,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Users.Add(user);
        }
        else
        {
            user.ExternalSubject = externalSubject;
            user.ArrivalDate = arrivalDate;
            user.BirthDate = birthDate;
            user.UpdatedAt = now;
            db.Events.RemoveRange(user.Events);
        }

        user.Events = snapshot.Events
            .Select(snapshotEvent => new PuppyEvent
            {
                ClientEventId = snapshotEvent.Id,
                Type = snapshotEvent.Type,
                OccurredAt = snapshotEvent.OccurredAt,
                DateKey = DateOnly.Parse(snapshotEvent.DateKey),
                MetadataJson = snapshotEvent.Details?.GetRawText(),
            })
            .ToList();

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Synced PawPrints snapshot for {Email}; stored {EventCount} events.",
            email,
            user.Events.Count
        );
    }
}
