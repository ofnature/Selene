using System;
using System.Collections.Generic;
using System.Linq;

namespace Selene.Features.Schedule;

/// <summary>One character on the board: its play day and what the schedule says of it right now.</summary>
/// <param name="NextEvening">When the play day AFTER the one on show starts its first burst; null when it has none.</param>
public sealed record BoardRow(
    ulong ContentId, string Name, bool IsThisCharacter, DayPlan Plan, ScheduleDecision Decision, DateTime? NextEvening);

/// <summary>
/// The schedule of every character this PC knows, for the window. Any client can show any character:
/// a plan needs only the content id and the day, so nothing is asked of the other clients.
/// </summary>
public static class ScheduleBoard
{
    /// <summary>The logged-in character first, the others by name.</summary>
    public static IReadOnlyList<BoardRow> Build(
        IReadOnlyDictionary<ulong, string> roster, ulong thisCharacter, ScheduleSettings settings, DateTime now)
    {
        return roster
            .Select(character =>
            {
                var plan = SessionSchedule.CurrentDay(settings, character.Key, now);
                var after = SessionSchedule.PlanDay(settings, character.Key, plan.Day.AddDays(1)).Bursts;
                return new BoardRow(
                    character.Key,
                    character.Value,
                    character.Key == thisCharacter,
                    plan,
                    SessionSchedule.Decide(settings, character.Key, now),
                    after.Count > 0 ? after[0].Start : null);
            })
            .OrderByDescending(row => row.IsThisCharacter)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ContentId)
            .ToList();
    }
}
