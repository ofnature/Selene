using Selene.Features.Missions;

namespace Selene.Tests.Features.Missions;

public class MissionChainTests
{
    private const uint Carpenter = 8;
    private const ulong Mene = 0x0040000000000001UL;
    private static readonly MissionCategory[] NoTabs = Array.Empty<MissionCategory>();

    private DateTime _now = new(2026, 11, 14, 21, 0, 0);

    // ---- the chooser ------------------------------------------------------------------------------------

    private static MissionInfo Mission(uint id, int tier = 1, bool master = false, params (int Kind, int Amount)[] toolData)
        => new(id, $"Mission {id}", Carpenter, tier, master, 0, 1000, 2000, new uint[] { 9000 + id },
            Reward: new MissionReward(1, 1, 0, toolData));

    private static MissionRow Row(
        MissionInfo info, bool offered = true, bool picked = false, MissionBest best = MissionBest.None, bool bonus = false, PayoutSummary? payout = null)
        => new(info, picked, offered, bonus, false, best, Payout: payout);

    private static PayoutSummary Paid(uint mission, int points, int seconds)
        => PayoutSummary.Summarise(new[] { new PayoutSample(new DateTime(2026, 11, 14), mission, Carpenter, 3, 0, points, null, seconds, 0) })[mission];

    private static Choice Choose(ChainMode mode, IReadOnlyList<MissionRow> rows, ToolState? tool = null, MissionCategory[]? tabs = null, int salt = 0)
        => MissionChooser.Choose(mode, rows, _ => true, tabs ?? NoTabs, tool, Mene, salt);

    [Fact]
    public void Ticked_TakesOnlyWhatIsTickedAndOnOffer()
    {
        var rows = new[] { Row(Mission(1), picked: true, offered: false), Row(Mission(2), picked: true), Row(Mission(3)) };

        Assert.Equal(2u, Choose(ChainMode.Ticked, rows).MissionId);
    }

    /// <summary>"Tick whole tabs": whatever the game offers of that kind, ticked or not.</summary>
    [Fact]
    public void Ticked_TakesWhateverIsOnOffer_OnATabTickedAsAWhole()
    {
        var rows = new[] { Row(Mission(1, tier: 1)), Row(Mission(2, tier: 3)) };

        Assert.Equal(2u, Choose(ChainMode.Ticked, rows, tabs: new[] { MissionCategory.RankB }).MissionId);
    }

    /// <summary>No re-rolling: with nothing wanted on offer the answer is "none", and it is not called finished.</summary>
    [Fact]
    public void Ticked_WithNothingWantedOnOffer_ChoosesNothing()
    {
        var choice = Choose(ChainMode.Ticked, new[] { Row(Mission(1)) });

        Assert.Equal(0u, choice.MissionId);
        Assert.False(choice.Finished);
    }

    /// <summary>Not the same order every evening: over many turns more than one of the ticked missions is taken.</summary>
    [Fact]
    public void Ticked_VariesItsPickAmongSeveral()
    {
        var rows = Enumerable.Range(1, 5).Select(id => Row(Mission((uint)id), picked: true)).ToList();

        var taken = Enumerable.Range(0, 40).Select(salt => Choose(ChainMode.Ticked, rows, salt: salt).MissionId).Distinct().Count();

        Assert.True(taken > 1);
    }

    [Fact]
    public void GoldAll_TakesTheLowestRankNotGoldYet_AndLeavesGoldOnesAlone()
    {
        var rows = new[]
        {
            Row(Mission(1, tier: 1), best: MissionBest.Gold),
            Row(Mission(2, tier: 3), best: MissionBest.Completed),
            Row(Mission(3, tier: 2), best: MissionBest.None),
            Row(Mission(4, tier: 6, master: true), best: MissionBest.None),
        };

        Assert.Equal(3u, Choose(ChainMode.GoldAll, rows).MissionId);
    }

    [Fact]
    public void GoldAll_IsFinished_WhenEverythingIsGold()
    {
        var rows = new[] { Row(Mission(1), best: MissionBest.Gold), Row(Mission(2), offered: false, best: MissionBest.Gold) };

        var choice = Choose(ChainMode.GoldAll, rows);

        Assert.Equal(0u, choice.MissionId);
        Assert.True(choice.Finished);
    }

