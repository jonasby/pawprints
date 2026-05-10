using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PawPrints.Api.Contracts;
using PawPrints.Api.Data;

namespace PawPrints.Api.Hubs;

public sealed class SyncHub(
    IServiceScopeFactory scopeFactory,
    ILogger<SyncHub> logger
) : Hub
{
    private const string SyncGroupContextKey = "PawPrints.SyncGroup";

    [Authorize(Policy = "AuthenticatedPawPrintsUser")]
    public async Task<SyncSnapshotRequest?> GetSnapshot()
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var syncService = scope.ServiceProvider.GetRequiredService<SnapshotSyncService>();
        var db = scope.ServiceProvider.GetRequiredService<PawPrintsDbContext>();

        var email = RequireEmail();
        await EnsureCorrectSyncGroupAsync(db, email, Context.ConnectionAborted);

        var snapshot = await syncService.GetSnapshotAsync(email, Context.ConnectionAborted);
        logger.LogInformation(
            "SignalR snapshot read completed for {Email} hasData {HasData}",
            email,
            snapshot is not null
        );
        return snapshot;
    }

    [Authorize(Policy = "AuthenticatedPawPrintsUser")]
    public async Task PushSnapshot(SyncSnapshotRequest snapshot)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var syncService = scope.ServiceProvider.GetRequiredService<SnapshotSyncService>();
        var db = scope.ServiceProvider.GetRequiredService<PawPrintsDbContext>();

        var email = RequireEmail();
        var subject =
            Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? email;

        await syncService.SyncAsync(email, subject, snapshot, Context.ConnectionAborted);

        var groupName = await EnsureCorrectSyncGroupAsync(db, email, Context.ConnectionAborted);
        var fresh = await syncService.GetSnapshotAsync(email, Context.ConnectionAborted);

        if (fresh is null)
        {
            logger.LogWarning(
                "SignalR push broadcast skipped because snapshot reload returned null for {Email}",
                email
            );
            return;
        }

        logger.LogInformation(
            "SignalR snapshot push broadcast for group {SyncGroup} excluding connection {ConnectionId}",
            groupName,
            Context.ConnectionId
        );

        await Clients
            .GroupExcept(groupName, Context.ConnectionId)
            .SendAsync("SnapshotUpdated", fresh, Context.ConnectionAborted);
    }

    private string RequireEmail()
    {
        var email = Context.User?.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new HubException("Missing email claim.");
        }

        return email;
    }

    private async Task<string> EnsureCorrectSyncGroupAsync(
        PawPrintsDbContext db,
        string email,
        CancellationToken cancellationToken
    )
    {
        var newGroup = await ResolveSyncGroupNameAsync(db, email, cancellationToken);

        if (
            Context.Items[SyncGroupContextKey] is string oldGroup
            && !string.Equals(oldGroup, newGroup, StringComparison.Ordinal)
        )
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, oldGroup);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, newGroup);
        Context.Items[SyncGroupContextKey] = newGroup;
        return newGroup;
    }

    private static async Task<string> ResolveSyncGroupNameAsync(
        PawPrintsDbContext db,
        string email,
        CancellationToken cancellationToken
    )
    {
        var actor = await db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(storedUser => storedUser.Email == email, cancellationToken);

        if (actor is null)
        {
            return $"sync-pending:{email}";
        }

        if (actor.CollaboratesWithUserId is long ownerId)
        {
            return $"sync-owner:{ownerId}";
        }

        return $"sync-owner:{actor.Id}";
    }
}
