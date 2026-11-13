namespace Selene.Features.Schedule;

/// <summary>A span a value is drawn from, in minutes, both ends included.</summary>
public readonly record struct MinuteRange(int Min, int Max);

/// <summary>
/// One character's session schedule, as the user set it. Nothing here is a time of day the character
/// acts at: every value is a RANGE, and <see cref="SessionSchedule"/> draws from it afresh for each
/// character and each day, so the schedule itself is not a pattern.
///
/// The defaults are the user's answers of 2026-11-12 (docs/scoping-decisions.md): evenings 17:00-01:00,
/// bursts of 2 to 4 hours, breaks of 30 to 90 minutes, a daily cap of 8 to 10 hours. The start delay
/// and the early end were not asked: they are what makes "a start time that moves from day to day".
/// </summary>
public sealed class ScheduleSettings
{
    /// <summary>When the play hours open, in minutes after midnight on the PC's clock.</summary>
    public int WindowStartMinute { get; set; } = 17 * 60;

    /// <summary>
    /// When the play hours close, in minutes after midnight. Earlier than the start means the next
    /// morning; the same as the start means the whole day.
    /// </summary>
    public int WindowEndMinute { get; set; } = 1 * 60;

    /// <summary>How long one burst of work lasts.</summary>
    public MinuteRange Burst { get; set; } = new(120, 240);

    /// <summary>How long the break between two bursts lasts.</summary>
    public MinuteRange Break { get; set; } = new(30, 90);

    /// <summary>The most work in one play day.</summary>
    public MinuteRange DailyCap { get; set; } = new(480, 600);

    /// <summary>How long after the play hours open the first burst starts.</summary>
    public MinuteRange StartDelay { get; set; } = new(0, 45);

    /// <summary>How long before the play hours close the day's work ends.</summary>
    public MinuteRange EndEarly { get; set; } = new(0, 30);
}