    /// <summary>Something is left but the game is not offering it: nothing is chosen, and it is NOT finished.</summary>
    [Fact]
    public void GoldAll_WithWhatIsLeftNotOnOffer_ChoosesNothing()
    {
        var rows = new[] { Row(Mission(1), best: MissionBest.Gold), Row(Mission(2), offered: false, best: MissionBest.None) };

        var choice = Choose(ChainMode.GoldAll, rows);

        Assert.Equal(0u, choice.MissionId);
        Assert.False(choice.Finished);
        Assert.Contains("1 mission(s) are not gold yet", choice.Why);
    }

    /// <summary>The tool is short of kind IV only: the mission that pays IV wins over one that pays more of what is full.</summary>
    [Fact]
    public void LevelTool_TakesWhatPaysTheKindsTheToolLacks()
    {
        var tool = new ToolState(10, 20, new[] { new ToolKind(1, 3450, 3450, 4600), new ToolKind(4, 1000, 2250, 3800) });
        var rows = new[] { Row(Mission(1, 1, false, (1, 50))), Row(Mission(2, 2, false, (4, 12), (1, 6))) };

        Assert.Equal(2u, Choose(ChainMode.LevelTool, rows, tool).MissionId);
    }

    [Fact]
    public void LevelTool_IsFinished_WhenTheToolHasAllItsStageAsksFor()
    {
        var tool = new ToolState(10, 20, new[] { new ToolKind(1, 3450, 3450, 4600) });

        var choice = Choose(ChainMode.LevelTool, new[] { Row(Mission(1, 1, false, (1, 8))) }, tool);

        Assert.True(choice.Finished);
        Assert.Contains("hand it in", choice.Why);
    }

    /// <summary>At the top stage a star allows there is no hand-in: it keeps banking until the stage is full.</summary>
    [Fact]
    public void LevelTool_AtTheStarsTopStage_BanksUntilFull()
    {
        var banking = new ToolState(20, 20, new[] { new ToolKind(1, 9000, 9500, 9500) });
        var full = new ToolState(20, 20, new[] { new ToolKind(1, 9500, 9500, 9500) });
        var rows = new[] { Row(Mission(1, 1, false, (1, 8))) };

        Assert.Equal(1u, Choose(ChainMode.LevelTool, rows, banking).MissionId);
        Assert.True(Choose(ChainMode.LevelTool, rows, full).Finished);
    }

    [Fact]
    public void ClassPoints_TriesAnUnmeasuredMasterFirst_ThenTheBestAMinute()
    {
        var slow = Row(Mission(1395, 6, true), payout: Paid(1395, 542, 300));
        var fast = Row(Mission(1396, 6, true), payout: Paid(1396, 433, 120));
        var unknown = Row(Mission(1397, 6, true));
        var ranked = Row(Mission(1370), picked: true);

        Assert.Equal(1397u, Choose(ChainMode.ClassPoints, new[] { slow, fast, unknown, ranked }).MissionId);
        Assert.Equal(1396u, Choose(ChainMode.ClassPoints, new[] { slow, fast, ranked }).MissionId);
    }

    // ---- the chain --------------------------------------------------------------------------------------

    private uint Tick(MissionChain chain, RunPhase run, Choice next, double seconds = 1)
    {
        _now = _now.AddSeconds(seconds);
        return chain.Tick(run, "run status", () => next, _now, Mene);
    }

    [Fact]
    public void AChain_StartsAMission_WaitsForIt_PausesAndStartsTheNext()
    {
        var chain = new MissionChain();
        chain.Start(ChainMode.Ticked, 0, _now);

        Assert.Equal(1370u, Tick(chain, RunPhase.Idle, new Choice(1370, "ticked")));
        Assert.Equal(0u, Tick(chain, RunPhase.Crafting, new Choice(1371, "ticked")));
        Assert.Equal(0u, Tick(chain, RunPhase.Reporting, new Choice(1371, "ticked")));

        // Reported: counted once, and the next one is not taken at once.
        Assert.Equal(0u, Tick(chain, RunPhase.Done, new Choice(1371, "ticked")));
        Assert.Equal(1, chain.Completed);
        Assert.Equal(0u, Tick(chain, RunPhase.Done, new Choice(1371, "ticked")));
        Assert.Equal(1, chain.Completed);

        Assert.Equal(1371u, Tick(chain, RunPhase.Done, new Choice(1371, "ticked"), MissionChain.LongPause.Max + 1));
        Assert.True(chain.Active);
    }

