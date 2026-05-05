using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PawPrints.Api.Data;

namespace PawPrints.Api.Predictions;

public sealed class PredictionEvaluator(
    PawPrintsDbContext db,
    TimeProvider timeProvider,
    ILogger<PredictionEvaluator> logger
)
{
    private const int MinimumHistorySamples = 2;
    private static readonly TimeSpan NotificationLeadTime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RefreshLookBack = TimeSpan.FromHours(4);

    public async Task EvaluateForUserAsync(long userId, CancellationToken cancellationToken)
    {
        var user = await db.Users
            .Include(storedUser => storedUser.Events)
            .Include(storedUser => storedUser.Predictions)
            .Include(storedUser => storedUser.NotificationOutboxItems)
            .SingleOrDefaultAsync(storedUser => storedUser.Id == userId, cancellationToken);

        if (user is null)
        {
            logger.LogInformation(
                "Prediction evaluation skipped with outcome {Outcome} owner user id {OwnerUserId}",
                "MissingUser",
                userId
            );
            return;
        }

        await EvaluateLoadedUserAsync(user, cancellationToken);
    }

    public async Task EvaluateForActorEmailAsync(string email, CancellationToken cancellationToken)
    {
        var actor = await db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(storedUser => storedUser.Email == email, cancellationToken);
        if (actor is null)
        {
            logger.LogInformation(
                "Prediction evaluation skipped with outcome {Outcome} email {Email}",
                "NoProfile",
                email
            );
            return;
        }

        await EvaluateForUserAsync(actor.CollaboratesWithUserId ?? actor.Id, cancellationToken);
    }

    public async Task EvaluateRecentlyActiveUsersAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var cutoff = now.Subtract(RefreshLookBack);
        var userIds = await db.Users
            .Where(storedUser =>
                storedUser.Events.Any(storedEvent => storedEvent.OccurredAt >= cutoff)
                || storedUser.Predictions.Any(storedPrediction => storedPrediction.Status == PredictionConstants.StatusActive))
            .Select(storedUser => storedUser.Id)
            .ToArrayAsync(cancellationToken);

        logger.LogInformation(
            "Scheduled prediction evaluation started for {UserCount} recently active users at {EvaluatedAt}",
            userIds.Length,
            now
        );

        foreach (var userId in userIds)
        {
            await EvaluateForUserAsync(userId, cancellationToken);
        }
    }

    private async Task EvaluateLoadedUserAsync(PawPrintsUser user, CancellationToken cancellationToken)
    {
        var events = user.Events
            .OrderBy(storedEvent => storedEvent.OccurredAt)
            .ThenBy(storedEvent => storedEvent.Id)
            .ToArray();
        var now = timeProvider.GetUtcNow();

        logger.LogInformation(
            "Prediction evaluation started for owner user id {OwnerUserId} with event count {EventCount} at {EvaluatedAt}",
            user.Id,
            events.Length,
            now
        );

        ResolveInvalidActivePredictions(user, events, now);

        var activePredictionsBefore = user.Predictions.Count(prediction => prediction.Status == PredictionConstants.StatusActive);
        EvaluateNapWakePrediction(user, events, now);
        EvaluatePoopNeedPrediction(user, events, now);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Prediction evaluation completed with outcome {Outcome} owner user id {OwnerUserId} active prediction count {ActivePredictionCount} notification pending count {PendingNotificationCount}",
            user.Predictions.Count(prediction => prediction.Status == PredictionConstants.StatusActive) != activePredictionsBefore
                ? "ActivePredictionCountChanged"
                : "PredictionsEvaluated",
            user.Id,
            user.Predictions.Count(prediction => prediction.Status == PredictionConstants.StatusActive),
            user.NotificationOutboxItems.Count(notification => notification.SentAtUtc == null && notification.CancelledAtUtc == null)
        );
    }

    private void EvaluateNapWakePrediction(
        PawPrintsUser user,
        IReadOnlyList<PuppyEvent> events,
        DateTimeOffset now
    )
    {
        var latestNap = events.LastOrDefault(storedEvent => storedEvent.Type == "nap");
        if (latestNap is null)
        {
            return;
        }

        var wakeAfterLatestNap = events.FirstOrDefault(storedEvent =>
            storedEvent.Type == "wake"
            && storedEvent.OccurredAt > latestNap.OccurredAt);
        if (wakeAfterLatestNap is not null)
        {
            ResolvePredictions(
                user,
                PredictionConstants.NapWakeType,
                latestNap.ClientEventId,
                wakeAfterLatestNap.OccurredAt,
                now
            );
            return;
        }

        var napDurations = GetCompletedNapDurations(events, latestNap.OccurredAt).ToArray();
        if (napDurations.Length < MinimumHistorySamples)
        {
            CancelPredictionsByType(user, PredictionConstants.NapWakeType, now, "not-enough-history");
            return;
        }

        var medianDuration = MedianTimeSpan(napDurations);
        var minDuration = napDurations.Min();
        var maxDuration = napDurations.Max();
        var windowStart = latestNap.OccurredAt.Add(minDuration);
        var bestGuess = latestNap.OccurredAt.Add(medianDuration);
        var windowEnd = latestNap.OccurredAt.Add(maxDuration);
        var confidence = Math.Min(0.9m, 0.55m + Math.Min(napDurations.Length, 7) * 0.05m);

        var prediction = UpsertPrediction(
            user,
            PredictionConstants.NapWakeType,
            latestNap.ClientEventId,
            windowStart,
            bestGuess,
            windowEnd,
            confidence,
            JsonSerializer.Serialize(new
            {
                method = "recency-weighted historical nap durations",
                sampleCount = napDurations.Length,
                durationMinutes = napDurations.Select(duration => Math.Round(duration.TotalMinutes)).ToArray(),
            }),
            now
        );

        EnsureNotification(
            user,
            prediction,
            windowStart.Subtract(NotificationLeadTime),
            "Nap wake likely soon",
            $"Wake likely around {FormatLocalishTime(bestGuess)}; usual window {FormatLocalishTime(windowStart)}-{FormatLocalishTime(windowEnd)}.",
            now
        );
    }

    private void EvaluatePoopNeedPrediction(
        PawPrintsUser user,
        IReadOnlyList<PuppyEvent> events,
        DateTimeOffset now
    )
    {
        var relevantEvents = events.Where(storedEvent => storedEvent.OccurredAt <= now).ToArray();
        var latestPoop = relevantEvents.LastOrDefault(storedEvent => storedEvent.Type == "poop");
        var latestEat = relevantEvents.LastOrDefault(storedEvent => storedEvent.Type == "eat");
        var latestWake = relevantEvents.LastOrDefault(storedEvent => storedEvent.Type == "wake");

        if (latestEat is null || latestWake is null)
        {
            CancelPredictionsByType(user, PredictionConstants.PoopNeedType, now, "missing-food-or-wake");
            return;
        }

        var triggerAt = latestEat.OccurredAt >= latestWake.OccurredAt
            ? latestEat.OccurredAt
            : latestWake.OccurredAt;
        var triggerEventClientId = latestEat.OccurredAt >= latestWake.OccurredAt
            ? latestEat.ClientEventId
            : latestWake.ClientEventId;

        if (latestPoop is not null && latestPoop.OccurredAt > triggerAt)
        {
            ResolvePredictions(user, PredictionConstants.PoopNeedType, null, latestPoop.OccurredAt, now);
            return;
        }

        var samples = GetPoopAfterFoodSamples(events, triggerAt).ToArray();
        if (samples.Length < MinimumHistorySamples)
        {
            CancelPredictionsByType(user, PredictionConstants.PoopNeedType, now, "not-enough-history");
            return;
        }

        var offsets = samples.Select(sample => sample.PoopAt - sample.TriggerAt).ToArray();
        var medianOffset = MedianTimeSpan(offsets);
        var minOffset = offsets.Min();
        var maxOffset = offsets.Max();
        var windowStart = triggerAt.Add(minOffset);
        var bestGuess = triggerAt.Add(medianOffset);
        var windowEnd = triggerAt.Add(maxOffset);

        if (now > windowEnd.AddMinutes(30))
        {
            CancelPredictionsByType(user, PredictionConstants.PoopNeedType, now, "window-expired");
            return;
        }

        var minutesUntilWindowEnd = Math.Max(0, (decimal)(windowEnd - now).TotalMinutes);
        var confidence = Math.Min(0.9m, 0.55m + Math.Min(samples.Length, 6) * 0.05m);
        if (now >= windowStart && minutesUntilWindowEnd <= 30)
        {
            confidence = Math.Min(0.95m, confidence + 0.1m);
        }

        var prediction = UpsertPrediction(
            user,
            PredictionConstants.PoopNeedType,
            triggerEventClientId,
            windowStart,
            bestGuess,
            windowEnd,
            confidence,
            JsonSerializer.Serialize(new
            {
                method = "rolling poo-after-food time-to-event window",
                sampleCount = samples.Length,
                offsetMinutes = offsets.Select(offset => Math.Round(offset.TotalMinutes)).ToArray(),
                latestFoodAt = latestEat.OccurredAt,
                latestWakeAt = latestWake.OccurredAt,
            }),
            now
        );

        EnsureNotification(
            user,
            prediction,
            windowStart,
            "Poo likely soon",
            $"Poo likely in the {FormatLocalishTime(windowStart)}-{FormatLocalishTime(windowEnd)} window.",
            now
        );
    }

    private PuppyPrediction UpsertPrediction(
        PawPrintsUser user,
        string type,
        string? triggerEventClientId,
        DateTimeOffset windowStart,
        DateTimeOffset bestGuess,
        DateTimeOffset windowEnd,
        decimal confidence,
        string explanationJson,
        DateTimeOffset now
    )
    {
        foreach (var stale in user.Predictions.Where(prediction =>
                     prediction.Type == type
                     && prediction.Status == PredictionConstants.StatusActive
                     && prediction.TriggerEventClientId != triggerEventClientId))
        {
            stale.Status = PredictionConstants.StatusCancelled;
            stale.ResolvedAtUtc = now;
            stale.LastEvaluatedAtUtc = now;
            CancelNotificationsForPrediction(user, stale, now);
        }

        var prediction = user.Predictions.SingleOrDefault(candidate =>
            candidate.Type == type
            && candidate.TriggerEventClientId == triggerEventClientId
            && candidate.Status == PredictionConstants.StatusActive);

        if (prediction is null)
        {
            prediction = new PuppyPrediction
            {
                UserId = user.Id,
                Type = type,
                TriggerEventClientId = triggerEventClientId,
                Status = PredictionConstants.StatusActive,
                PredictedAtUtc = now,
            };
            user.Predictions.Add(prediction);
        }

        prediction.WindowStartUtc = windowStart;
        prediction.BestGuessAtUtc = bestGuess;
        prediction.WindowEndUtc = windowEnd;
        prediction.Confidence = confidence;
        prediction.ExplanationJson = explanationJson;
        prediction.LastEvaluatedAtUtc = now;

        return prediction;
    }

    private static IEnumerable<TimeSpan> GetCompletedNapDurations(
        IReadOnlyList<PuppyEvent> events,
        DateTimeOffset before
    )
    {
        var naps = events
            .Where(storedEvent => storedEvent.Type == "nap" && storedEvent.OccurredAt < before)
            .OrderByDescending(storedEvent => storedEvent.OccurredAt)
            .Take(7)
            .OrderBy(storedEvent => storedEvent.OccurredAt)
            .ToArray();

        foreach (var nap in naps)
        {
            var wake = events.FirstOrDefault(storedEvent =>
                storedEvent.Type == "wake"
                && storedEvent.OccurredAt > nap.OccurredAt
                && storedEvent.OccurredAt < before);
            if (wake is null)
            {
                continue;
            }

            var duration = wake.OccurredAt - nap.OccurredAt;
            if (duration > TimeSpan.FromMinutes(10) && duration < TimeSpan.FromHours(4))
            {
                yield return duration;
            }
        }
    }

    private static IEnumerable<PoopSample> GetPoopAfterFoodSamples(
        IReadOnlyList<PuppyEvent> events,
        DateTimeOffset before
    )
    {
        var foodEvents = events
            .Where(storedEvent => storedEvent.Type == "eat" && storedEvent.OccurredAt < before)
            .OrderByDescending(storedEvent => storedEvent.OccurredAt)
            .Take(10)
            .OrderBy(storedEvent => storedEvent.OccurredAt)
            .ToArray();

        foreach (var food in foodEvents)
        {
            var poop = events.FirstOrDefault(storedEvent =>
                storedEvent.Type == "poop"
                && storedEvent.OccurredAt > food.OccurredAt
                && storedEvent.OccurredAt < before);
            if (poop is null)
            {
                continue;
            }

            var offset = poop.OccurredAt - food.OccurredAt;
            if (offset >= TimeSpan.FromMinutes(5) && offset <= TimeSpan.FromHours(3))
            {
                yield return new PoopSample(food.OccurredAt, poop.OccurredAt);
            }
        }
    }

    private static TimeSpan MedianTimeSpan(IReadOnlyCollection<TimeSpan> values)
    {
        var orderedTicks = values.Select(value => value.Ticks).Order().ToArray();
        var midpoint = orderedTicks.Length / 2;
        if (orderedTicks.Length % 2 == 1)
        {
            return TimeSpan.FromTicks(orderedTicks[midpoint]);
        }

        return TimeSpan.FromTicks((orderedTicks[midpoint - 1] + orderedTicks[midpoint]) / 2);
    }

    private static void ResolveInvalidActivePredictions(
        PawPrintsUser user,
        IReadOnlyList<PuppyEvent> events,
        DateTimeOffset now
    )
    {
        foreach (var prediction in user.Predictions.Where(prediction =>
                     prediction.Status == PredictionConstants.StatusActive).ToArray())
        {
            if (prediction.Type == PredictionConstants.NapWakeType)
            {
                var nap = events.SingleOrDefault(storedEvent => storedEvent.ClientEventId == prediction.TriggerEventClientId);
                var wake = nap is null
                    ? null
                    : events.FirstOrDefault(storedEvent =>
                        storedEvent.Type == "wake"
                        && storedEvent.OccurredAt > nap.OccurredAt);
                if (nap is null)
                {
                    prediction.Status = PredictionConstants.StatusCancelled;
                    prediction.ResolvedAtUtc = now;
                    CancelNotificationsForPrediction(user, prediction, now);
                }
                else if (wake is not null)
                {
                    prediction.Status = PredictionConstants.StatusResolved;
                    prediction.ResolvedAtUtc = wake.OccurredAt;
                    CancelNotificationsForPrediction(user, prediction, now);
                }
            }

            if (prediction.Type == PredictionConstants.PoopNeedType)
            {
                var trigger = prediction.TriggerEventClientId is null
                    ? null
                    : events.SingleOrDefault(storedEvent => storedEvent.ClientEventId == prediction.TriggerEventClientId);
                var poop = trigger is null
                    ? null
                    : events.FirstOrDefault(storedEvent =>
                        storedEvent.Type == "poop"
                        && storedEvent.OccurredAt > trigger.OccurredAt);
                if (trigger is null || prediction.WindowEndUtc.AddMinutes(30) < now)
                {
                    prediction.Status = PredictionConstants.StatusCancelled;
                    prediction.ResolvedAtUtc = now;
                    CancelNotificationsForPrediction(user, prediction, now);
                }
                else if (poop is not null)
                {
                    prediction.Status = PredictionConstants.StatusResolved;
                    prediction.ResolvedAtUtc = poop.OccurredAt;
                    CancelNotificationsForPrediction(user, prediction, now);
                }
            }
        }
    }

    private static void ResolvePredictions(
        PawPrintsUser user,
        string type,
        string? triggerEventClientId,
        DateTimeOffset resolvedAt,
        DateTimeOffset now
    )
    {
        foreach (var prediction in user.Predictions.Where(prediction =>
                     prediction.Type == type
                     && prediction.Status == PredictionConstants.StatusActive
                     && (triggerEventClientId is null || prediction.TriggerEventClientId == triggerEventClientId)))
        {
            prediction.Status = PredictionConstants.StatusResolved;
            prediction.ResolvedAtUtc = resolvedAt;
            prediction.LastEvaluatedAtUtc = now;
            CancelNotificationsForPrediction(user, prediction, now);
        }
    }

    private static void CancelPredictionsByType(
        PawPrintsUser user,
        string type,
        DateTimeOffset now,
        string reason
    )
    {
        foreach (var prediction in user.Predictions.Where(prediction =>
                     prediction.Type == type
                     && prediction.Status == PredictionConstants.StatusActive))
        {
            prediction.Status = PredictionConstants.StatusCancelled;
            prediction.ResolvedAtUtc = now;
            prediction.LastEvaluatedAtUtc = now;
            prediction.ExplanationJson = JsonSerializer.Serialize(new { reason });
            CancelNotificationsForPrediction(user, prediction, now);
        }
    }

    private static void EnsureNotification(
        PawPrintsUser user,
        PuppyPrediction prediction,
        DateTimeOffset sendAfter,
        string title,
        string body,
        DateTimeOffset now
    )
    {
        var notification = user.NotificationOutboxItems.SingleOrDefault(candidate =>
            (
                candidate.Prediction == prediction
                || (prediction.Id > 0 && candidate.PredictionId == prediction.Id)
            )
            && candidate.Type == prediction.Type);

        if (notification is null)
        {
            notification = new NotificationOutboxItem
            {
                UserId = user.Id,
                Prediction = prediction,
                Type = prediction.Type,
                CreatedAtUtc = now,
            };
            user.NotificationOutboxItems.Add(notification);
        }

        notification.SendAfterUtc = sendAfter;
        notification.Title = title;
        notification.Body = body;
        notification.PayloadJson = JsonSerializer.Serialize(new
        {
            predictionType = prediction.Type,
            windowStart = prediction.WindowStartUtc,
            bestGuessAt = prediction.BestGuessAtUtc,
            windowEnd = prediction.WindowEndUtc,
            confidence = prediction.Confidence,
        });
        notification.CancelledAtUtc = null;
    }

    private static void CancelNotificationsForPrediction(
        PawPrintsUser user,
        PuppyPrediction prediction,
        DateTimeOffset now
    )
    {
        foreach (var notification in user.NotificationOutboxItems.Where(notification =>
                     notification.Prediction == prediction
                     && notification.SentAtUtc == null
                     && notification.CancelledAtUtc == null))
        {
            notification.CancelledAtUtc = now;
        }
    }

    private static string FormatLocalishTime(DateTimeOffset value)
    {
        return value.ToString("HH:mm");
    }

    private sealed record PoopSample(DateTimeOffset TriggerAt, DateTimeOffset PoopAt);
}
