using Selene.Features.Missions;

namespace Selene.Tests.Features.Missions;

/// <summary>
/// The missions that are not Master ones (the user, 2026-11-14: "should be all missions"): mostly untimed,
/// reported at a rank the user sets, some of several recipes. The Master road is in <see cref="MissionRunTests"/>.
/// </summary>
public class RankedMissionRunTests
{
    private const uint Ranked = 1370;   // one recipe, untimed
    private const uint TwoPart = 1377;  // a part, then the item

    private static readonly HephaestusStatus Idle = new(true, false, false, string.Empty, "None", 87, 20);
    private static readonly HephaestusStatus Busy = Idle with { Busy = true };

    private static readonly CraftStep[] Steps = { new(37965, 2), new(37966, 1) };

    private DateTime _now = new(2026, 11, 14, 21, 0, 0);

    private static MissionPlan PlanFor(uint mission) => mission switch
    {
        Ranked => new MissionPlan(Timed: false, ReportAt: MissionRank.Gold),
        TwoPart => new MissionPlan(Timed: false, ReportAt: MissionRank.Gold, Steps: Steps),
        _ => MissionPlan.Master,
    };

    private static RunObservation Obs(
        HephaestusStatus hephaestus, uint mission, uint score = 0, MissionRank rank = MissionRank.None, bool log = true,
        uint[]? held = null, bool board = false, uint[]? rows = null, uint selected = 0)
        => new(
            new MissionState(true, 1319, 8, mission, score, rank, Array.Empty<uint>(), 0, 0, null, held ?? new[] { Ranked, TwoPart }),
            hephaestus, log, board, rows, selected);

    private RunCommand Tick(MissionRun run, RunObservation obs, double seconds = 3)
    {
        _now = _now.AddSeconds(seconds);
        return run.Tick(obs, _now);
    }

    /// <summary>No clock to see is no reason to refuse a mission that has none.</summary>
    [Fact]
    public void AnUntimedMissionInProgress_IsTakenUp_WithNoClockToSee()
    {
        var run = new MissionRun(PlanFor);

        Assert.True(run.Start(Obs(Idle, Ranked), new[] { Ranked }, _now));
        Assert.Equal(RunCommand.StartCraft, Tick(run, Obs(Idle, Ranked)));
    }

    /// <summary>The user's setting: at the goal rank nothing more is started, the item in hand is finished, and it is reported.</summary>
    [Fact]
    public void ARankedMission_IsReported_OnceTheGoalRankShows()
    {
        var run = new MissionRun(PlanFor);
        run.Start(Obs(Idle, Ranked), new[] { Ranked }, _now);
        Tick(run, Obs(Idle, Ranked));

        Assert.Equal(RunCommand.None, Tick(run, Obs(Busy, Ranked)));
        Assert.Equal(RunCommand.None, Tick(run, Obs(Busy, Ranked, 1300, MissionRank.Silver)));
        Assert.Equal(RunPhase.Crafting, run.Phase);
        Assert.Equal("Crafting — score 1,300, rank Silver", run.Status);

        Assert.Equal(RunCommand.StopAfterItem, Tick(run, Obs(Busy, Ranked, 2400, MissionRank.Gold)));
        Assert.Equal(RunPhase.Finishing, run.Phase);

        Assert.Equal(RunCommand.None, Tick(run, Obs(Idle, Ranked, 2400, MissionRank.Gold)));
        Assert.Equal(RunCommand.Report, Tick(run, Obs(Idle, Ranked, 2400, MissionRank.Gold)));
        Assert.Equal(RunCommand.None, Tick(run, Obs(Idle, 0)));
        Assert.Equal(RunPhase.Done, run.Phase);
    }

    /// <summary>The materials ran out short of the goal: what was made is reported, as the user was told.</summary>
    [Fact]
    public void WhenHephaestusRunsOutBelowTheGoal_WhatWasMadeIsReported()
    {
        var run = new MissionRun(PlanFor);
        run.Start(Obs(Idle, Ranked), new[] { Ranked }, _now);
        Tick(run, Obs(Idle, Ranked));
        Tick(run, Obs(Busy, Ranked));

        Assert.Equal(RunCommand.None, Tick(run, Obs(Idle, Ranked, 1300, MissionRank.Silver)));

        Assert.Equal(RunPhase.Reporting, run.Phase);
    }

    /// <summary>A failed mission is not "at the goal": rank 5 is above gold only as a number.</summary>
    [Fact]
    public void AFailedRank_IsNotTakenForTheGoal()
    {
        var run = new MissionRun(PlanFor);
        run.Start(Obs(Idle, Ranked), new[] { Ranked }, _now);
        Tick(run, Obs(Idle, Ranked));
        Tick(run, Obs(Busy, Ranked));

        Tick(run, Obs(Busy, Ranked, 0, MissionRank.Failed));

        Assert.Equal(RunPhase.Crafting, run.Phase);
    }

