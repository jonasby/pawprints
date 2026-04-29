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
        var actor = await db.Users
            .AsNoTracking()
            .Include(storedUser => storedUser.Events)
            .SingleOrDefaultAsync(storedUser => storedUser.Email == email, cancellationToken);

        if (actor is null)
        {
            logger.LogInformation(
                "Snapshot load completed with outcome {Outcome} email {Email} event count {EventCount} owner id {OwnerUserId} collaborator email {CollaboratorEmail}",
                "NoProfile",
                email,
                0,
                null,
                null
            );
            return null;
        }

        var dataUser = actor;
        long? ownerIdUsed = null;
        string? collaboratorEmail = null;
        string outcome;

        if (actor.CollaboratesWithUserId is long ownerId)
        {
            var owner = await db.Users
                .AsNoTracking()
                .Include(storedUser => storedUser.Events)
                .SingleOrDefaultAsync(storedUser => storedUser.Id == ownerId, cancellationToken);
            if (owner is null)
            {
                outcome = "MissingOwnerFallbackSelf";
                ownerIdUsed = ownerId;
            }
            else
            {
                outcome = "SharedFromOwner";
                dataUser = owner;
                ownerIdUsed = ownerId;
                collaboratorEmail = email;
            }
        }
        else
        {
            outcome = "OwnerData";
        }

        logger.LogInformation(
            "Snapshot load completed with outcome {Outcome} email {Email} event count {EventCount} owner id {OwnerUserId} collaborator email {CollaboratorEmail}",
            outcome,
            dataUser.Email,
            dataUser.Events.Count,
            ownerIdUsed,
            collaboratorEmail
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
                "Snapshot sync completed with outcome {Outcome} actor email {ActorEmail} events stored {EventCount} owner user id {OwnerUserId}",
                "CreatedOwner",
                email,
                newOwner.Events.Count,
                newOwner.Id
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
                "Snapshot sync completed with outcome {Outcome} actor email {ActorEmail} events stored {EventCount} owner user id {OwnerUserId}",
                "UpdatedOwner",
                email,
                actor.Events.Count,
                actor.Id
            );
            return;
        }

        var owner = await db.Users
            .Include(storedUser => storedUser.Events)
            .SingleOrDefaultAsync(storedUser => storedUser.Id == actor.CollaboratesWithUserId!.Value, cancellationToken);

        if (owner is null)
        {
            logger.LogWarning(
                "Snapshot sync reconciling missing owner id {OwnerId} for collaborator {ActorEmail}",
                actor.CollaboratesWithUserId,
                email
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
                "Snapshot sync completed with outcome {Outcome} actor email {ActorEmail} events stored {EventCount} owner user id {OwnerUserId}",
                "RecoveredStandaloneOwner",
                email,
                actor.Events.Count,
                actor.Id
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
            "Snapshot sync completed with outcome {Outcome} actor email {ActorEmail} events stored {EventCount} owner user id {OwnerUserId}",
            "UpdatedSharedOwner",
            email,
            owner.Events.Count,
            owner.Id
        );
    }
}
