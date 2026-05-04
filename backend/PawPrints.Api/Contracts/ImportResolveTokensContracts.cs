namespace PawPrints.Api.Contracts;

public sealed record KnownEventTypeDto(string Id, string Label);

public sealed record ImportResolveTokensRequest(
    IReadOnlyCollection<string> Tokens,
    IReadOnlyCollection<KnownEventTypeDto>? KnownTypes = null
);

public sealed record ImportTokenMatchDto(
    string Token,
    string TypeId,
    bool IsNew,
    string? Label = null,
    string? Emoji = null
);

public sealed record ImportResolveTokensResponse(bool AiAvailable, IReadOnlyCollection<ImportTokenMatchDto> Matches);
