using Selene.Features.Missions;

namespace Selene.Tests.Features.Missions;

/// <summary>
/// Re-rolling (the user, 2026-11-14: "need to figure out the rerolling missions for finishing golds"; the mix:
/// "Mix of both"): which mission is thrown away, how the chain mixes abandoning with running one, the cap,
/// and the run that accepts a mission only to abandon it.
/// </summary>
public class RerollTests
{
    private const uint Blacksmith = 9;
    private const ulong Mene = 0x0040000000000001UL;
    private static readonly MissionCategory[] NoTabs = Array.Empty<MissionCategory>();

    private DateTime _now = new(2026, 11, 14, 21, 0, 0);

    // ---- which mission is thrown away -------------------------------------------------------------------

    private static MissionInfo Mission(uint id, int tier, MissionCategory? special = null)
        => new(id, $"Mission {id}", Blacksmith, tier, false, 0, 1000, 2000, new uint[] { 9000 + id }, special);

    private static MissionRow Row(MissionInfo info, bool offered, bool gold, bool? locked = null)
        => new(info, false, offered, false, false, gold ? MissionBest.Gold : MissionBest.Completed, GameLocked: locked);

    private static Choice Choose(IReadOnlyList<MissionRow> rows, bool reroll = true, int salt = 0, Func<MissionInfo, bool>? runnable = null)
        => MissionChooser.Choose(ChainMode.GoldAll, rows, runnable ?? (_ => true), NoTabs, null, Mene, salt, reroll);

    /// <summary>The tick off: as before the re-roll was built - nothing wanted on offer, nothing chosen.</summary>
    [Fact]
    public void WithRerollOff_NothingWantedOnOffer_ChoosesNothing()
    {
        var rows = new[] { Row(Mission(1, 2), offered: false, gold: false), Row(Mission(2, 2), offered: true, gold: true) };

        var choice = Choose(rows, reroll: false);

        Assert.Equal(0u, choice.MissionId);
        Assert.False(choice.Reroll);
        Assert.Contains("offering none of them", choice.Why);
    }

    /// <summary>The spare is one on offer of the RANK that is wanted (abandoning re-draws that rank), not of another.</summary>
    [Fact]
    public void TheMissionThrownAway_IsOnOffer_AndOfTheRankThatIsWanted()
    {
        var rows = new[]
        {
            Row(Mission(1, 2), offered: false, gold: false), // C, wanted, not offered
            Row(Mission(2, 2), offered: true, gold: true),   // C, gold, offered: the spare
            Row(Mission(3, 1), offered: true, gold: true),   // D: nothing of D is wanted
            Row(Mission(4, 3), offered: true, gold: true),   // B: nothing of B is wanted
        };

        for (var salt = 0; salt < 20; salt++)
        {
            var choice = Choose(rows, salt: salt);
            Assert.True(choice.Reroll);
            Assert.Equal(2u, choice.MissionId);
            Assert.True(choice.CanRun);
        }
    }

    /// <summary>A wanted mission on offer is simply taken: no re-roll while there is something to do.</summary>
    [Fact]
    public void AWantedMissionOnOffer_IsTaken_NotReRolledFor()
    {
        var rows = new[] { Row(Mission(1, 2), offered: true, gold: false), Row(Mission(2, 2), offered: true, gold: true) };

        var choice = Choose(rows);

        Assert.Equal(1u, choice.MissionId);
        Assert.False(choice.Reroll);
    }

    /// <summary>A sequence, weather or timed mission comes by itself: there is nothing to turn the board over for.</summary>
    [Fact]
    public void OnlySpecialMissionsLeft_NothingIsReRolled()
    {
        var rows = new[]
        {
            Row(Mission(1, 2, MissionCategory.Weather), offered: false, gold: false),
            Row(Mission(2, 2), offered: true, gold: true),
        };

        var choice = Choose(rows);

        Assert.Equal(0u, choice.MissionId);
        Assert.False(choice.Reroll);
        Assert.Contains("comes by itself", choice.Why);
    }

