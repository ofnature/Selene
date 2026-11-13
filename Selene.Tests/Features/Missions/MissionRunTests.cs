using Selene.Features.Missions;

namespace Selene.Tests.Features.Missions;

public class MissionRunTests
{
    private const uint Mission = 1395;
    private static readonly uint[] Picked = { 1395, 1396 };

    private static readonly HephaestusStatus Idle = new(true, false, false, string.Empty, "None", 87, 20);
    private static readonly HephaestusStatus Busy = Idle with { Busy = true };

    private DateTime _now = new(2026, 11, 12, 21, 0, 0);

    private static RunObservation Obs(HephaestusStatus hephaestus, int? clock = 300, uint score = 0, uint mission = Mission, bool log = true, bool inZone = true)
        => new(new MissionState(inZone, 1319, 8, mission, score, MissionRank.None, new uint[] { 1395, 1396, 1397 }, 0, 0, clock), hephaestus, log);

    private RunCommand Tick(MissionRun run, RunObservation obs, double seconds = 1)
    {
        _now = _now.AddSeconds(seconds);
        return run.Tick(obs, _now);
    }

    private MissionRun Started()
    {
        var run = new MissionRun();
        Assert.True(run.Start(Obs(Idle), Picked, _now));
        return run;
    }

    /// <summary>The whole road: craft is asked for once, the clock ends it, the item in hand is finished, Report is pressed.</summary>
    [Fact]
    public void AMission_IsCrafted_FinishedBeforeTheClock_AndReported()
    {
        var run = Started();

        Assert.Equal(RunCommand.StartCraft, Tick(run, Obs(Idle)));
        Assert.Equal(RunCommand.None, Tick(run, Obs(Busy, clock: 298)));
        Assert.Equal(RunPhase.Crafting, run.Phase);

        Assert.Equal(RunCommand.None, Tick(run, Obs(Busy, clock: 200, score: 1000)));
        Assert.StartsWith("Crafting — score 1,000, 3:20 left", run.Status);

        Assert.Equal(RunCommand.StopAfterItem, Tick(run, Obs(Busy, clock: MissionRun.StopMarginSeconds, score: 1800)));
        Assert.Equal(RunPhase.Finishing, run.Phase);
        Assert.Equal(RunCommand.None, Tick(run, Obs(Busy, clock: 60, score: 1800)));

        Assert.Equal(RunCommand.None, Tick(run, Obs(Idle, clock: 30, score: 2600))); // the item landed: now report
        Assert.Equal(RunPhase.Reporting, run.Phase);
        Assert.Equal(RunCommand.Report, Tick(run, Obs(Idle, clock: 29, score: 2600)));

        Assert.Equal(RunCommand.None, Tick(run, Obs(Idle, clock: null, mission: 0)));
        Assert.Equal(RunPhase.Done, run.Phase);
        Assert.Equal("Done — reported with a score of 2,600.", run.Status);
        Assert.False(run.Active);
    }

    public static TheoryData<string, string> Refusals => new()
    {
        { "zone", "not in a Cosmic Exploration zone" },
        { "none", "accept a mission first" },
        { "unpicked", "is not one you ticked" },
        { "absent", "Hephaestus is not loaded" },
        { "busy", "Hephaestus is busy" },
        { "oldbuild", "does not report its gear gate" },
        { "worn", "gear is at 12% (gate 20%)" },
        { "nolog", "open the Cosmic Crafting Log" },
        { "noclock", "clock cannot be seen" },
        { "late", "only 100 s left" },
    };

    /// <summary>Nothing is started on a guess: each thing a run needs is checked, and the reason is said.</summary>
    [Theory]
    [MemberData(nameof(Refusals))]
    public void ARun_IsNotStarted_WithoutEverythingItNeeds(string what, string says)
    {
        var obs = what switch
        {
            "zone" => Obs(Idle, inZone: false),
            "none" => Obs(Idle, mission: 0),
            "unpicked" => Obs(Idle, mission: 1397),
            "absent" => Obs(HephaestusStatus.Absent),
            "busy" => Obs(Busy),
            "oldbuild" => Obs(Idle with { GearConditionPercent = -1, GearGatePercent = -1 }),
            "worn" => Obs(Idle with { GearConditionPercent = 12 }),
            "nolog" => Obs(Idle, log: false),
            "noclock" => Obs(Idle, clock: null),
            _ => Obs(Idle, clock: MissionRun.StopMarginSeconds),
        };
        var run = new MissionRun();

        Assert.False(run.Start(obs, Picked, _now));

        Assert.False(run.Active);
        Assert.StartsWith("Not started — ", run.Status);
        Assert.Contains(says, run.Status);
        Assert.Equal(RunCommand.None, Tick(run, obs));
    }

    /// <summary>Pace and cap: the same command never twice within two seconds, three times at most, then a person.</summary>
    [Fact]
    public void HephaestusThatNeverStarts_IsAskedThreeTimes_ThenTheRunHalts()
    {
        var run = Started();
        var asked = 0;

        for (var i = 0; i < 12 && run.Active; i++)
        {
            if (Tick(run, Obs(Idle)) == RunCommand.StartCraft)
                asked++;
        }

        Assert.Equal(MissionRun.MaxAttempts, asked);
        Assert.Equal(RunPhase.Halted, run.Phase);
        Assert.Contains("Hephaestus did not start crafting", run.Status);
    }

