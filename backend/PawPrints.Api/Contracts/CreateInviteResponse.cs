namespace PawPrints.Api.Contracts;

public sealed record CreateInviteResponse(string Token, DateTimeOffset ExpiresAt);
