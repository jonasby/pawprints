using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PawPrints.Api.Contracts;
using PawPrints.Api.Data;

namespace PawPrints.Api.Invites;

public sealed class InviteService(PawPrintsDbContext db, ILogger<InviteService> logger)
{
    private static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);

    public async Task<CreateInviteResponse?> CreateInviteAsync(string ownerEmail, CancellationToken cancellationToken)
    {
        logger.LogInformation("Creating PawPrints collaboration invite for {OwnerEmail}.", ownerEmail);

        var owner = await db.Users.SingleOrDefaultAsync(storedUser => storedUser.Email == ownerEmail, cancellationToken);
        if (owner is null)
        {
            logger.LogWarning("Invite creation failed because no PawPrints profile exists for {OwnerEmail}.", ownerEmail);
            return null;
        }

        if (owner.CollaboratesWithUserId is not null)
        {
            logger.LogWarning(
                "Invite creation rejected because {Email} collaborates on another owner's log.",
                ownerEmail
            );
            throw new InvalidOperationException("Collaborators cannot create invites.");
        }

        var rawToken = new byte[32];
        RandomNumberGenerator.Fill(rawToken);
        var tokenPlaintext = Convert.ToHexString(rawToken);
        var tokenHash = Convert.ToHexString(SHA256.HashData(rawToken));

        var now = DateTimeOffset.UtcNow;
        var invite = new PawPrintsInvite
        {
            TokenHash = tokenHash,
            OwnerUserId = owner.Id,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(InviteLifetime),
        };
        db.Invites.Add(invite);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Created collaboration invite id {InviteId} for owner user id {OwnerUserId}; expires {ExpiresAtUtc:o}.",
            invite.Id,
            owner.Id,
            invite.ExpiresAtUtc
        );

        return new CreateInviteResponse(tokenPlaintext, invite.ExpiresAtUtc);
    }

    public async Task<AcceptInviteResult> AcceptInviteAsync(
        string tokenPlaintext,
        string inviteeEmail,
        string inviteeSubject,
        CancellationToken cancellationToken
    )
    {
        logger.LogInformation("Accepting collaboration invite for invitee {InviteeEmail}.", inviteeEmail);

        string tokenHash;
        try
        {
            tokenHash = HashInviteTokenPlaintext(tokenPlaintext);
        }
        catch (FormatException)
        {
            logger.LogWarning("Invite acceptance failed: token was not valid hex.");
            return AcceptInviteResult.Fail("Invite not found or expired.", StatusCodes.Status404NotFound);
        }

        var invite = await db.Invites
            .Include(storedInvite => storedInvite.Owner)
            .SingleOrDefaultAsync(storedInvite => storedInvite.TokenHash == tokenHash, cancellationToken);

        if (invite is null)
        {
            logger.LogWarning("Invite acceptance failed: token not found.");
            return AcceptInviteResult.Fail("Invite not found or expired.", StatusCodes.Status404NotFound);
        }

        if (invite.ConsumedAtUtc is not null)
        {
            logger.LogWarning("Invite acceptance failed: invite id {InviteId} already consumed.", invite.Id);
            return AcceptInviteResult.Fail("This invite was already used.", StatusCodes.Status409Conflict);
        }

        if (invite.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            logger.LogWarning("Invite acceptance failed: invite id {InviteId} expired at {ExpiresAtUtc:o}.", invite.Id, invite.ExpiresAtUtc);
            return AcceptInviteResult.Fail("This invite has expired.", StatusCodes.Status410Gone);
        }

        var owner = invite.Owner;
        if (string.Equals(owner.Email, inviteeEmail, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Invite acceptance rejected: invitee matches owner email.");
            return AcceptInviteResult.Fail("You cannot accept your own invite.", StatusCodes.Status400BadRequest);
        }

        var invitee = await db.Users.Include(storedUser => storedUser.Events).SingleOrDefaultAsync(
            storedUser => storedUser.Email == inviteeEmail,
            cancellationToken
        );

        var now = DateTimeOffset.UtcNow;

        if (invitee is not null && invitee.CollaboratesWithUserId is long existingOwnerId && existingOwnerId == owner.Id)
        {
            logger.LogInformation("{InviteeEmail} already shares this owner's log; invite accept is a no-op.", inviteeEmail);
            return AcceptInviteResult.Ok();
        }

        if (invitee is not null && invitee.CollaboratesWithUserId is long otherOwnerId && otherOwnerId != owner.Id)
        {
            logger.LogWarning(
                "Invite acceptance rejected: {InviteeEmail} already collaborates with user id {ExistingOwnerId}.",
                inviteeEmail,
                otherOwnerId
            );
            return AcceptInviteResult.Fail(
                "This account already shares another puppy log.",
                StatusCodes.Status409Conflict
            );
        }

        if (invitee is null)
        {
            invitee = new PawPrintsUser
            {
                Email = inviteeEmail,
                ExternalSubject = inviteeSubject,
                ArrivalDate = owner.ArrivalDate,
                BirthDate = owner.BirthDate,
                CreatedAt = now,
                UpdatedAt = now,
                CollaboratesWithUserId = owner.Id,
            };
            db.Users.Add(invitee);
            logger.LogInformation(
                "Creating PawPrints profile for {InviteeEmail} as collaborator to owner user id {OwnerUserId}.",
                inviteeEmail,
                owner.Id
            );
        }
        else
        {
            invitee.ExternalSubject = inviteeSubject;
            invitee.CollaboratesWithUserId = owner.Id;
            invitee.UpdatedAt = now;
            if (invitee.Events.Count > 0)
            {
                db.Events.RemoveRange(invitee.Events);
                invitee.Events.Clear();
            }

            logger.LogInformation(
                "Linking existing PawPrints profile for {InviteeEmail} as collaborator to owner user id {OwnerUserId}.",
                inviteeEmail,
                owner.Id
            );
        }

        invite.ConsumedAtUtc = now;
        invite.ConsumedBy = invitee;

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Invite id {InviteId} consumed by user id {InviteeUserId}; collaborator now follows owner user id {OwnerUserId}.",
            invite.Id,
            invitee.Id,
            owner.Id
        );

        return AcceptInviteResult.Ok();
    }

    private static string HashInviteTokenPlaintext(string tokenPlaintext)
    {
        var normalized = tokenPlaintext.Trim();
        var rawBytes = Convert.FromHexString(normalized);
        return Convert.ToHexString(SHA256.HashData(rawBytes));
    }
}

public sealed record AcceptInviteResult(bool Success, string? Error, int StatusCode)
{
    public static AcceptInviteResult Ok()
    {
        return new AcceptInviteResult(true, null, StatusCodes.Status204NoContent);
    }

    public static AcceptInviteResult Fail(string error, int statusCode)
    {
        return new AcceptInviteResult(false, error, statusCode);
    }
}
