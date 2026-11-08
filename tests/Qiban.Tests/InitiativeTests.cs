using Qiban.Core;

internal static class InitiativeTests
{
    internal static void Run(Action<bool, string> check)
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var browser = new ActivityObservation("browser", "浏览器");
        var game = new ActivityObservation("game", "游戏");
        var social = new InitiativeProfile(0.5, 2, 0.8, 0.5);
        var quiet = new InitiativeProfile(45, 120, 0.05, 60);
        var shortWait = new InitiativeScheduler(new Random(10));
        var longWait = new InitiativeScheduler(new Random(10));
        shortWait.Reset(start, social); longWait.Reset(start, quiet);
        check(shortWait.TryIdle(start.AddMinutes(3))?.Kind == "idle"
            && longWait.TryIdle(start.AddMinutes(3)) == null && longWait.NextIdleAt > start.AddMinutes(40),
            "initiative cadence respects affectionate versus reserved model profiles");

        var varying = new InitiativeScheduler(new Random(42));
        varying.Reset(start, social);
        var intervals = new HashSet<double>();
        var clock = start;
        for (int i = 0; i < 8; i++)
        {
            intervals.Add((varying.NextIdleAt - clock).TotalSeconds);
            clock = varying.NextIdleAt;
            check(varying.TryIdle(clock, 10) != null, "random idle deadline is eligible without polling drift");
            varying.Completed(clock, true);
        }
        check(intervals.Count == 8 && intervals.All(seconds => seconds is >= 30 and <= 120),
            "successive proactive intervals vary within the character range");

        var scheduler = new InitiativeScheduler(new ConstantRandom(0.1));
        scheduler.Reset(start, social);
        scheduler.UserMessage(start);
        var immediate = scheduler.TryActivity(start, browser);
        check(immediate is { Kind: "activity", IdleMinutes: 0 } && immediate.Activity == browser && immediate.ObservedAt == start,
            "activity can interrupt a fresh conversation without an idle prerequisite");
        check(scheduler.TryActivity(start, game) == null && scheduler.TryIdle(start.AddHours(1)) == null,
            "only one event can be reserved while waiting for an ongoing reply");
        scheduler.Completed(start, true);
        scheduler.UserMessage(start.AddSeconds(1));
        check(scheduler.TryActivity(start.AddSeconds(5), game) == null
            && scheduler.TryActivity(start.AddSeconds(31), game) != null,
            "ordinary user messages preserve proactive cooldown instead of resetting it");
        scheduler.Completed(start.AddSeconds(31), true);

        var misses = new ConstantRandom(0.99);
        scheduler = new InitiativeScheduler(misses);
        scheduler.Reset(start, social);
        int initialCalls = misses.Calls;
        for (int i = 0; i < 15; i++) scheduler.TryActivity(start.AddSeconds(i), browser);
        check(misses.Calls == initialCalls + 1 && scheduler.TryActivity(start.AddSeconds(15), browser) == null
            && misses.Calls == initialCalls + 2,
            "repeated process polling does not reroll a failed probability before its reassessment interval");

        var realProfile = new InitiativeProfile(5, 15, 0.28, 12);
        misses = new ConstantRandom(0.99);
        scheduler = new InitiativeScheduler(misses);
        scheduler.Reset(start, realProfile);
        initialCalls = misses.Calls;
        for (int seconds = 0; seconds < 36; seconds += 2)
            scheduler.TryActivity(start.AddSeconds(seconds), browser);
        check(scheduler.ActivityRetryInterval == TimeSpan.FromSeconds(36) && misses.Calls == initialCalls + 1,
            "the actual 28 percent twelve-minute profile reassesses misses every 36 seconds despite two-second polling");
        check(scheduler.GetActivityEligibleAt(start.AddSeconds(35), browser) == start.AddSeconds(36)
            && scheduler.GetActivityEligibleAt(start.AddSeconds(35), browser) == start.AddSeconds(36)
            && misses.Calls == initialCalls + 1,
            "reading activity eligibility preserves the probability and original deadline");
        scheduler.TryActivity(start.AddSeconds(36), browser);
        check(misses.Calls == initialCalls + 2
            && scheduler.GetActivityEligibleAt(start.AddSeconds(36), browser) == start.AddSeconds(72),
            "a missed opportunity is reevaluated at 36 seconds instead of twelve minutes");
        scheduler.TryActivity(start.AddSeconds(37), game);
        scheduler.TryActivity(start.AddSeconds(38), browser);
        check(misses.Calls == initialCalls + 3
            && scheduler.GetActivityEligibleAt(start.AddSeconds(38), game) == start.AddSeconds(73),
            "different applications receive independent probability decisions without accelerating the previous application");
        scheduler.UserMessage(start.AddSeconds(40));
        int callsAfterInput = misses.Calls;
        scheduler.TryActivity(start.AddSeconds(41), new ActivityObservation(" BROWSER ", "浏览器"));
        check(misses.Calls == callsAfterInput
            && scheduler.GetActivityEligibleAt(start.AddSeconds(41), browser) == start.AddSeconds(72),
            "user input and case changes do not bypass a failed activity probability");
        scheduler.Reset(start.AddSeconds(45), realProfile, preserveActivityAttempts: true);
        int callsAfterReset = misses.Calls;
        scheduler.TryActivity(start.AddSeconds(46), browser);
        check(misses.Calls == callsAfterReset
            && scheduler.GetActivityEligibleAt(start.AddSeconds(46), browser) == start.AddSeconds(72),
            "same-character reset retains the last probability attempt");
        scheduler.UpdateProfile(start.AddSeconds(47), social);
        check(scheduler.GetActivityEligibleAt(start.AddSeconds(47), browser) == start.AddSeconds(51),
            "a new personality profile recalculates reassessment from the previous attempt rather than retaining an old deadline");
        scheduler.Reset(start.AddSeconds(48), realProfile);
        check(scheduler.GetActivityEligibleAt(start.AddSeconds(48), browser) == start.AddSeconds(48),
            "switching to a different character starts with independent activity decisions");

