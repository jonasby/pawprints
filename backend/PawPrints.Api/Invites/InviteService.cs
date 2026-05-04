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
        var owner = await db.Users.SingleOrDefaultAsync(storedUser => storedUser.Email == ownerEmail, cancellationToken);
        if (owner is null)
        {
            logger.LogWarning(
                "Collaboration invite create completed with outcome {Outcome} for owner email {OwnerEmail}",
                "ProfileNotFound",
                ownerEmail
            );
            return null;
        }

        if (owner.CollaboratesWithUserId is not null)
        {
            logger.LogWarning(
                "Collaboration invite create completed with outcome {Outcome} for owner email {OwnerEmail}",
                "CollaboratorCannotInvite",
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
            "Collaboration invite create completed with outcome {Outcome} invite id {InviteId} owner user id {OwnerUserId} owner email {OwnerEmail} expires at {ExpiresAtUtc:o}",
            "Created",
            invite.Id,
            owner.Id,
            ownerEmail,
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
        string outcome;
        long? inviteId = null;
        long? inviteeUserId = null;
        long? ownerUserId = null;

        string tokenHash;
        try
        {
            tokenHash = HashInviteTokenPlaintext(tokenPlaintext);
        }
        catch (FormatException)
        {
            outcome = "InvalidTokenFormat";
            LogAcceptComplete(outcome, inviteeEmail, inviteId, inviteeUserId, ownerUserId);
            return AcceptInviteResult.Fail("Invite not found or expired.", StatusCodes.Status404NotFound);
        }

        var invite = await db.Invites
            .Include(storedInvite => storedInvite.Owner)
            .SingleOrDefaultAsync(storedInvite => storedInvite.TokenHash == tokenHash, cancellationToken);

        if (invite is null)
        {
            outcome = "TokenNotFound";
            LogAcceptComplete(outcome, inviteeEmail, inviteId, inviteeUserId, ownerUserId);
            return AcceptInviteResult.Fail("Invite not found or expired.", StatusCodes.Status404NotFound);
        }

        inviteId = invite.Id;
        ownerUserId = invite.OwnerUserId;

        if (invite.ConsumedAtUtc is not null)
        {
            outcome = "AlreadyConsumed";
            LogAcceptComplete(outcome, inviteeEmail, inviteId, inviteeUserId, ownerUserId);
            return AcceptInviteResult.Fail("This invite was already used.", StatusCodes.Status409Conflict);
        }

        if (invite.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            outcome = "Expired";
            LogAcceptComplete(outcome, inviteeEmail, inviteId, inviteeUserId, ownerUserId);
            return AcceptInviteResult.Fail("This invite has expired.", StatusCodes.Status410Gone);
        }

        var owner = invite.Owner;
        if (string.Equals(owner.Email, inviteeEmail, StringComparison.OrdinalIgnoreCase))
        {
            outcome = "InviteeIsOwner";
            LogAcceptComplete(outcome, inviteeEmail, inviteId, inviteeUserId, ownerUserId);
            return AcceptInviteResult.Fail("You cannot accept your own invite.", StatusCodes.Status400BadRequest);
        }

        var invitee = await db.Users.Include(storedUser => storedUser.Events).SingleOrDefaultAsync(
            storedUser => storedUser.Email == inviteeEmail,
            cancellationToken
        );

        var now = DateTimeOffset.UtcNow;
        var createdNewProfile = false;

        if (invitee is not null && invitee.CollaboratesWithUserId is long existingOwnerId && existingOwnerId == owner.Id)
        {
            outcome = "AlreadyLinkedNoOp";
            inviteeUserId = invitee.Id;
            LogAcceptComplete(outcome, inviteeEmail, inviteId, inviteeUserId, ownerUserId);
            return AcceptInviteResult.Ok();
        }

        if (invitee is not null && invitee.CollaboratesWithUserId is long otherOwnerId && otherOwnerId != owner.Id)
        {
            outcome = "AlreadyCollaboratesElsewhere";
            inviteeUserId = invitee.Id;
            LogAcceptComplete(outcome, inviteeEmail, inviteId, inviteeUserId, ownerUserId);
            return AcceptInviteResult.Fail(
                "This account already shares another puppy log.",
                StatusCodes.Status409Conflict
            );
        }

        if (invitee is null)
        {
            createdNewProfile = true;
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
        }

        invite.ConsumedAtUtc = now;
        invite.ConsumedBy = invitee;

        await db.SaveChangesAsync(cancellationToken);

        inviteeUserId = invitee.Id;
        outcome = createdNewProfile ? "ConsumedNewProfile" : "ConsumedLinkedProfile";

        LogAcceptComplete(outcome, inviteeEmail, inviteId, inviteeUserId, ownerUserId);

        return AcceptInviteResult.Ok();

        void LogAcceptComplete(
            string completedOutcome,
            string email,
            long? completedInviteId,
            long? completedInviteeUserId,
            long? completedOwnerUserId
        )
        {
            logger.LogInformation(
                "Collaboration invite accept completed with outcome {Outcome} invitee email {InviteeEmail} invite id {InviteId} invitee user id {InviteeUserId} owner user id {OwnerUserId}",
                completedOutcome,
                email,
                completedInviteId,
                completedInviteeUserId,
                completedOwnerUserId
            );
        }
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