    /// <summary>A mission the character cannot accept is no spare; with no spare of the rank the chain has nothing to turn.</summary>
    [Fact]
    public void ALockedMission_IsNotThrownAway()
    {
        var rows = new[] { Row(Mission(1, 2), offered: false, gold: false), Row(Mission(2, 2), offered: true, gold: true, locked: true) };

        var choice = Choose(rows);

        Assert.Equal(0u, choice.MissionId);
        Assert.Contains("nothing of their rank is on offer", choice.Why);
    }

    /// <summary>A spare Selene cannot run can still be abandoned - but not run in an abandon's place.</summary>
    [Fact]
    public void ASpareSeleneCannotRun_IsMarkedSo()
    {
        var rows = new[] { Row(Mission(1, 2), offered: false, gold: false), Row(Mission(2, 2), offered: true, gold: true) };

        var choice = Choose(rows, runnable: mission => mission.Id == 1);

        Assert.True(choice.Reroll);
        Assert.Equal(2u, choice.MissionId);
        Assert.False(choice.CanRun);
    }

    // ---- the chain: the mix and the cap -----------------------------------------------------------------

    private uint Tick(MissionChain chain, RunPhase run, Choice next, double seconds = 1)
    {
        _now = _now.AddSeconds(seconds);
        return chain.Tick(run, "run status", () => next, _now, Mene);
    }

    /// <summary>One turn of the board: the chain hands the mission out, the run comes and goes, the pause passes.</summary>
    private uint Turn(MissionChain chain, Choice next, RunPhase during)
    {
        var started = Tick(chain, RunPhase.Done, next, MissionChain.LongPause.Max + 1);
        if (started == 0)
            return 0;

        Tick(chain, during, next);
        Tick(chain, RunPhase.Done, next);
        return started;
    }

    [Fact]
    public void AReRoll_IsAnAbandon_AndIsNotCountedAsAMissionReported()
    {
        var chain = new MissionChain();
        chain.Start(ChainMode.GoldAll, 0, _now);
        var spare = new Choice(1403, "none on offer", Reroll: true, CanRun: false);

        Assert.Equal(1403u, Tick(chain, RunPhase.Idle, spare));
        Assert.True(chain.Discard);
        Assert.Equal(1, chain.RerollsInRow);

        Tick(chain, RunPhase.Abandoning, spare);
        Assert.StartsWith("Re-roll 1 of 8", chain.Status);
        Tick(chain, RunPhase.Done, spare);

        Assert.Equal(1, chain.Abandoned);
        Assert.Equal(0, chain.Completed);
        Assert.True(chain.Active);
    }

    /// <summary>The cap: a board that never brings what is wanted is not turned over for ever.</summary>
    [Fact]
    public void AfterTheCapOfReRollsInARow_TheChainStops()
    {
        var chain = new MissionChain();
        chain.Start(ChainMode.GoldAll, 0, _now);
        var spare = new Choice(1403, "none on offer", Reroll: true, CanRun: false);

        Assert.Equal(1403u, Tick(chain, RunPhase.Idle, spare));
        Tick(chain, RunPhase.Abandoning, spare);
        Tick(chain, RunPhase.Done, spare);
        for (var i = 1; i < MissionChain.MaxRerolls; i++)
            Assert.Equal(1403u, Turn(chain, spare, RunPhase.Abandoning));

        Assert.Equal(0u, Turn(chain, spare, RunPhase.Abandoning));
        Assert.False(chain.Active);
        Assert.Contains("turned over 8 times in a row", chain.Status);
        Assert.Contains("8 abandoned", chain.Status);
    }

