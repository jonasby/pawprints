using Microsoft.EntityFrameworkCore;
using PawPrints.Api.Contracts;
using PawPrints.Api.Data;

namespace PawPrints.Api.Analytics;

public sealed class PuppyAnalyticsService(
    PawPrintsDbContext db,
    ILogger<PuppyAnalyticsService> logger
)
{
    private const int NapOutlierMinutes = 150;

    public async Task<PuppyAnalyticsResponse> GetAnalyticsAsync(
        string email,
        CancellationToken cancellationToken
    )
    {
        logger.LogInformation("Puppy analytics load requested for actor email {ActorEmail}.", email);

        var actor = await db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(storedUser => storedUser.Email == email, cancellationToken);

        if (actor is null)
        {
            logger.LogInformation(
                "Puppy analytics load completed with outcome {Outcome} actor email {ActorEmail} days returned {DayCount} event count {EventCount}.",
                "NoProfile",
                email,
                0,
                0
            );
            return new PuppyAnalyticsResponse([]);
        }

        var dataUser = actor;
        string outcome;
        if (actor.CollaboratesWithUserId is long ownerId)
        {
            var owner = await db.Users
                .AsNoTracking()
                .SingleOrDefaultAsync(storedUser => storedUser.Id == ownerId, cancellationToken);
            if (owner is null)
            {
                outcome = "MissingOwnerFallbackSelf";
            }
            else
            {
                dataUser = owner;
                outcome = "SharedFromOwner";
            }
        }
        else
        {
            outcome = "OwnerData";
        }

        var events = await db.Events
            .AsNoTracking()
            .Where(storedEvent => storedEvent.UserId == dataUser.Id)
            .ToListAsync(cancellationToken);
        events = events.OrderBy(storedEvent => storedEvent.OccurredAt).ToList();

        var days = BuildDays(events);
        logger.LogInformation(
            "Puppy analytics load completed with outcome {Outcome} actor email {ActorEmail} data owner email {DataOwnerEmail} days returned {DayCount} event count {EventCount}.",
            outcome,
            email,
            dataUser.Email,
            days.Count,
            events.Count
        );

        return new PuppyAnalyticsResponse(days);
    }

    private static IReadOnlyList<PuppyAnalyticsDayResponse> BuildDays(IReadOnlyList<PuppyEvent> events)
    {
        var metricsByDate = new SortedDictionary<DateOnly, MutableDayMetrics>();
        foreach (var storedEvent in events)
        {
            if (storedEvent.Type is not ("poop" or "pee"))
            {
                continue;
            }

            var metrics = GetMetrics(metricsByDate, storedEvent.DateKey);
            if (storedEvent.Type == "poop")
            {
                metrics.Poops++;
            }
            else
            {
                metrics.Wees++;
            }
        }

        AddSleepDurations(events, metricsByDate);
        AddNapDurations(events, metricsByDate);

        return metricsByDate
            .Where(entry => entry.Value.HasAnyMetric)
            .Select(entry => new PuppyAnalyticsDayResponse(
                entry.Key.ToString("yyyy-MM-dd"),
                entry.Value.Poops,
                entry.Value.Wees,
                entry.Value.SleepMinutes,
                entry.Value.NapMinutes
            ))
            .ToArray();
    }

    private static void AddSleepDurations(
        IReadOnlyList<PuppyEvent> events,
        SortedDictionary<DateOnly, MutableDayMetrics> metricsByDate
    )
    {
        PuppyEvent? openSleep = null;
        foreach (var storedEvent in events.OrderBy(storedEvent => storedEvent.OccurredAt))
        {
            if (storedEvent.Type == "sleep")
            {
                openSleep ??= storedEvent;
                continue;
            }

            if (openSleep is null || storedEvent.Type != "wake")
            {
                continue;
            }

            var duration = GetRoundedMinutes(openSleep.OccurredAt, storedEvent.OccurredAt);
            if (duration > 0)
            {
                GetMetrics(metricsByDate, openSleep.DateKey).SleepMinutes += duration;
            }

            openSleep = null;
        }
    }

    private static void AddNapDurations(
        IReadOnlyList<PuppyEvent> events,
        SortedDictionary<DateOnly, MutableDayMetrics> metricsByDate
    )
    {
        foreach (var dayGroup in events.GroupBy(storedEvent => storedEvent.DateKey))
        {
            PuppyEvent? openNap = null;
            foreach (var storedEvent in dayGroup.OrderBy(storedEvent => storedEvent.OccurredAt))
            {
                if (storedEvent.Type == "nap")
                {
                    openNap = storedEvent;
                    continue;
                }

                if (openNap is null)
                {
                    continue;
                }

                if (storedEvent.Type == "wake")
                {
                    var duration = GetRoundedMinutes(openNap.OccurredAt, storedEvent.OccurredAt);
                    if (duration is > 0 and <= NapOutlierMinutes)
                    {
                        GetMetrics(metricsByDate, dayGroup.Key).NapMinutes += duration;
                    }

                    openNap = null;
                    continue;
                }

                if (storedEvent.Type is "pee" or "poop" or "eat" or "sleep")
                {
                    openNap = null;
                }
            }
        }
    }

    private static int GetRoundedMinutes(DateTimeOffset start, DateTimeOffset end)
    {
        return (int)Math.Round((end - start).TotalMinutes, MidpointRounding.AwayFromZero);
    }

    private static MutableDayMetrics GetMetrics(
        SortedDictionary<DateOnly, MutableDayMetrics> metricsByDate,
        DateOnly dateKey
    )
    {
        if (!metricsByDate.TryGetValue(dateKey, out var metrics))
        {
            metrics = new MutableDayMetrics();
            metricsByDate.Add(dateKey, metrics);
        }

        return metrics;
    }

    private sealed class MutableDayMetrics
    {
        public int Poops { get; set; }
        public int Wees { get; set; }
        public int SleepMinutes { get; set; }
        public int NapMinutes { get; set; }

        public bool HasAnyMetric => Poops > 0 || Wees > 0 || SleepMinutes > 0 || NapMinutes > 0;
    }
}
