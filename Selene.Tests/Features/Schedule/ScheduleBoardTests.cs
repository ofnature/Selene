using Selene.Features.Schedule;

namespace Selene.Tests.Features.Schedule;

public class ScheduleBoardTests
{
    private const ulong Mene = 0x0040_0000_1234_5678UL;
    private const ulong Pandia = 0x0040_0000_8765_4321UL;
    private const ulong Hesper = 0x0040_0000_0BAD_F00DUL;

    private static readonly DateOnly Day = new(2026, 11, 12);
    private static readonly ScheduleSettings Defaults = new();

    private static readonly Dictionary<ulong, string> Roster = new()
    {
        [Pandia] = "Pandia",
        [Mene] = "Mene",
        [Hesper] = "Hesper",
    };

    [Fact]
    public void TheLoggedInCharacter_ComesFirst_TheOthersByName()
    {
        var rows = ScheduleBoard.Build(Roster, Mene, Defaults, new DateTime(2026, 11, 12, 18, 30, 0));

        Assert.Equal(new[] { "Mene", "Hesper", "Pandia" }, rows.Select(row => row.Name));
        Assert.Equal(new[] { true, false, false }, rows.Select(row => row.IsThisCharacter));
    }

    /// <summary>At the title screen there is nobody to put first: every known character is still shown.</summary>
    [Fact]
    public void WithNobodyLoggedIn_EveryKnownCharacterIsStillShown()
    {
        var rows = ScheduleBoard.Build(Roster, thisCharacter: 0, Defaults, new DateTime(2026, 11, 12, 18, 30, 0));

        Assert.Equal(new[] { "Hesper", "Mene", "Pandia" }, rows.Select(row => row.Name));
        Assert.DoesNotContain(rows, row => row.IsThisCharacter);
    }

    [Fact]
    public void EachRow_CarriesThatCharactersOwnPlanAndDecision()
    {
        var now = new DateTime(2026, 11, 12, 18, 30, 0);

        var rows = ScheduleBoard.Build(Roster, Mene, Defaults, now);

        foreach (var row in rows)
        {
            Assert.Equal(SessionSchedule.PlanDay(Defaults, row.ContentId, Day).Bursts, row.Plan.Bursts);
            Assert.Equal(SessionSchedule.Decide(Defaults, row.ContentId, now), row.Decision);
        }
    }

    /// <summary>"Next evening" is the play day AFTER the one on show - also in the hour past midnight.</summary>
    [Fact]
    public void EachRow_NamesTheFirstBurstOfThePlayDayAfterTheOneOnShow()
    {
        var pastMidnight = SessionSchedule.PlanDay(Defaults, Mene, Day).Bursts[^1].End.AddSeconds(-1);

        foreach (var now in new[] { new DateTime(2026, 11, 12, 18, 30, 0), pastMidnight })
        {
            var row = ScheduleBoard.Build(Roster, Mene, Defaults, now)[0];

            Assert.Equal(Day, row.Plan.Day);
            Assert.Equal(SessionSchedule.PlanDay(Defaults, Mene, Day.AddDays(1)).Bursts[0].Start, row.NextEvening);
        }
    }

    [Fact]
    public void WorkedSoFar_CountsFinishedBursts_AndTheRunningOneUpToNow()
    {
        var plan = SessionSchedule.PlanDay(Defaults, Mene, Day);
        var first = plan.Bursts[0];
        var second = plan.Bursts[1];

        Assert.Equal(TimeSpan.Zero, plan.WorkedBy(first.Start));
        Assert.Equal(TimeSpan.FromMinutes(10), plan.WorkedBy(first.Start.AddMinutes(10)));
        Assert.Equal(first.End - first.Start, plan.WorkedBy(first.End.AddMinutes(5))); // a break adds nothing
        Assert.Equal((first.End - first.Start) + TimeSpan.FromMinutes(7), plan.WorkedBy(second.Start.AddMinutes(7)));
        Assert.Equal(plan.Bursts.Aggregate(TimeSpan.Zero, (sum, burst) => sum + (burst.End - burst.Start)), plan.Total);
        Assert.Equal(plan.Total, plan.WorkedBy(plan.Closes.AddHours(3)));
    }

    [Fact]
    public void TheBurstNumber_CountsFromOne_AndIsZeroBetweenAndOutside()
    {
        var plan = SessionSchedule.PlanDay(Defaults, Mene, Day);

        Assert.Equal(0, plan.BurstAt(plan.Opens.AddHours(-1)));
        Assert.Equal(1, plan.BurstAt(plan.Bursts[0].Start));
        Assert.Equal(0, plan.BurstAt(plan.Bursts[0].End));
        Assert.Equal(2, plan.BurstAt(plan.Bursts[1].Start.AddMinutes(1)));
        Assert.Equal(0, plan.BurstAt(plan.Bursts[^1].End));
    }

    [Fact]
    public void AnEmptyRoster_IsAnEmptyBoard()
    {
        Assert.Empty(ScheduleBoard.Build(new Dictionary<ulong, string>(), Mene, Defaults, new DateTime(2026, 11, 12, 18, 30, 0)));
    }

    /// <summary>Past midnight the evening before is still the one on show - until its work is over.</summary>
    [Fact]
    public void PastMidnight_TheDayOnShow_IsLastEvenings_UntilItsWorkIsOver()
    {
        var lastEvening = SessionSchedule.PlanDay(Defaults, Mene, Day);
        var itsEnd = lastEvening.Bursts[^1].End;
        Assert.True(itsEnd > new DateTime(2026, 11, 13, 0, 0, 0), "this character's evening must run past midnight for the test to mean anything");

        Assert.Equal(Day, SessionSchedule.CurrentDay(Defaults, Mene, itsEnd.AddSeconds(-1)).Day);
        Assert.Equal(Day.AddDays(1), SessionSchedule.CurrentDay(Defaults, Mene, itsEnd).Day);
    }

    [Fact]
    public void BeforeTheEvening_TheDayOnShow_IsTodays()
    {
        Assert.Equal(Day, SessionSchedule.CurrentDay(Defaults, Mene, new DateTime(2026, 11, 12, 10, 0, 0)).Day);
    }
}
