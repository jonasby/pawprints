namespace PawPrints.Api.Contracts;

public sealed record MeResponse(string Email, CollaborationInfo Collaboration);

public sealed record CollaborationInfo(string Role, string? OwnerEmail);