        scheduler = new InitiativeScheduler(new ConstantRandom(0.1));
        scheduler.Reset(start, realProfile);
        scheduler.TryActivity(start, browser);
        scheduler.Completed(start, true);
        scheduler.UserMessage(start.AddSeconds(1));
        check(scheduler.GetActivityEligibleAt(start.AddMinutes(1), game) == start.AddMinutes(12)
            && scheduler.TryActivity(start.AddMinutes(12).AddTicks(-1), game) == null
            && scheduler.TryActivity(start.AddMinutes(12), game) != null,
            "a delivered activity message still applies the full twelve-minute cooldown across applications");
        scheduler.Completed(start.AddMinutes(12), true);

        scheduler.Reset(start, realProfile);
        scheduler.TryIdle(start.AddMinutes(6));
        scheduler.Completed(start.AddMinutes(6), true);
        check(scheduler.GetActivityEligibleAt(start.AddMinutes(6), browser) == start.AddMinutes(6).AddSeconds(30)
            && scheduler.TryActivity(start.AddMinutes(6).AddSeconds(29), browser) == null
            && scheduler.TryActivity(start.AddMinutes(6).AddSeconds(30), browser) != null,
            "an idle message applies only the global thirty-second gap and cannot starve activity conversation");
        scheduler.Completed(start.AddMinutes(6).AddSeconds(30), true);

        scheduler.Reset(start, realProfile);
        scheduler.TryActivity(start, browser);
        scheduler.Completed(start, true);
        scheduler.TryIdle(start.AddMinutes(6));
        scheduler.Completed(start.AddMinutes(6), true);
        check(scheduler.GetActivityEligibleAt(start.AddMinutes(6), game) == start.AddMinutes(12),
            "an intervening idle message does not extend an existing activity cooldown");
        scheduler.Reset(start, realProfile, lastProactiveAt: start.AddSeconds(-5), lastActivityAt: start.AddMinutes(-10));
        check(scheduler.GetActivityEligibleAt(start, browser) == start.AddMinutes(2)
            && scheduler.TryActivity(start.AddSeconds(30), browser) == null
            && scheduler.TryActivity(start.AddMinutes(2), browser) != null,
            "restart restores the activity timestamp separately from more recent idle conversation");
        scheduler.Reset(start, realProfile, lastProactiveAt: start.AddSeconds(-5));
        check(scheduler.GetActivityEligibleAt(start, browser) == start.AddSeconds(25)
            && scheduler.TryActivity(start.AddSeconds(25), browser) != null,
            "restoring an idle-only history does not manufacture a twelve-minute activity cooldown");

        shortWait = new InitiativeScheduler(new ConstantRandom(0.1));
        longWait = new InitiativeScheduler(new ConstantRandom(0.1));
        shortWait.Reset(start, social);
        longWait.Reset(start, quiet);
        check(shortWait.ActivityRetryInterval == TimeSpan.FromSeconds(15)
            && longWait.ActivityRetryInterval == TimeSpan.FromSeconds(120)
            && shortWait.TryActivity(start, browser) != null && longWait.TryActivity(start, browser) == null,
            "reserved characters retain both a lower activity probability and a longer reassessment interval");

