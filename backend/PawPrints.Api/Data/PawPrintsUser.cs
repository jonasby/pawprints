namespace PawPrints.Api.Data;

public sealed class PawPrintsUser
{
    public long Id { get; set; }

    public required string Email { get; set; }

    public required string ExternalSubject { get; set; }

    public DateOnly ArrivalDate { get; set; }

    public DateOnly BirthDate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public long? CollaboratesWithUserId { get; set; }

    public PawPrintsUser? CollaboratesWith { get; set; }

    public List<PuppyEvent> Events { get; set; } = [];

    public List<PuppyPrediction> Predictions { get; set; } = [];

    public List<NotificationOutboxItem> NotificationOutboxItems { get; set; } = [];

    public List<PawPrintsUser> Collaborators { get; set; } = [];
}
