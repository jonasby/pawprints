namespace PawPrints.Api.Contracts;

public sealed record PuppyAnalyticsResponse(IReadOnlyList<PuppyAnalyticsDayResponse> Days);

public sealed record PuppyAnalyticsDayResponse(
    string DateKey,
    int Poops,
    int Wees,
    int? SleepMinutes,
    int? NapMinutes
);
