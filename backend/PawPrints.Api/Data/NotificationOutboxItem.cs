namespace PawPrints.Api.Data;

public sealed class NotificationOutboxItem
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public PawPrintsUser User { get; set; } = null!;
    public long PredictionId { get; set; }
    public PuppyPrediction Prediction { get; set; } = null!;
    public string Type { get; set; } = "";
    public DateTimeOffset SendAfterUtc { get; set; }
    public DateTimeOffset? SentAtUtc { get; set; }
    public DateTimeOffset? CancelledAtUtc { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAtUtc { get; set; }
}
