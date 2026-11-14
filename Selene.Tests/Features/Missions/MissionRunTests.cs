using Selene.Features.Missions;

namespace Selene.Tests.Features.Missions;

public class MissionRunTests
{
    private const uint Mission = 1395;
    private static readonly uint[] Picked = { 1395, 1396 };

    private static readonly HephaestusStatus Idle = new(true, false, false, string.Empty, "None", 87, 20);
    private static readonly HephaestusStatus Busy = Idle with { Busy = true };

    private DateTime _now = new(2026, 11, 12, 21, 0, 0);

    private static RunObservation Obs(
        HephaestusStatus hephaestus, int? clock = 300, uint score = 0, uint mission = Mission, bool log = true, bool inZone = true,
        uint[]? offered = null, uint bonus = 0, bool board = false, uint[]? rows = null, uint selected = 0)
        => new(
            new MissionState(inZone, 1319, 8, mission, score, MissionRank.None, offered ?? new uint[] { 1395, 1396, 1397 }, bonus, 0, clock),
            hephaestus, log, board, rows, selected);

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
        { "notoffered", "none of the missions you ticked is offered" },
        { "unpicked", "is not one you ticked" },
        { "absent", "Hephaestus is not loaded" },
        { "busy", "Hephaestus is busy" },
        { "oldbuild", "does not report its gear gate" },
        { "worn", "gear is at 12% (gate 20%)" },
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
            "notoffered" => Obs(Idle, mission: 0, offered: new uint[] { 1397 }),
            "unpicked" => Obs(Idle, mission: 1397),
            "absent" => Obs(HephaestusStatus.Absent),
            "busy" => Obs(Busy),
            "oldbuild" => Obs(Idle with { GearConditionPercent = -1, GearGatePercent = -1 }),
            "worn" => Obs(Idle with { GearConditionPercent = 12 }),
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
    public void AReportThatDoesNotGoThrough_IsPressedFiveTimes_ThenTheRunHalts()
    {
        var run = Started();
        Tick(run, Obs(Idle));
        Tick(run, Obs(Busy));
        Tick(run, Obs(Busy, clock: 90, score: 1500));
        Tick(run, Obs(Idle, clock: 40, score: 1500));
        var pressed = 0;

        for (var i = 0; i < 20 && run.Active; i++)
        {
            if (Tick(run, Obs(Idle, clock: 30, score: 1500)) == RunCommand.Report)
                pressed++;
        }

        Assert.Equal(MissionRun.ReportAttempts, pressed);
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

    // ---- accepting -----------------------------------------------------------------------------

    private static readonly uint[] MasteryRows = { 1395, 1396, 1397 };
    private static readonly uint[] BasicRows = { 101, 102, 103 };

    /// <summary>The whole road in: the board, the row, Initiate, the log, the recipe - each seen before the next.</summary>
    [Fact]
    public void WithNoMissionInProgress_ATickedOneIsAccepted_AndItsLogOpened()
    {
        var run = new MissionRun();
        Assert.True(run.Start(Obs(Idle, mission: 0), Picked, _now));
        Assert.Equal(1395u, run.MissionId);

        Assert.Equal(RunCommand.OpenBoard, Tick(run, Obs(Idle, mission: 0)));
        Assert.Equal(RunCommand.None, Tick(run, Obs(Idle, mission: 0, board: true, rows: MasteryRows)));
        Assert.Equal(RunPhase.FindingMission, run.Phase);
        Assert.Equal(RunCommand.None, Tick(run, Obs(Idle, mission: 0, board: true, rows: MasteryRows)));
        Assert.Equal(RunPhase.Selecting, run.Phase);
        Assert.Equal(RunCommand.SelectMission, Tick(run, Obs(Idle, mission: 0, board: true, rows: MasteryRows)));
        Assert.Equal(RunCommand.None, Tick(run, Obs(Idle, mission: 0, board: true, rows: MasteryRows, selected: 1395)));
        Assert.Equal(RunPhase.Initiating, run.Phase);
        Assert.Equal(RunCommand.InitiateMission, Tick(run, Obs(Idle, mission: 0, board: true, rows: MasteryRows, selected: 1395)));

        Assert.Equal(RunCommand.None, Tick(run, Obs(Idle, log: false))); // the game shows it in progress
        Assert.Equal(RunPhase.OpeningLog, run.Phase);
        Assert.Equal(RunCommand.OpenLog, Tick(run, Obs(Idle, log: false)));
        Assert.Equal(RunCommand.None, Tick(run, Obs(Idle)));
        Assert.Equal(RunCommand.SelectRecipe, Tick(run, Obs(Idle)));
        Assert.Equal(RunCommand.None, Tick(run, Obs(Idle))); // paced: the craft is not asked for in the same breath
        Assert.Equal(RunCommand.StartCraft, Tick(run, Obs(Idle)));
        Assert.Equal(RunCommand.None, Tick(run, Obs(Busy)));
        Assert.Equal(RunPhase.Crafting, run.Phase);
    }

    [Fact]
    public void TheBonusMission_IsTakenFirst_WhenItIsTicked()
    {
        var run = new MissionRun();

        Assert.True(run.Start(Obs(Idle, mission: 0, bonus: 1396), Picked, _now));
        Assert.Equal(1396u, run.MissionId);

        var other = new MissionRun();
        Assert.True(other.Start(Obs(Idle, mission: 0, bonus: 1397), Picked, _now)); // the bonus one is not ticked
        Assert.Equal(1395u, other.MissionId);
    }

    /// <summary>Worn gear, a busy or absent Hephaestus: found out BEFORE a mission is taken, not after.</summary>
    [Fact]
    public void NothingIsAccepted_OnGearHephaestusWouldNotCraftOn()
    {
        var run = new MissionRun();

        Assert.False(run.Start(Obs(Idle with { GearConditionPercent = 12 }, mission: 0), Picked, _now));

        Assert.Contains("repair first", run.Status);
        Assert.Equal(RunCommand.None, Tick(run, Obs(Idle, mission: 0)));
    }

    /// <summary>The board on another tab: the Tool Mastery tab is asked for, three times at most.</summary>
    [Fact]
    public void AMissionNotAmongTheRows_AsksForTheMasteryTab_ThenHalts()
    {
        var run = new MissionRun();
        run.Start(Obs(Idle, mission: 0), Picked, _now);
        Tick(run, Obs(Idle, mission: 0));
        Tick(run, Obs(Idle, mission: 0, board: true, rows: BasicRows));
        var asked = 0;

        for (var i = 0; i < 12 && run.Active; i++)
        {
            if (Tick(run, Obs(Idle, mission: 0, board: true, rows: BasicRows)) == RunCommand.ShowMasteryTab)
                asked++;
        }

        Assert.Equal(MissionRun.MaxAttempts, asked);
        Assert.Equal(RunPhase.Halted, run.Phase);
        Assert.Contains("not among the board's rows", run.Status);
    }

    /// <summary>Initiate is never pressed on a row that is not the selected one.</summary>
    [Fact]
    public void Initiate_IsOnlyPressed_OnceTheMissionIsTheSelectedOne()
    {
        var run = new MissionRun();
        run.Start(Obs(Idle, mission: 0), Picked, _now);
        Tick(run, Obs(Idle, mission: 0));
        Tick(run, Obs(Idle, mission: 0, board: true, rows: MasteryRows));
        Tick(run, Obs(Idle, mission: 0, board: true, rows: MasteryRows));

        for (var i = 0; i < 12 && run.Active; i++)
            Assert.NotEqual(RunCommand.InitiateMission, Tick(run, Obs(Idle, mission: 0, board: true, rows: MasteryRows, selected: 1397)));

        Assert.Equal(RunPhase.Halted, run.Phase);
        Assert.Contains("could not be selected", run.Status);
    }

    [Fact]
    public void AnotherMissionTurningUpInProgress_HaltsTheRun()
    {
        var run = new MissionRun();
        run.Start(Obs(Idle, mission: 0), Picked, _now);
        Tick(run, Obs(Idle, mission: 0));

        Assert.Equal(RunCommand.None, Tick(run, Obs(Idle, mission: 1397)));

        Assert.Equal(RunPhase.Halted, run.Phase);
        Assert.Contains("Another mission (1397)", run.Status);
    }

    /// <summary>A confirmation the game puts up is not answered: the run stops there and says so.</summary>
    [Fact]
    public void AnInitiateThatDoesNotTake_Halts_AndPointsAtAConfirmation()
    {
        var run = new MissionRun();
        run.Start(Obs(Idle, mission: 0), Picked, _now);
        var ready = Obs(Idle, mission: 0, board: true, rows: MasteryRows, selected: 1395);
        Tick(run, Obs(Idle, mission: 0));
        Tick(run, ready);
        Tick(run, ready);
        Tick(run, ready);

        for (var i = 0; i < 15 && run.Active; i++)
            Tick(run, ready);

        Assert.Equal(RunPhase.Halted, run.Phase);
        Assert.Contains("a confirmation may be waiting", run.Status);
    }

    /// <summary>"Commence selected mission?" - seen in game after Initiate: answered when it is about OUR mission.</summary>
    [Fact]
    public void TheGamesOwnConfirmation_IsAnswered_ForTheMissionSeleneWentFor()
    {
        var run = new MissionRun();
        run.Start(Obs(Idle, mission: 0), Picked, _now);
        var ready = Obs(Idle, mission: 0, board: true, rows: MasteryRows, selected: 1395);
        Tick(run, Obs(Idle, mission: 0));
        Tick(run, ready);
        Tick(run, ready);
        Tick(run, ready);
        Assert.Equal(RunCommand.InitiateMission, Tick(run, ready));

        var asking = ready with { ConfirmMission = 1395 };
        Assert.Equal(RunCommand.None, Tick(run, asking)); // paced after the Initiate
        Assert.Equal(RunCommand.ConfirmMission, Tick(run, asking));
        Assert.Equal("Confirming the mission", run.Status);

        Assert.Equal(RunCommand.None, Tick(run, Obs(Idle, log: false)));
        Assert.Equal(RunPhase.OpeningLog, run.Phase);
    }

    /// <summary>A question about any other mission is not Selene's to answer.</summary>
    [Fact]
    public void AConfirmationForAnotherMission_IsNotAnswered()
    {
        var run = new MissionRun();
        run.Start(Obs(Idle, mission: 0), Picked, _now);
        var ready = Obs(Idle, mission: 0, board: true, rows: MasteryRows, selected: 1395);
        Tick(run, Obs(Idle, mission: 0));
        Tick(run, ready);
        Tick(run, ready);
        Tick(run, ready);

        Assert.Equal(RunCommand.None, Tick(run, ready with { ConfirmMission = 1397 }));

        Assert.Equal(RunPhase.Halted, run.Phase);
        Assert.Contains("asks to commence mission 1397", run.Status);
    }

    /// <summary>The first way in still works: a mission accepted by hand, the log not open yet.</summary>
    [Fact]
    public void AMissionAcceptedByHand_HasItsLogOpened()
    {
        var run = new MissionRun();

        Assert.True(run.Start(Obs(Idle, log: false), Picked, _now));
        Assert.Equal(RunPhase.OpeningLog, run.Phase);
        Assert.Equal(RunCommand.OpenLog, Tick(run, Obs(Idle, log: false)));
    }

    [Fact]
    public void StoppedBeforeAnythingWasAccepted_SaysSo()
    {
        var run = new MissionRun();
        run.Start(Obs(Idle, mission: 0), Picked, _now);

        Assert.Equal(RunCommand.None, run.Stop(Obs(Idle, mission: 0)));
        Assert.Contains("no mission was accepted", run.Status);
    }

    [Fact]
    public void ARunningRun_CannotBeStartedAgain()
    {
        var run = Started();

        Assert.False(run.Start(Obs(Idle), Picked, _now));
        Assert.True(run.Active);
    }
}