    /// <summary>The wait between two missions is inside the two ranges and is not one fixed number.</summary>
    [Fact]
    public void ThePauseBetweenMissions_Varies()
    {
        var waits = new HashSet<int>();
        for (var i = 0; i < 30; i++)
        {
            var chain = new MissionChain();
            chain.Start(ChainMode.Ticked, 0, _now);
            Tick(chain, RunPhase.Idle, new Choice(1, "x"));
            Tick(chain, RunPhase.Crafting, new Choice(1, "x"));
            Tick(chain, RunPhase.Done, new Choice(1, "x"), 7 + i);

            var seconds = int.Parse(chain.Status.Split(" in ")[1].Split(' ')[0]);
            Assert.InRange(seconds, MissionChain.Pause.Min, MissionChain.LongPause.Max);
            waits.Add(seconds);
        }

        Assert.True(waits.Count > 3);
    }

    [Fact]
    public void AChain_StopsAtItsLimit()
    {
        var chain = new MissionChain();
        chain.Start(ChainMode.Ticked, 1, _now);
        Tick(chain, RunPhase.Idle, new Choice(1370, "ticked"));
        Tick(chain, RunPhase.Crafting, new Choice(1371, "ticked"));

        Tick(chain, RunPhase.Done, new Choice(1371, "ticked"));

        Assert.False(chain.Active);
        Assert.Contains("the number of missions asked for", chain.Status);
        Assert.Contains("(1 mission reported)", chain.Status);
    }

    /// <summary>A run that halts is a person's to look at: the chain does not go on to the next mission.</summary>
    [Fact]
    public void AChain_Stops_WhenAMissionsRunHalts()
    {
        var chain = new MissionChain();
        chain.Start(ChainMode.Ticked, 0, _now);
        Tick(chain, RunPhase.Idle, new Choice(1370, "ticked"));
        Tick(chain, RunPhase.Crafting, new Choice(1371, "ticked"));

        Assert.Equal(0u, Tick(chain, RunPhase.Halted, new Choice(1371, "ticked")));

        Assert.False(chain.Active);
        Assert.Equal(0, chain.Completed);
        Assert.Contains("halted", chain.Status);
    }

    [Theory]
    [InlineData(false, "Stopped — ")]
    [InlineData(true, "Done — ")]
    public void AChain_Ends_WhenThereIsNothingToTake(bool finished, string says)
    {
        var chain = new MissionChain();
        chain.Start(ChainMode.GoldAll, 0, _now);

        Assert.Equal(0u, Tick(chain, RunPhase.Idle, new Choice(0, "nothing on offer", finished)));

        Assert.False(chain.Active);
        Assert.StartsWith(says, chain.Status);
    }

    [Fact]
    public void AfterThisOne_LetsTheMissionFinish_ThenEnds()
    {
        var chain = new MissionChain();
        chain.Start(ChainMode.Ticked, 0, _now);
        Tick(chain, RunPhase.Idle, new Choice(1370, "ticked"));
        Tick(chain, RunPhase.Crafting, new Choice(1371, "ticked"));

        chain.StopAfterThis();
        Assert.Equal(0u, Tick(chain, RunPhase.Crafting, new Choice(1371, "ticked")));
        Assert.True(chain.Active);

        Tick(chain, RunPhase.Done, new Choice(1371, "ticked"));
        Assert.False(chain.Active);
        Assert.Equal(1, chain.Completed);
    }

    // ---- the tool ---------------------------------------------------------------------------------------

    /// <summary>Data of a kind the stage has enough of is worth nothing while another kind is still short.</summary>
    [Fact]
    public void ToolData_IsWorthWhatTheToolIsShortOf()
    {
        var tool = new ToolState(10, 20, new[] { new ToolKind(1, 3450, 3450, 4600), new ToolKind(4, 0, 2250, 3800) });

        Assert.Equal(0.0, tool.Worth(new MissionReward(0, 0, 0, new[] { (1, 50) })));
        Assert.Equal(12.0, tool.Worth(new MissionReward(0, 0, 0, new[] { (4, 12) })));
        Assert.False(tool.ReadyToHandIn);
    }
}