    /// <summary>"Tick whole tabs": a mission the game holds on its other lists is accepted like a Master one.</summary>
    [Fact]
    public void AMissionTheGameHoldsOnItsOtherLists_IsAccepted()
    {
        var run = new MissionRun(PlanFor);

        Assert.True(run.Start(Obs(Idle, 0), new[] { Ranked }, _now));

        Assert.Equal(Ranked, run.MissionId);
        Assert.Equal(RunPhase.OpeningBoard, run.Phase);
    }

    /// <summary>Several recipes: each is asked of Hephaestus by its id, the part first, and the log is never opened by Selene.</summary>
    [Fact]
    public void AMissionOfSeveralRecipes_IsCraftedRecipeByRecipe_ThenReported()
    {
        var run = new MissionRun(PlanFor);
        run.Start(Obs(Idle, TwoPart, log: false), new[] { TwoPart }, _now);

        Assert.Equal(RunCommand.StartCraftStep, Tick(run, Obs(Idle, TwoPart, log: false)));
        Assert.Equal(Steps[0], run.Step);

        Tick(run, Obs(Busy, TwoPart, log: false));
        Assert.Equal(RunPhase.Crafting, run.Phase);

        // The parts are made and Hephaestus lets go: the item itself is next.
        Assert.Equal(RunCommand.None, Tick(run, Obs(Idle, TwoPart, log: false)));
        Assert.Equal(RunPhase.StartingCraft, run.Phase);
        Assert.Equal(RunCommand.StartCraftStep, Tick(run, Obs(Idle, TwoPart, log: false)));
        Assert.Equal(Steps[1], run.Step);

        Tick(run, Obs(Busy, TwoPart, log: false));

        // The item is made and gold shows: nothing is in hand to finish, so the next look reports.
        Assert.Equal(RunCommand.None, Tick(run, Obs(Idle, TwoPart, 1500, MissionRank.Gold, log: false)));
        Assert.Equal(RunCommand.None, Tick(run, Obs(Idle, TwoPart, 1500, MissionRank.Gold, log: false)));
        Assert.Equal(RunPhase.Reporting, run.Phase);
        Assert.Equal(RunCommand.Report, Tick(run, Obs(Idle, TwoPart, 1500, MissionRank.Gold, log: false)));
    }

    /// <summary>Accepted from the board, a mission of several recipes goes straight to its first craft.</summary>
    [Fact]
    public void AfterAccepting_AMissionOfSeveralRecipes_SkipsTheLog()
    {
        var run = new MissionRun(PlanFor);
        run.Start(Obs(Idle, 0), new[] { TwoPart }, _now);

        Tick(run, Obs(Idle, TwoPart, log: false));

        Assert.Equal(RunPhase.StartingCraft, run.Phase);
    }

    // ---- the order and the counts of a mission's recipes ----------------------------------------------

    private static (uint RecipeId, uint Makes, int Yield, IReadOnlyDictionary<uint, int> Uses) Recipe(
        uint id, uint makes, params (uint Item, int Amount)[] uses)
        => (id, makes, 1, uses.ToDictionary(u => u.Item, u => u.Amount));

    /// <summary>Mission 1377 as the game's data has it: one item wanted, made from two of a part.</summary>
    [Fact]
    public void APart_IsMadeFirst_AsOftenAsTheItemNeedsIt()
    {
        var steps = CraftStep.Plan(
            new[] { Recipe(37965, 52507, (48233, 1)), Recipe(37966, 52488, (52507, 2)) },
            new Dictionary<uint, int> { [52488] = 1 });

        Assert.Equal(new[] { new CraftStep(37965, 2), new CraftStep(37966, 1) }, steps);
    }

    /// <summary>The order in the sheet does not matter: the part still comes first.</summary>
    [Fact]
    public void ThePartComesFirst_WhateverTheSheetsOrder()
    {
        var steps = CraftStep.Plan(
            new[] { Recipe(2, 200, (100, 1)), Recipe(1, 100, (48233, 1)) },
            new Dictionary<uint, int> { [200] = 2 });

        Assert.Equal(new[] { new CraftStep(1, 2), new CraftStep(2, 2) }, steps);
    }

    /// <summary>Mission 26: three items wanted, none made from another.</summary>
    [Fact]
    public void ThreeWantedItems_AreEachMadeAsOftenAsAsked()
    {
        var steps = CraftStep.Plan(
            new[] { Recipe(36196, 48301, (48233, 1)), Recipe(36197, 48302, (48233, 1)), Recipe(36198, 48303, (48233, 1)) },
            new Dictionary<uint, int> { [48301] = 1, [48302] = 1, [48303] = 1 });

        Assert.Equal(3, steps.Count);
        Assert.All(steps, step => Assert.Equal(1, step.Count));
        Assert.Equal(new uint[] { 36196, 36197, 36198 }, steps.Select(step => step.RecipeId).OrderBy(id => id));
    }

    /// <summary>A recipe nothing asks for is not made.</summary>
    [Fact]
    public void ARecipeNothingAsksFor_IsLeftOut()
    {
        var steps = CraftStep.Plan(
            new[] { Recipe(1, 100, (48233, 1)), Recipe(2, 999, (48233, 1)) },
            new Dictionary<uint, int> { [100] = 1 });

        Assert.Equal(new[] { new CraftStep(1, 1) }, steps);
    }
}
