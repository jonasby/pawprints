namespace PawPrints.Api.Data;

public sealed class PuppyPrediction
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public PawPrintsUser User { get; set; } = null!;

    public string Type { get; set; } = "";

    public string Status { get; set; } = "";

    public string? TriggerEventClientId { get; set; }

    public DateTimeOffset PredictedAtUtc { get; set; }

    public DateTimeOffset LastEvaluatedAtUtc { get; set; }

    public DateTimeOffset? ResolvedAtUtc { get; set; }

    public DateTimeOffset WindowStartUtc { get; set; }

    public DateTimeOffset BestGuessAtUtc { get; set; }

    public DateTimeOffset WindowEndUtc { get; set; }

    public decimal Confidence { get; set; }

    public string? ExplanationJson { get; set; }

    public List<NotificationOutboxItem> Notifications { get; set; } = [];
}
