using Microsoft.EntityFrameworkCore;
using PawPrints.Api.Contracts;
using PawPrints.Api.Data;

namespace PawPrints.Api.Sync;

public sealed class SnapshotSyncService(
    PawPrintsDbContext db,
    PredictionEvaluator predictionEvaluator,
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

        var incomingUpserts = GetIncomingUpserts(snapshot);
        var incomingDeletedEventIds = snapshot.DeletedEventIds ?? [];
        var usesDeltaSync = snapshot.Upserts is not null || snapshot.DeletedEventIds is not null;

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
            newOwner.Events = incomingUpserts
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
                "Snapshot sync completed with outcome {Outcome} actor email {ActorEmail} upsert count {UpsertCount} delete count {DeleteCount} events stored {EventCount} owner user id {OwnerUserId}",
                "CreatedOwner",
                email,
                incomingUpserts.Count,
                incomingDeletedEventIds.Count,
                newOwner.Events.Count,
                newOwner.Id
            );
            await predictionEvaluator.EvaluateForUserAsync(newOwner.Id, cancellationToken);
            return;
        }

        actor.ExternalSubject = externalSubject;
        actor.UpdatedAt = now;

        if (actor.CollaboratesWithUserId is null)
        {
            actor.ArrivalDate = arrivalDate;
            actor.BirthDate = birthDate;
            ApplyEventChanges(actor, incomingUpserts, incomingDeletedEventIds, usesDeltaSync);

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Snapshot sync completed with outcome {Outcome} actor email {ActorEmail} upsert count {UpsertCount} delete count {DeleteCount} events stored {EventCount} owner user id {OwnerUserId}",
                "UpdatedOwner",
                email,
                incomingUpserts.Count,
                incomingDeletedEventIds.Count,
                actor.Events.Count,
                actor.Id
            );
            await predictionEvaluator.EvaluateForUserAsync(actor.Id, cancellationToken);
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
            ApplyEventChanges(actor, incomingUpserts, incomingDeletedEventIds, usesDeltaSync);

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Snapshot sync completed with outcome {Outcome} actor email {ActorEmail} upsert count {UpsertCount} delete count {DeleteCount} events stored {EventCount} owner user id {OwnerUserId}",
                "RecoveredStandaloneOwner",
                email,
                incomingUpserts.Count,
                incomingDeletedEventIds.Count,
                actor.Events.Count,
                actor.Id
            );
            await predictionEvaluator.EvaluateForUserAsync(actor.Id, cancellationToken);
            return;
        }

        owner.ArrivalDate = arrivalDate;
        owner.BirthDate = birthDate;
        owner.UpdatedAt = now;
        ApplyEventChanges(owner, incomingUpserts, incomingDeletedEventIds, usesDeltaSync);

        if (actor.Events.Count > 0)
        {
            db.Events.RemoveRange(actor.Events);
            actor.Events.Clear();
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Snapshot sync completed with outcome {Outcome} actor email {ActorEmail} upsert count {UpsertCount} delete count {DeleteCount} events stored {EventCount} owner user id {OwnerUserId}",
            "UpdatedSharedOwner",
            email,
            incomingUpserts.Count,
            incomingDeletedEventIds.Count,
            owner.Events.Count,
            owner.Id
        );
        await predictionEvaluator.EvaluateForUserAsync(owner.Id, cancellationToken);
    }

    private static IReadOnlyCollection<SyncEventRequest> GetIncomingUpserts(SyncSnapshotRequest snapshot)
    {
        return snapshot.Upserts ?? snapshot.Events ?? [];
    }

    private void ApplyEventChanges(
        PawPrintsUser user,
        IReadOnlyCollection<SyncEventRequest> upserts,
        IReadOnlyCollection<string> deletedEventIds,
        bool useDeltaSync
    )
    {
        if (!useDeltaSync)
        {
            db.Events.RemoveRange(user.Events);
            user.Events = upserts
                .Select(snapshotEvent => new PuppyEvent
                {
                    UserId = user.Id,
                    ClientEventId = snapshotEvent.Id,
                    Type = snapshotEvent.Type,
                    OccurredAt = snapshotEvent.OccurredAt,
                    DateKey = DateOnly.Parse(snapshotEvent.DateKey),
                    MetadataJson = snapshotEvent.Details?.GetRawText(),
                })
                .ToList();
            return;
        }

        var eventsByClientId = user.Events.ToDictionary(existing => existing.ClientEventId, StringComparer.Ordinal);

        foreach (var upsert in upserts)
        {
            if (eventsByClientId.TryGetValue(upsert.Id, out var existing))
            {
                existing.Type = upsert.Type;
                existing.OccurredAt = upsert.OccurredAt;
                existing.DateKey = DateOnly.Parse(upsert.DateKey);
                existing.MetadataJson = upsert.Details?.GetRawText();
                continue;
            }

            user.Events.Add(new PuppyEvent
            {
                UserId = user.Id,
                ClientEventId = upsert.Id,
                Type = upsert.Type,
                OccurredAt = upsert.OccurredAt,
                DateKey = DateOnly.Parse(upsert.DateKey),
                MetadataJson = upsert.Details?.GetRawText(),
            });
        }

        if (deletedEventIds.Count == 0)
        {
            return;
        }

        var deletedIdSet = deletedEventIds.ToHashSet(StringComparer.Ordinal);
        var toRemove = user.Events.Where(stored => deletedIdSet.Contains(stored.ClientEventId)).ToList();
        if (toRemove.Count > 0)
        {
            db.Events.RemoveRange(toRemove);
            foreach (var deleted in toRemove)
            {
                user.Events.Remove(deleted);
            }
        }
    }
}