        scheduler = new InitiativeScheduler(new ConstantRandom(0.1));
        scheduler.Reset(start, social, unanswered: 3, lastProactiveAt: start.AddMinutes(-5));
        check(scheduler.TryIdle(start.AddHours(5), 3) == null && scheduler.TryActivity(start.AddHours(5), browser, 3) == null,
            "restored unanswered count suppresses both triggers across application restarts");
        scheduler.UserMessage(start.AddHours(5));
        check(scheduler.TryActivity(start.AddHours(5), browser, 3) != null && scheduler.UnansweredCount == 0,
            "a new user message resumes capped proactive conversation immediately");
        scheduler.UserMessage(start.AddHours(5).AddSeconds(1));
        check(scheduler.IsPending, "user input cannot silently discard an in-flight event reservation");
        scheduler.Completed(start.AddHours(5).AddSeconds(2), true);
        check(scheduler.UnansweredCount == 0, "a user response during generation clears the unanswered streak");
        scheduler.Reset(start, social);
        scheduler.TryActivity(start, browser);
        scheduler.UserMessage(start.AddSeconds(1));
        scheduler.DispatchPending();
        scheduler.Completed(start.AddSeconds(2), true);
        check(scheduler.UnansweredCount == 1,
            "queued activity dispatched after a user message counts as a newly unanswered reply");
        scheduler.Reset(start, social, lastProactiveAt: start.AddSeconds(-5));
        check(scheduler.TryActivity(start, browser) == null && scheduler.TryActivity(start.AddSeconds(26), browser) != null,
            "last proactive timestamp restores cooldown without treating restart as user activity");
        scheduler.CancelPending(start.AddSeconds(26));
        check(!scheduler.IsPending && scheduler.UnansweredCount == 0, "abandoned queued activity does not create an unanswered message");

        scheduler.Reset(start, social);
        clock = start;
        var expectedBackoffs = new[] { 2, 4, 8, 16, 30, 30 };
        bool backoffPassed = true;
        foreach (int minutes in expectedBackoffs)
        {
            clock = scheduler.NextIdleAt;
            if (scheduler.TryIdle(clock) == null) { backoffPassed = false; break; }
            scheduler.Completed(clock, false);
            scheduler.UserMessage(clock.AddSeconds(1));
            backoffPassed &= scheduler.UnansweredCount == 0
                && scheduler.NextIdleAt >= clock.AddMinutes(minutes)
                && scheduler.TryActivity(clock.AddMinutes(minutes).AddSeconds(-1), game) == null;
        }
        check(backoffPassed, "model failures back off exponentially up to thirty minutes without counting as messages");
        clock = scheduler.NextIdleAt;
        check(scheduler.TryIdle(clock) != null, "proactive generation can resume once network failure backoff expires");
        scheduler.Completed(clock, true);
        check(scheduler.NextIdleAt <= clock.AddMinutes(2) && scheduler.UnansweredCount == 1,
            "successful recovery restores character cadence and records one delivered message");

        var malformed = new InitiativeProfile(double.NaN, double.PositiveInfinity, -5, double.NegativeInfinity).Normalize();
        var fixedRange = new InitiativeProfile(240, 240, 8, -8).Normalize();
        check(malformed.IdleMinMinutes == 5 && malformed.IdleMaxMinutes == 15
            && malformed.ActivityProbability == 0.01 && malformed.ActivityCooldownMinutes == 5
            && fixedRange.IdleMinMinutes < fixedRange.IdleMaxMinutes && fixedRange.IdleMaxMinutes <= 240
            && fixedRange.ActivityProbability == 0.8 && fixedRange.ActivityCooldownMinutes == 0.5,
            "malformed or fixed model ranges normalize into bounded variable cadence");
        scheduler.Reset(start, social);
        check(scheduler.TryIdle(start.AddYears(1)) != null
            && scheduler.TryActivity(start.AddYears(1), browser) == null,
            "waking after a long suspension reserves one event instead of catch-up bursts");

        scheduler = new InitiativeScheduler(new ConstantRandom(0));
        scheduler.Reset(start, InitiativeProfile.Balanced);
        scheduler.UpdateProfile(start.AddMinutes(2), social);
        check(scheduler.NextIdleAt == start.AddSeconds(30) && scheduler.TryIdle(start.AddMinutes(2))?.IdleMinutes == 2,
            "late character analysis preserves elapsed idle time instead of restarting the wait");
        scheduler.Reset(start, InitiativeProfile.Balanced, unanswered: 2);
        scheduler.TryActivity(start, browser);
        scheduler.UpdateProfile(start.AddSeconds(10), quiet);
        bool reserved = scheduler.IsPending && scheduler.UnansweredCount == 2;
        scheduler.Completed(start.AddSeconds(11), true);
        check(reserved && scheduler.UnansweredCount == 3,
            "character analysis cannot erase queued activity reservations or the unanswered limit");
        scheduler.Reset(start, social);
        scheduler.TryActivity(start, browser);
        scheduler.Completed(start, false);
        scheduler.UpdateProfile(start.AddSeconds(5), social);
        check(scheduler.RetryAfter == start.AddMinutes(2) && scheduler.NextIdleAt >= scheduler.RetryAfter
            && scheduler.TryActivity(start.AddMinutes(1), game) == null,
            "updating personality cadence preserves failed-generation backoff");
        scheduler.Reset(start, InitiativeProfile.Balanced);
        scheduler.UpdateProfile(start.AddMinutes(2), quiet);
        check(scheduler.NextIdleAt == start.AddMinutes(45) && scheduler.TryIdle(start.AddMinutes(10)) == null,
            "reserved characters receive their longer cadence measured from the original idle anchor");
    }

    private sealed class ConstantRandom(double value) : Random
    {
        internal int Calls { get; private set; }
        public override double NextDouble() { Calls++; return value; }
    }
}
