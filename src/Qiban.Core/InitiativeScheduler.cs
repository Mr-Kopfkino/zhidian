namespace Qiban.Core;

/// <summary>Model-derived cadence. Bounds prevent malformed model output from creating request loops.</summary>
public sealed record InitiativeProfile(double IdleMinMinutes = 5, double IdleMaxMinutes = 15,
    double ActivityProbability = 0.25, double ActivityCooldownMinutes = 5)
{
    public static InitiativeProfile Balanced { get; } = new();

    public InitiativeProfile Normalize()
    {
        static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
        double minimum = Math.Clamp(Finite(IdleMinMinutes, 5), 0.5, 200);
        // Even a model that returns identical endpoints must yield varying delays.
        double maximum = Math.Clamp(Finite(IdleMaxMinutes, 15), minimum * 1.2, 240);
        return new(minimum, maximum, Math.Clamp(Finite(ActivityProbability, 0.25), 0.01, 0.8),
            Math.Clamp(Finite(ActivityCooldownMinutes, 5), 0.5, 120));
    }
}

/// <summary>Only an executable name and an application category; never a title, URL or document.</summary>
public sealed record ActivityObservation(string ProcessName, string Category);
public sealed record InitiativeTrigger(string Kind, ActivityObservation? Activity, double IdleMinutes,
    DateTimeOffset? ObservedAt = null);

/// <summary>
/// In-memory, single-threaded scheduling policy. Call from the desktop dispatcher.
/// Trigger reservation is separate from completion so a busy conversation can queue one activity event.
/// </summary>
public sealed class InitiativeScheduler(Random? random = null)
{
    private readonly Random random = random ?? Random.Shared;
    private readonly Dictionary<string, DateTimeOffset> activityAttempts = new(StringComparer.OrdinalIgnoreCase);
    private InitiativeProfile profile = InitiativeProfile.Balanced;
    private DateTimeOffset lastUserMessage;
    private DateTimeOffset idleAnchor;
    private DateTimeOffset activityAllowedAt;
    private DateTimeOffset retryAfter;
    private long userGeneration;
    private long reservedGeneration;
    private string? reservedKind;
    private int failureCount;

    public DateTimeOffset NextIdleAt { get; private set; }
    public int UnansweredCount { get; private set; }
    public bool IsPending { get; private set; }
    public DateTimeOffset RetryAfter => retryAfter;
    public TimeSpan ActivityRetryInterval => TimeSpan.FromSeconds(Math.Clamp(profile.ActivityCooldownMinutes * 3, 15, 120));