    [Fact]
    public void AReportThatDoesNotGoThrough_IsPressedThreeTimes_ThenTheRunHalts()
    {
        var run = Started();
        Tick(run, Obs(Idle));
        Tick(run, Obs(Busy));
        Tick(run, Obs(Busy, clock: 90, score: 1500));
        Tick(run, Obs(Idle, clock: 40, score: 1500));
        var pressed = 0;

        for (var i = 0; i < 12 && run.Active; i++)
        {
            if (Tick(run, Obs(Idle, clock: 30, score: 1500)) == RunCommand.Report)
                pressed++;
        }

        Assert.Equal(MissionRun.MaxAttempts, pressed);
        Assert.Equal(RunPhase.Halted, run.Phase);
        Assert.Contains("The report did not go through", run.Status);
    }

    /// <summary>Out of materials, worn gear: with points on the board the mission is reported, not left to run out.</summary>
    [Fact]
    public void WhenHephaestusStopsByItself_WithAScore_TheMissionIsReported()
    {
        var run = Started();
        Tick(run, Obs(Idle));
        Tick(run, Obs(Busy));

        Assert.Equal(RunCommand.None, Tick(run, Obs(Idle with { RunHalt = "OutOfMaterials" }, clock: 220, score: 1200)));
        Assert.Equal(RunPhase.Reporting, run.Phase);
        Assert.Contains("OutOfMaterials", run.Status);
        Assert.Equal(RunCommand.Report, Tick(run, Obs(Idle, clock: 219, score: 1200)));
    }

    /// <summary>With nothing scored there is nothing to report - and Selene never abandons: it halts.</summary>
    [Fact]
    public void WhenHephaestusStopsByItself_WithNothingScored_TheRunHalts()
    {
        var run = Started();
        Tick(run, Obs(Idle));
        Tick(run, Obs(Busy));

        Assert.Equal(RunCommand.None, Tick(run, Obs(Idle with { RunHalt = "GearGate" }, clock: 250)));

        Assert.Equal(RunPhase.Halted, run.Phase);
        Assert.Contains("before anything was scored (GearGate)", run.Status);
    }

    /// <summary>The clock gone from sight mid-craft is treated as the clock running out: finish and report.</summary>
    [Fact]
    public void LosingSightOfTheClock_FinishesTheItemInHand()
    {
        var run = Started();
        Tick(run, Obs(Idle));
        Tick(run, Obs(Busy));

        Assert.Equal(RunCommand.StopAfterItem, Tick(run, Obs(Busy, clock: null, score: 900)));
        Assert.Equal(RunPhase.Finishing, run.Phase);
    }

    /// <summary>Reported by hand, timed out, abandoned: not ours any more - stop Hephaestus and say so.</summary>
    [Fact]
    public void AMissionThatEndsUnderTheRun_HaltsIt_AndStopsHephaestus()
    {
        var run = Started();
        Tick(run, Obs(Idle));
        Tick(run, Obs(Busy));

        Assert.Equal(RunCommand.StopAfterItem, Tick(run, Obs(Busy, clock: null, mission: 0)));

        Assert.Equal(RunPhase.Halted, run.Phase);
        Assert.Contains("ended before Selene reported it", run.Status);
    }

    [Fact]
    public void AnItemThatWillNotFinish_HaltsTheRun_AndNothingIsReportedMidCraft()
    {
        var run = Started();
        Tick(run, Obs(Idle));
        Tick(run, Obs(Busy));
        Tick(run, Obs(Busy, clock: 95, score: 500));

        Assert.Equal(RunCommand.None, Tick(run, Obs(Busy, clock: 1, score: 500), MissionRun.FinishDeadline.TotalSeconds + 1));

        Assert.Equal(RunPhase.Halted, run.Phase);
        Assert.Contains("did not finish the item in time", run.Status);
    }

    /// <summary>The user's Stop: Hephaestus finishes the item, the mission is left alone, nothing is reported.</summary>
    [Fact]
    public void Stop_EndsTheRun_FinishesTheItem_AndLeavesTheMission()
    {
        var run = Started();
        Tick(run, Obs(Idle));
        Tick(run, Obs(Busy));

        Assert.Equal(RunCommand.StopAfterItem, run.Stop(Obs(Busy, clock: 200, score: 700)));

        Assert.Equal(RunPhase.Halted, run.Phase);
        Assert.Contains("Stopped by you", run.Status);
        Assert.Equal(RunCommand.None, Tick(run, Obs(Idle, clock: 150, score: 1000)));
        Assert.Equal(RunCommand.None, run.Stop(Obs(Idle)));
    }

    [Fact]
    public void ARunningRun_CannotBeStartedAgain()
    {
        var run = Started();

        Assert.False(run.Start(Obs(Idle), Picked, _now));
        Assert.True(run.Active);
    }
}
