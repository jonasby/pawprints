using Microsoft.EntityFrameworkCore;
using PawPrints.Api.Contracts;
using PawPrints.Api.Data;

namespace PawPrints.Api.Sync;

public sealed class SnapshotSyncService(
    PawPrintsDbContext db,
    ILogger<SnapshotSyncService> logger
)
{
    public async Task<SyncSnapshotRequest?> GetSnapshotAsync(
        string email,
        CancellationToken cancellationToken
    )
    {
        logger.LogInformation("Loading PawPrints snapshot for {Email}.", email);

        var actor = await db.Users
            .AsNoTracking()
            .Include(storedUser => storedUser.Events)
            .SingleOrDefaultAsync(storedUser => storedUser.Email == email, cancellationToken);

        if (actor is null)
        {
            logger.LogInformation("No PawPrints snapshot exists yet for {Email}.", email);
            return null;
        }

        var dataUser = actor;
        if (actor.CollaboratesWithUserId is long ownerId)
        {
            var owner = await db.Users
                .AsNoTracking()
                .Include(storedUser => storedUser.Events)
                .SingleOrDefaultAsync(storedUser => storedUser.Id == ownerId, cancellationToken);
            if (owner is null)
            {
                logger.LogWarning(
                    "Actor {Email} references missing owner id {OwnerId}; returning their own snapshot row.",
                    email,
                    ownerId
                );
            }
            else
            {
                dataUser = owner;
                logger.LogInformation(
                    "Loading shared puppy log for collaborator {Email} from owner id {OwnerId}.",
                    email,
                    ownerId
                );
            }
        }

        logger.LogInformation(
            "Loaded PawPrints snapshot for {Email}; returning {EventCount} events.",
            email,
            dataUser.Events.Count
        );

        return new SyncSnapshotRequest(
            new SyncSettingsRequest(
                dataUser.ArrivalDate.ToString("yyyy-MM-dd"),
                dataUser.BirthDate.ToString("yyyy-MM-dd")
            ),
            dataUser.Events
                .OrderBy(storedEvent => storedEvent.OccurredAt)
                .Select(storedEvent => new SyncEventRequest(
                    storedEvent.ClientEventId,
                    storedEvent.Type,
                    storedEvent.OccurredAt,
                    storedEvent.DateKey.ToString("yyyy-MM-dd")
                ))
                .ToArray()
        );
    }

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

        var actor = await db.Users
            .Include(storedUser => storedUser.Events)
            .SingleOrDefaultAsync(storedUser => storedUser.Email == email, cancellationToken);

        if (actor is null)
        {
            var newOwner = new PawPrintsUser
            {
                Email = email,
                ExternalSubject = externalSubject,
                ArrivalDate = arrivalDate,
                BirthDate = birthDate,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Users.Add(newOwner);
            newOwner.Events = snapshot.Events
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
                "Synced PawPrints snapshot for new owner {Email}; stored {EventCount} events.",
                email,
                newOwner.Events.Count
            );
            return;
        }

        actor.ExternalSubject = externalSubject;
        actor.UpdatedAt = now;

        if (actor.CollaboratesWithUserId is null)
        {
            actor.ArrivalDate = arrivalDate;
            actor.BirthDate = birthDate;
            db.Events.RemoveRange(actor.Events);
            actor.Events = snapshot.Events
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
                "Synced PawPrints snapshot for owner {Email}; stored {EventCount} events.",
                email,
                actor.Events.Count
            );
            return;
        }

        var owner = await db.Users
            .Include(storedUser => storedUser.Events)
            .SingleOrDefaultAsync(storedUser => storedUser.Id == actor.CollaboratesWithUserId!.Value, cancellationToken);

        if (owner is null)
        {
            logger.LogWarning(
                "Collaborator {Email} referenced missing owner id {OwnerId}; syncing as standalone owner row.",
                email,
                actor.CollaboratesWithUserId
            );

            actor.CollaboratesWithUserId = null;
            actor.ArrivalDate = arrivalDate;
            actor.BirthDate = birthDate;
            db.Events.RemoveRange(actor.Events);
            actor.Events = snapshot.Events
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
                "Synced PawPrints snapshot for recovered owner {Email}; stored {EventCount} events.",
                email,
                actor.Events.Count
            );
            return;
        }

        owner.ArrivalDate = arrivalDate;
        owner.BirthDate = birthDate;
        owner.UpdatedAt = now;
        db.Events.RemoveRange(owner.Events);
        owner.Events = snapshot.Events
            .Select(snapshotEvent => new PuppyEvent
            {
                UserId = owner.Id,
                ClientEventId = snapshotEvent.Id,
                Type = snapshotEvent.Type,
                OccurredAt = snapshotEvent.OccurredAt,
                DateKey = DateOnly.Parse(snapshotEvent.DateKey),
                MetadataJson = snapshotEvent.Details?.GetRawText(),
            })
            .ToList();

        if (actor.Events.Count > 0)
        {
            db.Events.RemoveRange(actor.Events);
            actor.Events.Clear();
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Synced PawPrints snapshot from collaborator {Email} onto owner id {OwnerId}; stored {EventCount} shared events.",
            email,
            owner.Id,
            owner.Events.Count
        );
    }
}
