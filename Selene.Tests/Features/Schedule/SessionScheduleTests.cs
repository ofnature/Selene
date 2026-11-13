using Selene.Features.Schedule;

namespace Selene.Tests.Features.Schedule;

public class SessionScheduleTests
{
    private const ulong Mene = 0x0040_0000_1234_5678UL;
    private const ulong Pandia = 0x0040_0000_8765_4321UL;

    private static readonly DateOnly FirstDay = new(2026, 11, 12);

    /// <summary>The user's answers of 2026-11-12: evenings 17:00-01:00, 2-4 h bursts, 30-90 min breaks.</summary>
    private static readonly ScheduleSettings Defaults = new();

    /// <summary>Many characters over many days: what must hold for every plan, not for a lucky one.</summary>
    private static IEnumerable<DayPlan> ManyPlans(ScheduleSettings settings)
    {
        for (var character = 0UL; character < 100; character++)
        {
            for (var day = 0; day < 60; day++)
                yield return SessionSchedule.PlanDay(settings, Mene + (character * 7919), FirstDay.AddDays(day));
        }
    }

    /// <summary>A plugin reload mid-evening must not deal a new evening: nothing is stored anywhere.</summary>
    [Fact]
    public void TheSameCharacterAndDay_AlwaysGetTheSamePlan()
    {
        var first = SessionSchedule.PlanDay(Defaults, Mene, FirstDay);
        var again = SessionSchedule.PlanDay(Defaults, Mene, FirstDay);

        Assert.Equal(first.Bursts, again.Bursts);
        Assert.Equal(first.Opens, again.Opens);
        Assert.Equal(first.Closes, again.Closes);
    }

    [Fact]
    public void TheDefaultPlayHours_AreTheEvening_PastMidnight()
    {
        var plan = SessionSchedule.PlanDay(Defaults, Mene, FirstDay);

        Assert.Equal(new DateTime(2026, 11, 12, 17, 0, 0), plan.Opens);
        Assert.Equal(new DateTime(2026, 11, 13, 1, 0, 0), plan.Closes);
    }

    [Fact]
    public void EveryBurst_LiesInsideThePlayHours_InOrder_WithARealBreakBetween()
    {
        foreach (var plan in ManyPlans(Defaults))
        {
            Assert.InRange(plan.Bursts[0].Start, plan.Opens, plan.Opens.AddMinutes(Defaults.StartDelay.Max));
            Assert.InRange(plan.Bursts[^1].End, plan.Opens, plan.Closes);

            for (var i = 0; i < plan.Bursts.Count; i++)
            {
                var length = plan.Bursts[i].End - plan.Bursts[i].Start;
                Assert.InRange(length, SessionSchedule.ShortestCutBurst, TimeSpan.FromMinutes(Defaults.Burst.Max));

                // Only the day's last burst may be cut short - by the end of the evening.
                if (i < plan.Bursts.Count - 1)
                {
                    Assert.True(length >= TimeSpan.FromMinutes(Defaults.Burst.Min));

                    var rest = plan.Bursts[i + 1].Start - plan.Bursts[i].End;
                    Assert.InRange(rest, TimeSpan.FromMinutes(Defaults.Break.Min), TimeSpan.FromMinutes(Defaults.Break.Max));
                }
            }
        }
    }

    /// <summary>An eight-hour evening holds two or three bursts - "do 4 hours, wait an hour".</summary>
    [Fact]
    public void AnEvening_HasTwoOrThreeBursts()
    {
        foreach (var plan in ManyPlans(Defaults))
            Assert.InRange(plan.Bursts.Count, 2, 3);
    }

    /// <summary>The schedule itself must not be a pattern.</summary>
    [Fact]
    public void OneCharactersEvenings_DoNotStartAtTheSameTimeTwice()
    {
        var starts = Enumerable.Range(0, 30)
            .Select(day => SessionSchedule.PlanDay(Defaults, Mene, FirstDay.AddDays(day)).Bursts[0].Start.TimeOfDay)
            .ToList();

        Assert.True(starts.Distinct().Count() >= 28, "the first burst started at the same second on several days");
        Assert.True(starts.Max() - starts.Min() > TimeSpan.FromMinutes(20), "the start barely moves from day to day");
    }

