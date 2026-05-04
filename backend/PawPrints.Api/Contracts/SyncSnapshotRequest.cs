using System.Text.Json;

namespace PawPrints.Api.Contracts;

public sealed record SyncSnapshotRequest(
    SyncSettingsRequest Settings,
    IReadOnlyCollection<SyncEventRequest>? Events = null,
    IReadOnlyCollection<SyncEventRequest>? Upserts = null,
    IReadOnlyCollection<string>? DeletedEventIds = null
);

public sealed record SyncSettingsRequest(
    string ArrivalDate,
    string BirthDate
);

public sealed record SyncEventRequest(
    string Id,
    string Type,
    DateTimeOffset OccurredAt,
    string DateKey,
    JsonElement? Details = null
);
