using Selene.Features.Missions;

namespace Selene.Tests.Features.Missions;

/// <summary>
/// Waiting for the missions the game offers only at certain hours or in certain weather (the user,
/// 2026-11-15, asked whether a Gold all chain should wait for them: "yes but only is those missions are part
/// of the acheviement" - a star's gold achievement asks for every mission on it).
/// </summary>
public class WaitTests
{
    private const uint Blacksmith = 9;
    private const ulong Mene = 0x0040000000000001UL;
    private static readonly MissionCategory[] NoTabs = Array.Empty<MissionCategory>();

    private DateTime _now = new(2026, 11, 15, 21, 0, 0);

    private static MissionInfo Mission(uint id, int tier, MissionCategory? special = null, MissionWindow? window = null)
        => new(id, $"Mission {id}", Blacksmith, tier, false, 0, 1000, 2000, new uint[] { 9000 + id }, special, Window: window);

    private static MissionRow Row(MissionInfo info, bool offered, bool gold)
        => new(info, false, offered, false, false, gold ? MissionBest.Gold : MissionBest.Completed);

    private static Choice Choose(IReadOnlyList<MissionRow> rows, bool reroll = true, bool wait = true, DateTime? utcNow = null)
        => MissionChooser.Choose(ChainMode.GoldAll, rows, _ => true, NoTabs, null, Mene, 0, reroll, wait, utcNow);

    private static readonly MissionInfo Timed = Mission(1419, 5, MissionCategory.Timed, new MissionWindow(8, 12, string.Empty));
    private static readonly MissionInfo Weather = Mission(1421, 6, MissionCategory.Weather, new MissionWindow(0, 0, "Clouds"));

    // ---- the Eorzean clock ------------------------------------------------------------------------------

