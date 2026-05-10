using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PawPrints.Api.Contracts;
using PawPrints.Api.Data;

namespace PawPrints.Api.Tests;

public sealed class InviteApiTests
{
    [Fact]
    public async Task GivenSignedInOwner_WhenCreatingInvite_ThenTokenIsReturned()
    {
        await using var application = new PawPrintsApiApplication();
        using var client = application.CreateAuthenticatedClient("owner@gmail.com");

        await SyncHubTestConnections.PushSnapshotAsync(application, "owner@gmail.com", InviteApiTestData.CreateSnapshot());

        var response = await client.PostAsync("/api/invites", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateInviteResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body.Token));
        Assert.True(body.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task GivenInvite_WhenCollaboratorAccepts_ThenCollaboratorSyncAppliesToOwnerSnapshot()
    {
        await using var application = new PawPrintsApiApplication();
        using var ownerClient = application.CreateAuthenticatedClient("owner@gmail.com");
        await SyncHubTestConnections.PushSnapshotAsync(application, "owner@gmail.com", InviteApiTestData.CreateSnapshot());

        var inviteResponse = await ownerClient.PostAsync("/api/invites", content: null);
        var invite = await inviteResponse.Content.ReadFromJsonAsync<CreateInviteResponse>();
        Assert.NotNull(invite);

        using var collaboratorClient = application.CreateAuthenticatedClient("helper@gmail.com");
        var acceptResponse = await collaboratorClient.PostAsync(
            $"/api/invites/{Uri.EscapeDataString(invite.Token)}/accept",
            content: null
        );
        Assert.Equal(HttpStatusCode.NoContent, acceptResponse.StatusCode);

        var changedSnapshot = InviteApiTestData.CreateSnapshot(
            new SyncEventRequest(
                "evt-helper",
                "eat",
                DateTimeOffset.Parse("2026-04-26T14:00:00Z"),
                "2026-04-26"
            )
        );
        await SyncHubTestConnections.PushSnapshotAsync(application, "helper@gmail.com", changedSnapshot);

        using var scope = application.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PawPrintsDbContext>();
        var owner = await db.Users.Include(storedUser => storedUser.Events).SingleAsync(
            storedUser => storedUser.Email == "owner@gmail.com"
        );
        var collaborator = await db.Users.SingleAsync(storedUser => storedUser.Email == "helper@gmail.com");
        Assert.Equal(owner.Id, collaborator.CollaboratesWithUserId);
        Assert.Equal("evt-helper", Assert.Single(owner.Events).ClientEventId);
    }

    [Fact]
    public async Task GivenSignedInUser_WhenMeIncludesCollaboration_ThenCollaboratorSeesOwnerEmail()
    {
        await using var application = new PawPrintsApiApplication();
        using var ownerClient = application.CreateAuthenticatedClient("owner@gmail.com");
        await SyncHubTestConnections.PushSnapshotAsync(application, "owner@gmail.com", InviteApiTestData.CreateSnapshot());
        var inviteResponse = await ownerClient.PostAsync("/api/invites", content: null);
        var invite = await inviteResponse.Content.ReadFromJsonAsync<CreateInviteResponse>();
        Assert.NotNull(invite);

        using var collaboratorClient = application.CreateAuthenticatedClient("helper@gmail.com");
        await collaboratorClient.PostAsync(
            $"/api/invites/{Uri.EscapeDataString(invite.Token)}/accept",
            content: null
        );

        var me = await collaboratorClient.GetFromJsonAsync<MeResponse>("/api/auth/me");
        Assert.NotNull(me);
        Assert.Equal("helper@gmail.com", me.Email);
        Assert.NotNull(me.Collaboration);
        Assert.Equal("collaborator", me.Collaboration.Role);
        Assert.Equal("owner@gmail.com", me.Collaboration.OwnerEmail);
    }
}

internal static class InviteApiTestData
{
    public static SyncSnapshotRequest CreateSnapshot(params SyncEventRequest[] events)
    {
        var snapshotEvents = events.Length > 0
            ? events
            : [
                new SyncEventRequest("evt-sleep", "sleep", DateTimeOffset.Parse("2026-04-25T22:00:00Z"), "2026-04-25"),
            ];

        return new SyncSnapshotRequest(
            new SyncSettingsRequest("2026-04-19", "2026-02-22"),
            snapshotEvents
        );
    }
}