    /// <summary>
    /// Earliest time to evaluate this application again, excluding pending work and the unanswered limit.
    /// This read does not draw a probability or consume an opportunity.
    /// </summary>
    public DateTimeOffset GetActivityEligibleAt(DateTimeOffset now, ActivityObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!ValidActivity(observation)) return DateTimeOffset.MaxValue;
        var eligibleAt = now;
        if (eligibleAt < activityAllowedAt) eligibleAt = activityAllowedAt;
        if (eligibleAt < retryAfter) eligibleAt = retryAfter;
        if (activityAttempts.TryGetValue(observation.ProcessName.Trim(), out var lastAttempt)
            && eligibleAt < lastAttempt + ActivityRetryInterval)
            eligibleAt = lastAttempt + ActivityRetryInterval;
        return eligibleAt;
    }

    /// <summary>Refine the cadence without discarding elapsed idle time, reservations or backoff.</summary>
    public void UpdateProfile(DateTimeOffset now, InitiativeProfile profile)
    {
        this.profile = profile.Normalize();
        ScheduleIdle(idleAnchor > now ? now : idleAnchor);
    }

    public void Reset(DateTimeOffset now, InitiativeProfile profile, int unanswered = 0,
        DateTimeOffset? lastProactiveAt = null, DateTimeOffset? lastActivityAt = null,
        bool preserveActivityAttempts = false)
    {
        this.profile = profile.Normalize();
        UnansweredCount = Math.Max(0, unanswered);
        IsPending = false;
        failureCount = 0;
        userGeneration = 0;
        reservedKind = null;
        if (!preserveActivityAttempts) activityAttempts.Clear();
        lastUserMessage = now;
        // Future persisted timestamps cannot defer all conversation forever after a clock correction.
        var previous = lastProactiveAt is { } value && value > now ? now : lastProactiveAt;
        var previousActivity = lastActivityAt is { } activity && activity > now ? now : lastActivityAt;
        activityAllowedAt = previousActivity?.AddMinutes(this.profile.ActivityCooldownMinutes) ?? now;
        retryAfter = previous?.AddSeconds(30) ?? now;
        ScheduleIdle(now);
    }

    public void UserMessage(DateTimeOffset now)
    {
        lastUserMessage = now;
        userGeneration++;
        UnansweredCount = 0;
        // Do not clear reservations, failure backoff, or activity cooldown when a user speaks.
        ScheduleIdle(now);
    }

    public InitiativeTrigger? TryIdle(DateTimeOffset now, int maxUnanswered = 3)
    {
        if (!CanReserve(now, maxUnanswered) || now < NextIdleAt) return null;
        return Reserve(now, "idle", null);
    }

    public InitiativeTrigger? TryActivity(DateTimeOffset now, ActivityObservation observation, int maxUnanswered = 3)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!ValidActivity(observation) || !CanReserve(now, maxUnanswered)
            || now < GetActivityEligibleAt(now, observation)) return null;
        string key = observation.ProcessName.Trim();
        // A miss has a shorter reassessment interval than a delivered message's cooldown.
        // Store the observation time so profile updates can adjust that interval without rerolling on every poll.
        if (!activityAttempts.ContainsKey(key) && activityAttempts.Count >= 64)
            activityAttempts.Remove(activityAttempts.MinBy(pair => pair.Value).Key);
        activityAttempts[key] = now;
        if (random.NextDouble() >= profile.ActivityProbability) return null;
        return Reserve(now, "activity", observation);
    }

    public void Completed(DateTimeOffset now, bool success)
    {
        if (!IsPending) return;
        IsPending = false;
        if (success)
        {
            failureCount = 0;
            if (reservedGeneration == userGeneration) UnansweredCount++;
            if (reservedKind == "activity") activityAllowedAt = now.AddMinutes(profile.ActivityCooldownMinutes);
            retryAfter = now.AddSeconds(30);
        }
        else
        {
            failureCount = Math.Min(failureCount + 1, 5);
            retryAfter = now.AddMinutes(Math.Min(30, Math.Pow(2, failureCount)));
            if (activityAllowedAt < retryAfter) activityAllowedAt = retryAfter;
        }
        ScheduleIdle(now);
    }

    /// <summary>Start the reserved generation using the most recent user turn as its unanswered baseline.</summary>
    public void DispatchPending()
    {
        if (IsPending) reservedGeneration = userGeneration;
    }

    /// <summary>Release a stale queued event without recording a failed model call.</summary>
    public void CancelPending(DateTimeOffset now)
    {
        if (!IsPending) return;
        IsPending = false;
        if (retryAfter < now.AddSeconds(30)) retryAfter = now.AddSeconds(30);
        ScheduleIdle(now);
    }

    public void Abandon(DateTimeOffset now) => CancelPending(now);

    private bool CanReserve(DateTimeOffset now, int maximum) => !IsPending && now >= retryAfter
        && maximum > 0 && UnansweredCount < maximum;

    private static bool ValidActivity(ActivityObservation observation) =>
        !string.IsNullOrWhiteSpace(observation.ProcessName) && observation.ProcessName.Length <= 128;

    private InitiativeTrigger Reserve(DateTimeOffset now, string kind, ActivityObservation? observation)
    {
        IsPending = true;
        reservedGeneration = userGeneration;
        reservedKind = kind;
        return new(kind, observation, Math.Max(0, (now - lastUserMessage).TotalMinutes),
            observation == null ? null : now);
    }

    private void ScheduleIdle(DateTimeOffset now)
    {
        idleAnchor = now;
        double minutes = profile.IdleMinMinutes + random.NextDouble() * (profile.IdleMaxMinutes - profile.IdleMinMinutes);
        NextIdleAt = now.AddMinutes(minutes);
        if (NextIdleAt < retryAfter) NextIdleAt = retryAfter;
    }
}
