using System.Net;
using System.Net.Http.Json;
using PawPrints.Api.Contracts;

namespace PawPrints.Api.Tests;

public sealed class PuppyAnalyticsApiTests
{
    [Fact]
    public async Task GivenNoLogin_WhenFetchingPuppyAnalytics_ThenItIsRejected()
    {
        await using var application = new PawPrintsApiApplication();
        using var client = application.CreateClient();

        var response = await client.GetAsync("/api/puppy-analytics");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GivenOwnerEvents_WhenFetchingPuppyAnalytics_ThenDailyMetricsAreReturned()
    {
        await using var application = new PawPrintsApiApplication();
        using var client = application.CreateAuthenticatedClient("owner@gmail.com");

        await client.PutAsJsonAsync("/api/sync", PuppyAnalyticsTestData.CreateSnapshot(
            new SyncEventRequest("sleep-apr25", "sleep", DateTimeOffset.Parse("2026-04-25T22:00:00Z"), "2026-04-25"),
            new SyncEventRequest("wake-apr26", "wake", DateTimeOffset.Parse("2026-04-26T07:00:00Z"), "2026-04-26"),
            new SyncEventRequest("pee-1-apr26", "pee", DateTimeOffset.Parse("2026-04-26T07:10:00Z"), "2026-04-26"),
            new SyncEventRequest("pee-2-apr26", "pee", DateTimeOffset.Parse("2026-04-26T08:20:00Z"), "2026-04-26"),
            new SyncEventRequest("poop-apr26", "poop", DateTimeOffset.Parse("2026-04-26T09:05:00Z"), "2026-04-26"),
            new SyncEventRequest("nap-apr26", "nap", DateTimeOffset.Parse("2026-04-26T10:00:00Z"), "2026-04-26"),
            new SyncEventRequest("wake-nap-apr26", "wake", DateTimeOffset.Parse("2026-04-26T11:10:00Z"), "2026-04-26"),
            new SyncEventRequest("nap-outlier-apr26", "nap", DateTimeOffset.Parse("2026-04-26T13:00:00Z"), "2026-04-26"),
            new SyncEventRequest("wake-outlier-apr26", "wake", DateTimeOffset.Parse("2026-04-26T16:10:00Z"), "2026-04-26"),
            new SyncEventRequest("pee-apr28", "pee", DateTimeOffset.Parse("2026-04-28T06:50:00Z"), "2026-04-28"),
            new SyncEventRequest("sleep-missing-wake", "sleep", DateTimeOffset.Parse("2026-04-28T21:30:00Z"), "2026-04-28")
        ));

        var analytics = await client.GetFromJsonAsync<PuppyAnalyticsResponse>("/api/puppy-analytics");

        Assert.NotNull(analytics);
        Assert.Equal(
            ["2026-04-25", "2026-04-26", "2026-04-28"],
            analytics.Days.Select(day => day.DateKey).ToArray()
        );

        var april25 = Assert.Single(analytics.Days, day => day.DateKey == "2026-04-25");
        Assert.Equal(0, april25.Poops);
        Assert.Equal(0, april25.Wees);
        Assert.Equal(540, april25.SleepMinutes);
        Assert.Null(april25.NapMinutes);

        var april26 = Assert.Single(analytics.Days, day => day.DateKey == "2026-04-26");
        Assert.Equal(1, april26.Poops);
        Assert.Equal(2, april26.Wees);
        Assert.Null(april26.SleepMinutes);
        Assert.Equal(70, april26.NapMinutes);

        var april28 = Assert.Single(analytics.Days, day => day.DateKey == "2026-04-28");
        Assert.Equal(0, april28.Poops);
        Assert.Equal(1, april28.Wees);
        Assert.Null(april28.SleepMinutes);
        Assert.Null(april28.NapMinutes);
    }

    [Fact]
    public async Task GivenSleepWithoutFollowingMorningEvent_WhenFetchingPuppyAnalytics_ThenSleepDurationIsMissing()
    {
        await using var application = new PawPrintsApiApplication();
        using var client = application.CreateAuthenticatedClient("owner@gmail.com");

        await client.PutAsJsonAsync("/api/sync", PuppyAnalyticsTestData.CreateSnapshot(
            new SyncEventRequest("pee-may6", "pee", DateTimeOffset.Parse("2026-05-06T20:30:00Z"), "2026-05-06"),
            new SyncEventRequest("sleep-may6", "sleep", DateTimeOffset.Parse("2026-05-06T22:30:00Z"), "2026-05-06"),
            new SyncEventRequest("pee-may7", "pee", DateTimeOffset.Parse("2026-05-07T12:00:00Z"), "2026-05-07")
        ));

        var analytics = await client.GetFromJsonAsync<PuppyAnalyticsResponse>("/api/puppy-analytics");

        Assert.NotNull(analytics);
        Assert.Equal(
            ["2026-05-06", "2026-05-07"],
            analytics.Days.Select(day => day.DateKey).ToArray()
        );

        var may6 = Assert.Single(analytics.Days, day => day.DateKey == "2026-05-06");
        Assert.Equal(1, may6.Wees);
        Assert.Null(may6.SleepMinutes);

        var may7 = Assert.Single(analytics.Days, day => day.DateKey == "2026-05-07");
        Assert.Equal(1, may7.Wees);
        Assert.Null(may7.SleepMinutes);
    }

    [Fact]
    public async Task GivenOvernightSleepWithUnpairedNightEvents_WhenFetchingPuppyAnalytics_ThenSleepBelongsToNightStartDay()
    {
        await using var application = new PawPrintsApiApplication();
        using var client = application.CreateAuthenticatedClient("owner@gmail.com");

        await client.PutAsJsonAsync("/api/sync", PuppyAnalyticsTestData.CreateSnapshot(
            new SyncEventRequest("sleep-may6", "sleep", DateTimeOffset.Parse("2026-05-06T22:30:00Z"), "2026-05-06"),
            new SyncEventRequest("wee-may7-night", "pee", DateTimeOffset.Parse("2026-05-07T00:40:00Z"), "2026-05-07"),
            new SyncEventRequest("poop-may7-night", "poop", DateTimeOffset.Parse("2026-05-07T01:05:00Z"), "2026-05-07"),
            new SyncEventRequest("extra-sleep-may7", "sleep", DateTimeOffset.Parse("2026-05-07T01:30:00Z"), "2026-05-07"),
            new SyncEventRequest("wake-may7", "wake", DateTimeOffset.Parse("2026-05-07T06:20:00Z"), "2026-05-07"),
            new SyncEventRequest("wee-may7-day", "pee", DateTimeOffset.Parse("2026-05-07T06:25:00Z"), "2026-05-07")
        ));

        var analytics = await client.GetFromJsonAsync<PuppyAnalyticsResponse>("/api/puppy-analytics");

        Assert.NotNull(analytics);
        Assert.Equal(
            ["2026-05-06", "2026-05-07"],
            analytics.Days.Select(day => day.DateKey).ToArray()
        );

        var may6 = Assert.Single(analytics.Days, day => day.DateKey == "2026-05-06");
        Assert.Equal(470, may6.SleepMinutes);
        Assert.Equal(0, may6.Poops);
        Assert.Equal(0, may6.Wees);

        var may7 = Assert.Single(analytics.Days, day => day.DateKey == "2026-05-07");
        Assert.Equal(1, may7.Poops);
        Assert.Equal(2, may7.Wees);
        Assert.Null(may7.SleepMinutes);
    }

    [Fact]
    public async Task GivenOvernightSleepWithLoggedWakePause_WhenFetchingPuppyAnalytics_ThenAwakePauseIsExcludedFromNightSleep()
    {
        await using var application = new PawPrintsApiApplication();
        using var client = application.CreateAuthenticatedClient("owner@gmail.com");

        await client.PutAsJsonAsync("/api/sync", PuppyAnalyticsTestData.CreateSnapshot(
            new SyncEventRequest("sleep-may6", "sleep", DateTimeOffset.Parse("2026-05-06T22:30:00Z"), "2026-05-06"),
            new SyncEventRequest("wake-for-wee-may7", "wake", DateTimeOffset.Parse("2026-05-07T03:00:00Z"), "2026-05-07"),
            new SyncEventRequest("wee-may7-night", "pee", DateTimeOffset.Parse("2026-05-07T03:05:00Z"), "2026-05-07"),
            new SyncEventRequest("back-to-sleep-may7", "sleep", DateTimeOffset.Parse("2026-05-07T03:10:00Z"), "2026-05-07"),
            new SyncEventRequest("wake-may7", "wake", DateTimeOffset.Parse("2026-05-07T06:20:00Z"), "2026-05-07"),
            new SyncEventRequest("wee-may7-day", "pee", DateTimeOffset.Parse("2026-05-07T06:25:00Z"), "2026-05-07")
        ));

        var analytics = await client.GetFromJsonAsync<PuppyAnalyticsResponse>("/api/puppy-analytics");

        Assert.NotNull(analytics);
        Assert.Equal(
            ["2026-05-06", "2026-05-07"],
            analytics.Days.Select(day => day.DateKey).ToArray()
        );

        var may6 = Assert.Single(analytics.Days, day => day.DateKey == "2026-05-06");
        Assert.Equal(460, may6.SleepMinutes);

        var may7 = Assert.Single(analytics.Days, day => day.DateKey == "2026-05-07");
        Assert.Equal(2, may7.Wees);
        Assert.Null(may7.SleepMinutes);
    }

    [Fact]
    public async Task GivenCollaborator_WhenFetchingPuppyAnalytics_ThenOwnerMetricsAreReturned()
    {
        await using var application = new PawPrintsApiApplication();
        using var ownerClient = application.CreateAuthenticatedClient("owner@gmail.com");
        await ownerClient.PutAsJsonAsync("/api/sync", PuppyAnalyticsTestData.CreateSnapshot(
            new SyncEventRequest("sleep-apr25", "sleep", DateTimeOffset.Parse("2026-04-25T22:30:00Z"), "2026-04-25"),
            new SyncEventRequest("wake-apr26", "wake", DateTimeOffset.Parse("2026-04-26T06:30:00Z"), "2026-04-26"),
            new SyncEventRequest("poop-apr26", "poop", DateTimeOffset.Parse("2026-04-26T08:00:00Z"), "2026-04-26")
        ));

        var inviteResponse = await ownerClient.PostAsync("/api/invites", content: null);
        var invite = await inviteResponse.Content.ReadFromJsonAsync<CreateInviteResponse>();
        Assert.NotNull(invite);

        using var collaboratorClient = application.CreateAuthenticatedClient("helper@gmail.com");
        await collaboratorClient.PostAsync(
            $"/api/invites/{Uri.EscapeDataString(invite.Token)}/accept",
            content: null
        );

        var analytics = await collaboratorClient.GetFromJsonAsync<PuppyAnalyticsResponse>("/api/puppy-analytics");

        Assert.NotNull(analytics);
        Assert.Equal(
            ["2026-04-25", "2026-04-26"],
            analytics.Days.Select(day => day.DateKey).ToArray()
        );
        var sleepDay = Assert.Single(analytics.Days, day => day.DateKey == "2026-04-25");
        Assert.Equal(480, sleepDay.SleepMinutes);
        var activityDay = Assert.Single(analytics.Days, day => day.DateKey == "2026-04-26");
        Assert.Equal(1, activityDay.Poops);
        Assert.Null(activityDay.SleepMinutes);
    }

    private sealed record PuppyAnalyticsResponse(PuppyAnalyticsDayResponse[] Days);

    private sealed record PuppyAnalyticsDayResponse(
        string DateKey,
        int Poops,
        int Wees,
        int? SleepMinutes,
        int? NapMinutes
    );
}

internal static class PuppyAnalyticsTestData
{
    public static SyncSnapshotRequest CreateSnapshot(params SyncEventRequest[] events)
    {
        return new SyncSnapshotRequest(
            new SyncSettingsRequest("2026-04-19", "2026-02-22"),
            events
        );
    }
}