    /// <summary>Multibox: two characters must not start and stop in step.</summary>
    [Fact]
    public void TwoCharacters_NeverShareAnEvening()
    {
        for (var day = 0; day < 60; day++)
        {
            var one = SessionSchedule.PlanDay(Defaults, Mene, FirstDay.AddDays(day));
            var other = SessionSchedule.PlanDay(Defaults, Pandia, FirstDay.AddDays(day));

            Assert.NotEqual(one.Bursts[0].Start, other.Bursts[0].Start);
            Assert.NotEqual(one.Bursts[0].End, other.Bursts[0].End);
        }
    }

    /// <summary>Content ids one apart (two characters of one account) must not get neighbouring evenings.</summary>
    [Fact]
    public void NeighbouringCharactersAndDays_ShareNothing()
    {
        var plan = SessionSchedule.PlanDay(Defaults, Mene, FirstDay);
        var nextCharacter = SessionSchedule.PlanDay(Defaults, Mene + 1, FirstDay);
        var nextDay = SessionSchedule.PlanDay(Defaults, Mene, FirstDay.AddDays(1));

        Assert.NotEqual(plan.Bursts[0].Start, nextCharacter.Bursts[0].Start);
        Assert.NotEqual(plan.Bursts[0].Start.TimeOfDay, nextDay.Bursts[0].Start.TimeOfDay);
        Assert.NotEqual(plan.Bursts[0].End - plan.Bursts[0].Start, nextDay.Bursts[0].End - nextDay.Bursts[0].Start);
    }

    /// <summary>With the whole day to play in, the daily cap is what ends the work.</summary>
    [Fact]
    public void TheDailyCap_EndsTheWork_WhenThePlayHoursDoNot()
    {
        var allDay = new ScheduleSettings { WindowStartMinute = 9 * 60, WindowEndMinute = 9 * 60 };

        foreach (var plan in ManyPlans(allDay))
        {
            Assert.Equal(TimeSpan.FromHours(24), plan.Closes - plan.Opens);

            var worked = plan.Bursts.Aggregate(TimeSpan.Zero, (sum, burst) => sum + (burst.End - burst.Start));
            Assert.InRange(
                worked,
                TimeSpan.FromMinutes(allDay.DailyCap.Min) - SessionSchedule.ShortestCutBurst,
                TimeSpan.FromMinutes(allDay.DailyCap.Max));
        }
    }