    /// <summary>"Mix of both": never a long string of accept-and-abandon - a spare Selene can run is run in between.</summary>
    [Fact]
    public void TheMix_NeverAbandonsMoreThanAFewTimesOnEnd()
    {
        var chain = new MissionChain();
        chain.Start(ChainMode.GoldAll, 0, _now);
        var spare = new Choice(1403, "none on offer", Reroll: true, CanRun: true);

        var discards = new List<bool>();
        Assert.Equal(1403u, Tick(chain, RunPhase.Idle, spare));
        discards.Add(chain.Discard);
        Tick(chain, chain.Discard ? RunPhase.Abandoning : RunPhase.Crafting, spare);
        Tick(chain, RunPhase.Done, spare);
        for (var i = 1; i < MissionChain.MaxRerolls; i++)
        {
            var started = Tick(chain, RunPhase.Done, spare, MissionChain.LongPause.Max + 1);
            Assert.Equal(1403u, started);
            discards.Add(chain.Discard);
            Tick(chain, chain.Discard ? RunPhase.Abandoning : RunPhase.Crafting, spare);
            Tick(chain, RunPhase.Done, spare);
        }

        var onEnd = 0;
        foreach (var discard in discards)
        {
            onEnd = discard ? onEnd + 1 : 0;
            Assert.True(onEnd <= MissionChain.MaxAbandonsInRow, "more abandons on end than the mix allows");
        }

        Assert.Contains(false, discards); // at least one was run
        Assert.Equal(discards.Count(discard => discard), chain.Abandoned);
        Assert.Equal(discards.Count(discard => !discard), chain.Completed);
    }

    /// <summary>A wanted mission coming up ends the streak: the cap counts turns that brought nothing.</summary>
    [Fact]
    public void AWantedMission_ResetsTheCount()
    {
        var chain = new MissionChain();
        chain.Start(ChainMode.GoldAll, 0, _now);
        var spare = new Choice(1403, "none on offer", Reroll: true, CanRun: false);

        Tick(chain, RunPhase.Idle, spare);
        Tick(chain, RunPhase.Abandoning, spare);
        Tick(chain, RunPhase.Done, spare);
        Assert.Equal(1, chain.RerollsInRow);

        Assert.Equal(1402u, Turn(chain, new Choice(1402, "not gold yet"), RunPhase.Crafting));
        Assert.Equal(0, chain.RerollsInRow);
        Assert.False(chain.Discard);
        Assert.Equal(1, chain.Completed);
    }

    // ---- the run: accept, abandon -----------------------------------------------------------------------

    private const uint Spare = 1403;
    private static readonly uint[] SpareOnly = { Spare };
    private static readonly HephaestusStatus Idle = new(true, false, false, string.Empty, "None", 87, 20);

    private static RunObservation Obs(
        uint mission = 0, uint score = 0, bool board = false, uint[]? rows = null, uint selected = 0, uint confirm = 0, bool abandonAsked = false,
        HephaestusStatus? hephaestus = null)
        => new(
            new MissionState(true, 1319, Blacksmith, mission, score, MissionRank.None, new uint[] { Spare, 1404 }, 0, 0),
            hephaestus ?? Idle, false, board, rows, selected, confirm, abandonAsked);

    private RunCommand Tick(MissionRun run, RunObservation obs, double seconds = 3)
    {
        _now = _now.AddSeconds(seconds);
        return run.Tick(obs, _now);
    }

