namespace PawPrints.Api.Contracts;

public sealed record PredictionResponse(
    long Id,
    string Type,
    string Status,
    string? TriggerEventClientId,
    DateTimeOffset PredictedAt,
    DateTimeOffset WindowStart,
    DateTimeOffset? BestGuessAt,
    DateTimeOffset WindowEnd,
    decimal Confidence,
    string Explanation,
    DateTimeOffset LastEvaluatedAt
);

public sealed record NotificationResponse(
    long Id,
    long PredictionId,
    string Type,
    string Title,
    string Body,
    DateTimeOffset SendAfter
);
