namespace PawPrints.Api.Data;

public sealed class PawPrintsInvite
{
    public long Id { get; set; }

    public required string TokenHash { get; set; }

    public long OwnerUserId { get; set; }

    public PawPrintsUser Owner { get; set; } = null!;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public DateTimeOffset? ConsumedAtUtc { get; set; }

    public long? ConsumedByUserId { get; set; }

    public PawPrintsUser? ConsumedBy { get; set; }
}