    /// <summary>The whole re-roll: board, select, initiate, confirm - then Abandon, its question, and the mission gone. Nothing is crafted.</summary>
    [Fact]
    public void ADiscard_AcceptsTheMission_AbandonsIt_AndCraftsNothing()
    {
        var run = new MissionRun();
        Assert.True(run.Start(Obs(), SpareOnly, _now, discard: true));
        Assert.True(run.Discard);
        var rows = new uint[] { Spare, 1404 };

        Assert.Equal(RunCommand.OpenBoard, Tick(run, Obs()));
        Assert.Equal(RunCommand.None, Tick(run, Obs(board: true, rows: rows)));
        Assert.Equal(RunCommand.None, Tick(run, Obs(board: true, rows: rows)));
        Assert.Equal(RunCommand.SelectMission, Tick(run, Obs(board: true, rows: rows)));
        Assert.Equal(RunCommand.None, Tick(run, Obs(board: true, rows: rows, selected: Spare)));
        Assert.Equal(RunCommand.InitiateMission, Tick(run, Obs(board: true, rows: rows, selected: Spare)));
        Assert.Equal(RunCommand.ConfirmMission, Tick(run, Obs(board: true, rows: rows, selected: Spare, confirm: Spare)));

        Assert.Equal(RunCommand.None, Tick(run, Obs(mission: Spare)));
        Assert.Equal(RunPhase.Abandoning, run.Phase);
        Assert.Equal(RunCommand.None, Tick(run, Obs(mission: Spare), 1)); // a moment first, as a person looks before abandoning

        Assert.Equal(RunCommand.Abandon, Tick(run, Obs(mission: Spare)));
        Assert.Equal(RunCommand.ConfirmAbandon, Tick(run, Obs(mission: Spare, abandonAsked: true)));

        Assert.Equal(RunCommand.None, Tick(run, Obs()));
        Assert.Equal(RunPhase.Done, run.Phase);
        Assert.Equal("Done — mission 1403 abandoned, to turn the board over.", run.Status);
    }

    /// <summary>Selene abandons only what it accepted to abandon: never a mission that was already going.</summary>
    [Fact]
    public void ADiscard_IsRefused_WithAMissionInProgress()
    {
        var run = new MissionRun();

        Assert.False(run.Start(Obs(mission: 1404, score: 500), SpareOnly, _now, discard: true));
        Assert.Contains("abandons only a mission it accepted to abandon", run.Status);
        Assert.False(run.Active);
    }

    /// <summary>Work is reported, never thrown away: a score on the mission and Abandon is not pressed.</summary>
    [Fact]
    public void ADiscard_NeverAbandonsAMissionWithAScore()
    {
        var run = new MissionRun();
        run.Start(Obs(), SpareOnly, _now, discard: true);
        Tick(run, Obs());
        Tick(run, Obs(mission: Spare));
        Assert.Equal(RunPhase.Abandoning, run.Phase);

        Assert.Equal(RunCommand.None, Tick(run, Obs(mission: Spare, score: 300), 10));
        Assert.Equal(RunPhase.Halted, run.Phase);
        Assert.Contains("has a score", run.Status);
    }

    /// <summary>Nothing is crafted on a re-roll, so a missing or busy Hephaestus does not stand in its way.</summary>
    [Fact]
    public void ADiscard_DoesNotNeedHephaestus()
    {
        var run = new MissionRun();

        Assert.True(run.Start(Obs(hephaestus: HephaestusStatus.Absent), SpareOnly, _now, discard: true));
    }

    /// <summary>The abandon question that never comes, or a press that never takes: a few tries, then a person takes over.</summary>
    [Fact]
    public void ADiscard_ThatCannotAbandon_Halts()
    {
        var run = new MissionRun();
        run.Start(Obs(), SpareOnly, _now, discard: true);
        Tick(run, Obs());
        Tick(run, Obs(mission: Spare));

        var presses = 0;
        for (var i = 0; i < 20 && run.Active; i++)
        {
            if (Tick(run, Obs(mission: Spare)) == RunCommand.Abandon)
                presses++;
        }

        Assert.Equal(MissionRun.ReportAttempts, presses);
        Assert.Equal(RunPhase.Halted, run.Phase);
        Assert.Contains("could not be abandoned", run.Status);
    }

    /// <summary>An ordinary run is no discard, and one started after a discard is not one either.</summary>
    [Fact]
    public void AnOrdinaryRun_IsNotADiscard()
    {
        var run = new MissionRun();
        run.Start(Obs(), SpareOnly, _now, discard: true);
        run.Stop(Obs());

        Assert.True(run.Start(Obs(), SpareOnly, _now));
        Assert.False(run.Discard);
    }
}
