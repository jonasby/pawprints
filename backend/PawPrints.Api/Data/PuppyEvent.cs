namespace PawPrints.Api.Data;

public sealed class PuppyEvent
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public PawPrintsUser User { get; set; } = null!;
    public string ClientEventId { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateOnly DateKey { get; set; }
    public string? MetadataJson { get; set; }
}
