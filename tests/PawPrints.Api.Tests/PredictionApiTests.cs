using System.Net.Http.Json;
using System.Text.Json;
using PawPrints.Api.Contracts;

namespace PawPrints.Api.Tests;

public sealed class PredictionApiTests
{
    [Fact]
    public async Task GivenHistoricalNaps_WhenCurrentNapIsSynced_ThenWakePredictionAndDueNotificationAreGenerated()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-04-30T08:15:00Z"));
        await using var application = new PawPrintsApiApplication(clock);
        using var client = application.CreateAuthenticatedClient("jon.asby@gmail.com");

        var syncResponse = await client.PutAsJsonAsync("/api/sync", CreateSnapshot(
            new SyncEventRequest("nap-1", "nap", DateTimeOffset.Parse("2026-04-27T08:00:00Z"), "2026-04-27"),
            new SyncEventRequest("wake-1", "wake", DateTimeOffset.Parse("2026-04-27T09:00:00Z"), "2026-04-27"),
            new SyncEventRequest("nap-2", "nap", DateTimeOffset.Parse("2026-04-28T08:10:00Z"), "2026-04-28"),
            new SyncEventRequest("wake-2", "wake", DateTimeOffset.Parse("2026-04-28T09:20:00Z"), "2026-04-28"),
            new SyncEventRequest("nap-3", "nap", DateTimeOffset.Parse("2026-04-29T08:15:00Z"), "2026-04-29"),
            new SyncEventRequest("wake-3", "wake", DateTimeOffset.Parse("2026-04-29T09:20:00Z"), "2026-04-29"),
            new SyncEventRequest("nap-now", "nap", DateTimeOffset.Parse("2026-04-30T08:00:00Z"), "2026-04-30")
        ));
        syncResponse.EnsureSuccessStatusCode();

        var predictionsResponse = await client.GetAsync("/api/predictions");

        predictionsResponse.EnsureSuccessStatusCode();
        using var predictions = JsonDocument.Parse(await predictionsResponse.Content.ReadAsStringAsync());
        var wakePrediction = Assert.Single(predictions.RootElement.EnumerateArray());
        Assert.Equal("nap_wake", wakePrediction.GetProperty("type").GetString());
        Assert.Equal("active", wakePrediction.GetProperty("status").GetString());
        Assert.Equal("nap-now", wakePrediction.GetProperty("triggerEventClientId").GetString());
        Assert.Equal("2026-04-30T09:00:00+00:00", wakePrediction.GetProperty("windowStart").GetString());
        Assert.Equal("2026-04-30T09:10:00+00:00", wakePrediction.GetProperty("bestGuessAt").GetString());
        Assert.Equal("2026-04-30T09:20:00+00:00", wakePrediction.GetProperty("windowEnd").GetString());

        clock.SetUtcNow(DateTimeOffset.Parse("2026-04-30T08:50:00Z"));
        var notificationsResponse = await client.GetAsync("/api/notifications/due");

