using System;
using System.Collections.Generic;
using System.Globalization;

namespace Selene.Features.Schedule;

/// <summary>One stretch of work.</summary>
public readonly record struct Burst(DateTime Start, DateTime End);

/// <summary>
/// One character's play day: the hours it may play in and the bursts drawn inside them. A play day is
/// named after the date its hours OPEN on; it may run past midnight.
/// </summary>
public sealed record DayPlan(DateOnly Day, DateTime Opens, DateTime Closes, IReadOnlyList<Burst> Bursts)
{
    /// <summary>All the work the day holds.</summary>
    public TimeSpan Total => WorkedBy(DateTime.MaxValue);

    /// <summary>How much of the day's work lies before <paramref name="now"/>.</summary>
    public TimeSpan WorkedBy(DateTime now)
    {
        var worked = TimeSpan.Zero;
        foreach (var burst in Bursts)
        {
            if (now > burst.Start)
                worked += (now < burst.End ? now : burst.End) - burst.Start;
        }

        return worked;
    }

    /// <summary>The number of the burst that holds <paramref name="now"/>, counting from 1; 0 when none does.</summary>
    public int BurstAt(DateTime now)
    {
        for (var i = 0; i < Bursts.Count; i++)
        {
            if (now >= Bursts[i].Start && now < Bursts[i].End)
                return i + 1;
        }

        return 0;
    }
}

public enum SchedulePhase
{
    /// <summary>Inside a burst: missions may run.</summary>
    Work,

    /// <summary>Between two bursts of the same play day.</summary>
    Break,

    /// <summary>Outside the day's work: before its first burst or after its last.</summary>
    Quiet,
}

/// <param name="Until">When the phase changes next.</param>
/// <param name="Status">What the schedule says, for the Debug tab.</param>
public readonly record struct ScheduleDecision(SchedulePhase Phase, DateTime Until, string Status);

/// <summary>
/// The session schedule - the reason Selene exists: work in bursts, rest in between, never at the same
/// times twice. Pure: a character, a clock and the settings in, "work now" or "rest now" out, with the
/// next change. The dice are seeded by the character and the day (<see cref="SeededRandom"/>), so two
/// characters never start and stop in step and nothing has to be stored or coordinated.
///
/// Times are the PC's local wall clock, as the user thinks of their play hours. On the night the
/// clocks change, that evening's plan is an hour off; nothing else depends on it.
/// </summary>
public static class SessionSchedule
{
    /// <summary>
    /// A burst that the end of the day (or the daily cap) cut shorter than this is not started: walking
    /// back in for a few minutes is not what a person does. A burst DRAWN shorter than this is kept.
    /// </summary>
    public static readonly TimeSpan ShortestCutBurst = TimeSpan.FromMinutes(30);

    /// <summary>How many days ahead the next session is looked for when today has none.</summary>
    private const int LookAheadDays = 7;

    /// <summary>The bursts of one character's play day. Same inputs, same plan - always.</summary>
    public static DayPlan PlanDay(ScheduleSettings settings, ulong contentId, DateOnly day)
    {
        var random = new SeededRandom(contentId, day.DayNumber);

        var opens = day.ToDateTime(TimeOnly.MinValue).AddMinutes(settings.WindowStartMinute);
        var windowMinutes = (((settings.WindowEndMinute - settings.WindowStartMinute) % 1440) + 1440) % 1440;
        var closes = opens.AddMinutes(windowMinutes == 0 ? 1440 : windowMinutes);

        var at = opens + Draw(ref random, settings.StartDelay);
        var stop = closes - Draw(ref random, settings.EndEarly);
        var capLeft = Draw(ref random, settings.DailyCap);

        var bursts = new List<Burst>();
        while (true)
        {
            var drawn = Draw(ref random, settings.Burst);
            var length = Shortest(drawn, stop - at, capLeft);
            if (length <= TimeSpan.Zero || (length < drawn && length < ShortestCutBurst))
                break;

            bursts.Add(new Burst(at, at + length));
            capLeft -= length;
            at += length + Draw(ref random, settings.Break);
        }

        return new DayPlan(day, opens, closes, bursts);
    }

    /// <summary>
    /// The play day <paramref name="now"/> belongs to, for showing: last evening's while its work is
    /// still running past midnight, otherwise today's (which may not have begun, or may be over).
    /// </summary>
    public static DayPlan CurrentDay(ScheduleSettings settings, ulong contentId, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var yesterday = PlanDay(settings, contentId, today.AddDays(-1));

        return yesterday.Bursts.Count > 0 && now < yesterday.Bursts[^1].End
            ? yesterday
            : PlanDay(settings, contentId, today);
    }

    /// <summary>
    /// What the character should be doing at <paramref name="now"/> (the PC's local time), and until
    /// when. It plans a day or two on every call: ask once a second, not once a frame.
    /// </summary>
    public static ScheduleDecision Decide(ScheduleSettings settings, ulong contentId, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);

        // Yesterday's play day may still be running past midnight; today's may not have begun.
        foreach (var day in new[] { today.AddDays(-1), today })
        {
            var bursts = PlanDay(settings, contentId, day).Bursts;
            if (bursts.Count == 0 || now < bursts[0].Start || now >= bursts[^1].End)
                continue;

            foreach (var burst in bursts)
            {
                if (now < burst.Start)
                    return new ScheduleDecision(SchedulePhase.Break, burst.Start, $"On a break until {Clock(burst.Start)}");
                if (now < burst.End)
                    return new ScheduleDecision(SchedulePhase.Work, burst.End, $"Working until {Clock(burst.End)}");
            }
        }

        for (var ahead = 0; ahead <= LookAheadDays; ahead++)
        {
            var bursts = PlanDay(settings, contentId, today.AddDays(ahead)).Bursts;
            if (bursts.Count > 0 && bursts[0].Start > now)
            {
                var next = bursts[0].Start;
                return new ScheduleDecision(SchedulePhase.Quiet, next, $"Quiet — next session {next.ToString("ddd HH:mm", CultureInfo.InvariantCulture)}");
            }
        }

        return new ScheduleDecision(SchedulePhase.Quiet, now.AddDays(1), "Quiet — no session fits the play hours as they are set");
    }

    private static TimeSpan Draw(ref SeededRandom random, MinuteRange range)
    {
        var min = Math.Max(0, range.Min);
        var max = Math.Max(min, range.Max);

        // To the second: a start that always falls on a whole minute would be a pattern of its own.
        return TimeSpan.FromSeconds(Math.Round((min + ((max - min) * random.NextDouble())) * 60));
    }

    private static TimeSpan Shortest(TimeSpan a, TimeSpan b, TimeSpan c)
    {
        var shortest = a < b ? a : b;
        return shortest < c ? shortest : c;
    }

    private static string Clock(DateTime time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
}