    /// <summary>The 30-minute floor is for a burst that was CUT; short bursts the user asked for are kept.</summary>
    [Fact]
    public void BurstsTheUserSetShort_AreKept()
    {
        var shortBursts = new ScheduleSettings { Burst = new MinuteRange(10, 20), Break = new MinuteRange(5, 10) };

        var plan = SessionSchedule.PlanDay(shortBursts, Mene, FirstDay);

        Assert.True(plan.Bursts.Count > 10);
        Assert.All(plan.Bursts.Take(plan.Bursts.Count - 1), burst =>
            Assert.InRange(burst.End - burst.Start, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20)));
    }

    [Fact]
    public void InsideABurst_ItIsWork_UntilTheBurstEnds()
    {
        var burst = SessionSchedule.PlanDay(Defaults, Mene, FirstDay).Bursts[0];

        var decision = SessionSchedule.Decide(Defaults, Mene, burst.Start.AddMinutes(1));

        Assert.Equal(SchedulePhase.Work, decision.Phase);
        Assert.Equal(burst.End, decision.Until);
        Assert.StartsWith("Working until ", decision.Status);
    }

    [Fact]
    public void BetweenTwoBursts_ItIsABreak_UntilTheNextOneStarts()
    {
        var plan = SessionSchedule.PlanDay(Defaults, Mene, FirstDay);

        var decision = SessionSchedule.Decide(Defaults, Mene, plan.Bursts[0].End);

        Assert.Equal(SchedulePhase.Break, decision.Phase);
        Assert.Equal(plan.Bursts[1].Start, decision.Until);
        Assert.StartsWith("On a break until ", decision.Status);
    }

    [Fact]
    public void BeforeTheEvening_ItIsQuiet_UntilTheFirstBurst()
    {
        var plan = SessionSchedule.PlanDay(Defaults, Mene, FirstDay);

        var decision = SessionSchedule.Decide(Defaults, Mene, new DateTime(2026, 11, 12, 10, 0, 0));

        Assert.Equal(SchedulePhase.Quiet, decision.Phase);
        Assert.Equal(plan.Bursts[0].Start, decision.Until);
        Assert.StartsWith("Quiet — next session Thu 17:", decision.Status);
    }

    [Fact]
    public void AfterTheLastBurst_ItIsQuiet_UntilTomorrowsFirst()
    {
        var plan = SessionSchedule.PlanDay(Defaults, Mene, FirstDay);
        var tomorrow = SessionSchedule.PlanDay(Defaults, Mene, FirstDay.AddDays(1));

        var decision = SessionSchedule.Decide(Defaults, Mene, plan.Bursts[^1].End);

        Assert.Equal(SchedulePhase.Quiet, decision.Phase);
        Assert.Equal(tomorrow.Bursts[0].Start, decision.Until);
    }

    /// <summary>
    /// Every seven minutes for four days, for two characters: it is Work exactly when a burst holds the
    /// moment, and <c>Until</c> is the moment the phase really changes - including the hour past midnight
    /// that belongs to the evening before.
    /// </summary>
    [Fact]
    public void AroundTheClock_TheDecisionFollowsThePlan_AndUntilIsWhenItChanges()
    {
        foreach (var character in new[] { Mene, Pandia })
        {
            var bursts = Enumerable.Range(-1, 6)
                .SelectMany(day => SessionSchedule.PlanDay(Defaults, character, FirstDay.AddDays(day)).Bursts)
                .ToList();
            var sawWorkPastMidnight = false;

            var end = FirstDay.AddDays(4).ToDateTime(TimeOnly.MinValue);
            for (var now = FirstDay.ToDateTime(TimeOnly.MinValue); now < end; now = now.AddMinutes(7))
            {
                var decision = SessionSchedule.Decide(Defaults, character, now);
                var working = bursts.Any(burst => now >= burst.Start && now < burst.End);

                Assert.Equal(working, decision.Phase == SchedulePhase.Work);
                Assert.True(decision.Until > now);
                Assert.NotEqual(decision.Phase, SessionSchedule.Decide(Defaults, character, decision.Until).Phase);
                Assert.Equal(decision.Phase, SessionSchedule.Decide(Defaults, character, decision.Until.AddSeconds(-1)).Phase);

                sawWorkPastMidnight |= working && now.Hour == 0;
            }

            Assert.True(sawWorkPastMidnight, "no burst ran past midnight in four evenings - the test proves less than it says");
        }
    }

    /// <summary>Play hours too short for anything: it says so and waits - it must not spin or throw.</summary>
    [Fact]
    public void PlayHoursNothingFitsIn_AreQuiet_AndSaySo()
    {
        var nothingFits = new ScheduleSettings
        {
            WindowStartMinute = 17 * 60,
            WindowEndMinute = (17 * 60) + 10,
            StartDelay = new MinuteRange(0, 0),
            EndEarly = new MinuteRange(0, 0),
        };
        var now = new DateTime(2026, 11, 12, 17, 5, 0);

        Assert.Empty(SessionSchedule.PlanDay(nothingFits, Mene, FirstDay).Bursts);

        var decision = SessionSchedule.Decide(nothingFits, Mene, now);

        Assert.Equal(SchedulePhase.Quiet, decision.Phase);
        Assert.True(decision.Until > now);
        Assert.Contains("no session fits", decision.Status);
    }

    /// <summary>The dice: in range, and the same seed deals the same numbers.</summary>
    [Fact]
    public void TheDice_AreRepeatable_AndStayBelowOne()
    {
        var one = new SeededRandom(Mene, FirstDay.DayNumber);
        var two = new SeededRandom(Mene, FirstDay.DayNumber);

        for (var i = 0; i < 1000; i++)
        {
            var value = one.NextDouble();

            Assert.InRange(value, 0.0, 1.0);
            Assert.NotEqual(1.0, value);
            Assert.Equal(value, two.NextDouble());
        }
    }
}