        notificationsResponse.EnsureSuccessStatusCode();
        using var notifications = JsonDocument.Parse(await notificationsResponse.Content.ReadAsStringAsync());
        var notification = Assert.Single(notifications.RootElement.EnumerateArray());
        Assert.Equal("nap_wake", notification.GetProperty("type").GetString());
        Assert.Contains("wake", notification.GetProperty("title").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GivenFoodWakeAndHistoricalPoos_WhenCurrentContextIsSynced_ThenPooNotificationIsDue()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-04-30T08:35:00Z"));
        await using var application = new PawPrintsApiApplication(clock);
        using var client = application.CreateAuthenticatedClient("jon.asby@gmail.com");

        var syncResponse = await client.PutAsJsonAsync("/api/sync", CreateSnapshot(
            new SyncEventRequest("wake-1", "wake", DateTimeOffset.Parse("2026-04-27T07:50:00Z"), "2026-04-27"),
            new SyncEventRequest("eat-1", "eat", DateTimeOffset.Parse("2026-04-27T08:00:00Z"), "2026-04-27"),
            new SyncEventRequest("poop-1", "poop", DateTimeOffset.Parse("2026-04-27T08:35:00Z"), "2026-04-27"),
            new SyncEventRequest("wake-2", "wake", DateTimeOffset.Parse("2026-04-28T07:45:00Z"), "2026-04-28"),
            new SyncEventRequest("eat-2", "eat", DateTimeOffset.Parse("2026-04-28T08:05:00Z"), "2026-04-28"),
            new SyncEventRequest("poop-2", "poop", DateTimeOffset.Parse("2026-04-28T08:40:00Z"), "2026-04-28"),
            new SyncEventRequest("wake-now", "wake", DateTimeOffset.Parse("2026-04-30T07:55:00Z"), "2026-04-30"),
            new SyncEventRequest("eat-now", "eat", DateTimeOffset.Parse("2026-04-30T08:00:00Z"), "2026-04-30")
        ));
        syncResponse.EnsureSuccessStatusCode();

        var notificationsResponse = await client.GetAsync("/api/notifications/due");

        notificationsResponse.EnsureSuccessStatusCode();
        using var notifications = JsonDocument.Parse(await notificationsResponse.Content.ReadAsStringAsync());
        var notification = Assert.Single(notifications.RootElement.EnumerateArray());
        Assert.Equal("poop_need", notification.GetProperty("type").GetString());
        Assert.Contains("poo", notification.GetProperty("title").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("likely", notification.GetProperty("body").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GivenPendingWakeNotification_WhenWakeIsSynced_ThenNotificationIsCancelled()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-04-30T08:15:00Z"));
        await using var application = new PawPrintsApiApplication(clock);
        using var client = application.CreateAuthenticatedClient("jon.asby@gmail.com");
        await client.PutAsJsonAsync("/api/sync", CreateSnapshot(
            new SyncEventRequest("nap-1", "nap", DateTimeOffset.Parse("2026-04-27T08:00:00Z"), "2026-04-27"),
            new SyncEventRequest("wake-1", "wake", DateTimeOffset.Parse("2026-04-27T09:00:00Z"), "2026-04-27"),
            new SyncEventRequest("nap-2", "nap", DateTimeOffset.Parse("2026-04-28T08:10:00Z"), "2026-04-28"),
            new SyncEventRequest("wake-2", "wake", DateTimeOffset.Parse("2026-04-28T09:20:00Z"), "2026-04-28"),
            new SyncEventRequest("nap-3", "nap", DateTimeOffset.Parse("2026-04-29T08:15:00Z"), "2026-04-29"),
            new SyncEventRequest("wake-3", "wake", DateTimeOffset.Parse("2026-04-29T09:20:00Z"), "2026-04-29"),
            new SyncEventRequest("nap-now", "nap", DateTimeOffset.Parse("2026-04-30T08:00:00Z"), "2026-04-30")
        ));

        var resolveResponse = await client.PutAsJsonAsync("/api/sync", CreateDelta(
            new SyncEventRequest("wake-now", "wake", DateTimeOffset.Parse("2026-04-30T08:45:00Z"), "2026-04-30")
        ));
        resolveResponse.EnsureSuccessStatusCode();
        clock.SetUtcNow(DateTimeOffset.Parse("2026-04-30T08:50:00Z"));

        var notificationsResponse = await client.GetAsync("/api/notifications/due");

        notificationsResponse.EnsureSuccessStatusCode();
        using var notifications = JsonDocument.Parse(await notificationsResponse.Content.ReadAsStringAsync());
        Assert.Empty(notifications.RootElement.EnumerateArray());
    }

    private static SyncSnapshotRequest CreateSnapshot(params SyncEventRequest[] events)
    {
        return new SyncSnapshotRequest(
            new SyncSettingsRequest("2026-04-19", "2026-02-22"),
            events
        );
    }

    private static SyncSnapshotRequest CreateDelta(params SyncEventRequest[] upserts)
    {
        return new SyncSnapshotRequest(
            new SyncSettingsRequest("2026-04-19", "2026-02-22"),
            Upserts: upserts
        );
    }
}

public sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow()
    {
        return _utcNow;
    }

    public void SetUtcNow(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }
}
