using System.Text.Json;

namespace PawPrints.Api.Contracts;

public sealed record SyncSnapshotRequest(
    SyncSettingsRequest Settings,
    IReadOnlyCollection<SyncEventRequest> Events
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