    /// <summary>An Eorzean hour is 175 real seconds, counted from the Unix epoch.</summary>
    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(175 * 8, 8.0)]
    [InlineData(175 * 24, 0.0)]
    [InlineData((175 * 24 * 3) + (175 * 13) + 87, 13.497)]
    public void TheEorzeanHour_ComesFromTheRealClock(int unixSeconds, double hour)
        => Assert.Equal(hour, EorzeaClock.Hour(DateTime.UnixEpoch.AddSeconds(unixSeconds)), 2);

    [Theory]
    [InlineData(6.0, 8, 12, 6)]    // two Eorzean hours = 350 s, rounded up
    [InlineData(8.0, 8, 12, 0)]    // open
    [InlineData(11.9, 8, 12, 0)]
    [InlineData(12.0, 8, 12, 59)]  // closed again: twenty hours on
    [InlineData(23.0, 20, 24, 0)]
    [InlineData(1.0, 20, 24, 56)]
    public void MinutesUntilTheHours_AreRealMinutes(double eorzeanHour, int start, int end, int minutes)
        => Assert.Equal(minutes, EorzeaClock.MinutesUntil(DateTime.UnixEpoch.AddSeconds(eorzeanHour * 175), start, end));

    // ---- the choice -------------------------------------------------------------------------------------

    /// <summary>What is left comes with the hours or the weather: wait, and say for what and when.</summary>
    [Fact]
    public void WithOnlyTimedAndWeatherMissionsLeft_TheChoiceIsToWait_NamingThem()
    {
        var rows = new[] { Row(Timed, offered: false, gold: false), Row(Weather, offered: false, gold: false), Row(Mission(2, 2), offered: true, gold: true) };

        var choice = Choose(rows, utcNow: DateTime.UnixEpoch.AddSeconds(6 * 175));

        Assert.Equal(0u, choice.MissionId);
        Assert.True(choice.Wait);
        Assert.False(choice.Finished);
        Assert.Contains("Mission 1419 (hours 8-12, in 6 min)", choice.Why);
        Assert.Contains("Mission 1421 (in Clouds)", choice.Why);
    }

    /// <summary>The tick off: it stops there, as it did before.</summary>
    [Fact]
    public void WithWaitingOff_TheChainIsToldToStop()
    {
        var rows = new[] { Row(Timed, offered: false, gold: false) };

        var choice = Choose(rows, wait: false);

        Assert.False(choice.Wait);
        Assert.Contains("comes by itself", choice.Why);
    }

    /// <summary>The hours have come and the game offers it: it is simply taken.</summary>
    [Fact]
    public void ATimedMissionOnOffer_IsTaken()
    {
        var rows = new[] { Row(Timed, offered: true, gold: false) };

        var choice = Choose(rows);

        Assert.Equal(1419u, choice.MissionId);
        Assert.False(choice.Wait);
    }

    /// <summary>A ranked mission left and a spare on offer: the board is turned over first; waiting is for when nothing else can be done.</summary>
    [Fact]
    public void ReRollingComesBeforeWaiting()
    {
        var rows = new[]
        {
            Row(Timed, offered: false, gold: false),
            Row(Mission(1, 2), offered: false, gold: false),
            Row(Mission(2, 2), offered: true, gold: true),
        };

        var choice = Choose(rows);

        Assert.True(choice.Reroll);
        Assert.False(choice.Wait);
    }

    /// <summary>A sequence mission does not come with the clock: with only those left there is nothing to wait for.</summary>
    [Fact]
    public void ASequenceMission_IsNotWaitedFor()
    {
        var rows = new[] { Row(Mission(1, 2, MissionCategory.Sequence), offered: false, gold: false) };

        Assert.False(Choose(rows).Wait);
    }

    // ---- the chain --------------------------------------------------------------------------------------

    private uint Tick(MissionChain chain, RunPhase run, Func<Choice> choose, double seconds = 1)
    {
        _now = _now.AddSeconds(seconds);
        return chain.Tick(run, "run status", choose, _now, Mene);
    }

    /// <summary>While waiting the chain stays on, looks again only now and then, and takes the mission when it comes.</summary>
    [Fact]
    public void AChain_ThatWaits_StaysOn_LooksAgainNowAndThen_AndTakesWhatComes()
    {
        var chain = new MissionChain();
        chain.Start(ChainMode.GoldAll, 0, _now);
        var looks = 0;
        var offered = false;
        Choice Look()
        {
            looks++;
            return offered ? new Choice(1419, "not gold yet (1 left)") : new Choice(0, "waiting for 1 of the 1 left", Wait: true);
        }

        Assert.Equal(0u, Tick(chain, RunPhase.Idle, Look));
        Assert.True(chain.Active);
        Assert.True(chain.Waiting);
        Assert.Contains("waiting for 1 of the 1 left", chain.Status);

        // Not every tick: nothing is asked again before the shortest wait is over.
        for (var i = 0; i < MissionChain.WaitLook.Min - 2; i++)
            Assert.Equal(0u, Tick(chain, RunPhase.Idle, Look));
        Assert.Equal(1, looks);
        Assert.Contains("waiting for 1 of the 1 left", chain.Status);

        Assert.Equal(0u, Tick(chain, RunPhase.Idle, Look, MissionChain.WaitLook.Max));
        Assert.Equal(2, looks);

        offered = true;
        Assert.Equal(1419u, Tick(chain, RunPhase.Idle, Look, MissionChain.WaitLook.Max + 1));
        Assert.False(chain.Waiting);
    }

    /// <summary>"After this one" while nothing is running: the wait ends.</summary>
    [Fact]
    public void StopAfterThis_EndsAWait()
    {
        var chain = new MissionChain();
        chain.Start(ChainMode.GoldAll, 0, _now);
        Choice Look() => new(0, "waiting", Wait: true);

        Tick(chain, RunPhase.Idle, Look);
        chain.StopAfterThis();

        Assert.Equal(0u, Tick(chain, RunPhase.Idle, Look, MissionChain.WaitLook.Max + 1));
        Assert.False(chain.Active);
        Assert.StartsWith("Stopped while waiting", chain.Status);
    }
}
