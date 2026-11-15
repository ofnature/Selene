using System.Numerics;
using Selene.Features.Fishing;
using Selene.Features.Missions;
using Selene.Features.Repair;

namespace Selene.Tests.Features.Repair;

public class MenderTripTests
{
    private const ulong Mene = 0x0040000000000001UL;
    private static readonly Mender Godgyth = Menders.For(1319)!;
    private static readonly Vector3 Stand = new(315f, 205.6f, 375.5f);

    private DateTime _now = new(2026, 11, 15, 21, 0, 0);

    private static MenderObservation Obs(
        int gear = 19, float? distance = 80f, bool menu = false, bool repair = false, bool asked = false,
        WalkPhase walk = WalkPhase.Idle, string walkStatus = "", bool inWorld = true)
        => new(inWorld, gear, distance, menu, repair, asked, walk, walkStatus);

    private MenderCommand Tick(MenderTrip trip, MenderObservation obs, double seconds = 3)
    {
        _now = _now.AddSeconds(seconds);
        return trip.Tick(obs, _now);
    }

    private MenderTrip Started(MenderObservation? obs = null)
    {
        var trip = new MenderTrip();
        Assert.True(trip.Start(obs ?? Obs(), Godgyth, Stand, null, _now));
        return trip;
    }

    /// <summary>The whole road: walk, talk, the mender's entry, Repair All, the price, the gear read back, the windows closed.</summary>
    [Fact]
    public void ARepair_WalksToTheMender_Repairs_AndIsDoneWhenTheGearIsMended()
    {
        var trip = Started();
        Assert.Equal(MenderPhase.Walking, trip.Phase);

        Assert.Equal(MenderCommand.StartWalk, Tick(trip, Obs()));
        Assert.Equal(MenderCommand.None, Tick(trip, Obs(walk: WalkPhase.Asking)));
        Assert.Equal(MenderCommand.None, Tick(trip, Obs(distance: 40f, walk: WalkPhase.Walking)));
        Assert.Equal("Walking to the mender — 40 y away", trip.Status);

        Assert.Equal(MenderCommand.None, Tick(trip, Obs(distance: 3f, walk: WalkPhase.Done)));
        Assert.Equal(MenderPhase.Talking, trip.Phase);
        Assert.Equal(MenderCommand.Interact, Tick(trip, Obs(distance: 3f, walk: WalkPhase.Done)));

        Assert.Equal(MenderCommand.None, Tick(trip, Obs(distance: 3f, menu: true)));
        Assert.Equal(MenderPhase.Choosing, trip.Phase);
        Assert.Equal(MenderCommand.ChooseMender, Tick(trip, Obs(distance: 3f, menu: true)));

        Assert.Equal(MenderCommand.None, Tick(trip, Obs(distance: 3f, repair: true)));
        Assert.Equal(MenderPhase.Repairing, trip.Phase);
        Assert.Equal(MenderCommand.RepairAll, Tick(trip, Obs(distance: 3f, repair: true)));
        Assert.Equal(MenderCommand.ConfirmRepair, Tick(trip, Obs(distance: 3f, repair: true, asked: true)));

        Assert.Equal(MenderCommand.None, Tick(trip, Obs(gear: 100, distance: 3f, repair: true)));
        Assert.Equal(MenderPhase.Closing, trip.Phase);
        Assert.Equal(MenderCommand.Close, Tick(trip, Obs(gear: 100, distance: 3f, repair: true)));
        Assert.Equal(MenderCommand.Close, Tick(trip, Obs(gear: 100, distance: 3f, menu: true))); // the menu behind it

        Assert.Equal(MenderCommand.None, Tick(trip, Obs(gear: 100, distance: 3f)));
        Assert.Equal(MenderPhase.Done, trip.Phase);
        Assert.Equal("Done — gear repaired from 19% to 100%.", trip.Status);
    }

    /// <summary>Standing beside the mender already: no walk is asked for.</summary>
    [Fact]
    public void BesideTheMenderAlready_NoWalkIsMade()
    {
        var trip = Started(Obs(distance: 4f));

        Assert.Equal(MenderPhase.Talking, trip.Phase);
        Assert.Equal(MenderCommand.Interact, Tick(trip, Obs(distance: 4f)));
    }

    public static TheoryData<string, string> Refusals => new()
    {
        { "noworld", "no character in the world" },
        { "nomender", "no mender is known for this zone" },
        { "mission", "a mission is in progress" },
        { "mended", "nothing to repair" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void ARepair_IsRefused_WithTheReason(string what, string reason)
    {
        var trip = new MenderTrip();
        var obs = what switch
        {
            "noworld" => Obs(inWorld: false),
            "mended" => Obs(gear: 100),
            _ => Obs(),
        };

        Assert.False(trip.Start(
            obs, what == "nomender" ? null : Godgyth, Stand,
            what == "mission" ? "a mission is in progress: it is finished and reported first" : null, _now));
        Assert.Contains(reason, trip.Status);
        Assert.False(trip.Active);
    }

    /// <summary>The last trip's walk is still "Done" on the walk: it is not taken for this trip's arrival.</summary>
    [Fact]
    public void AnOldWalksVerdict_IsNotTakenForThisTrips()
    {
        var trip = Started();

        Assert.Equal(MenderCommand.StartWalk, Tick(trip, Obs(walk: WalkPhase.Done)));
        Assert.Equal(MenderPhase.Walking, trip.Phase);
    }

    [Fact]
    public void AWalkThatFails_HaltsTheRepair_WithWhatTheWalkSaid()
    {
        var trip = Started();
        Tick(trip, Obs());
        Tick(trip, Obs(walk: WalkPhase.Walking));

        Assert.Equal(MenderCommand.None, Tick(trip, Obs(walk: WalkPhase.Halted, walkStatus: "Halted — the mesh reaches no point.")));
        Assert.Equal(MenderPhase.Halted, trip.Phase);
        Assert.Contains("the mesh reaches no point", trip.Status);
    }

    /// <summary>A walk that never starts (Ariadne not loaded): asked a few times, then a person takes over.</summary>
    [Fact]
    public void AWalkThatNeverStarts_Halts()
    {
        var trip = Started();

        var asked = 0;
        for (var i = 0; i < 12 && trip.Active; i++)
        {
            if (Tick(trip, Obs(walkStatus: "Not started — Ariadne is not loaded.")) == MenderCommand.StartWalk)
                asked++;
        }

        Assert.Equal(MenderTrip.MaxAttempts, asked);
        Assert.Contains("did not start (Not started — Ariadne is not loaded.)", trip.Status);
    }

    /// <summary>Too far to talk: nothing is pressed at a mender out of reach.</summary>
    [Fact]
    public void AMenderOutOfReach_IsNotTalkedTo()
    {
        var trip = Started();
        Tick(trip, Obs());
        Tick(trip, Obs(walk: WalkPhase.Walking));
        Tick(trip, Obs(distance: 9f, walk: WalkPhase.Done));

        Assert.Equal(MenderCommand.None, Tick(trip, Obs(distance: 9f, walk: WalkPhase.Done)));
        Assert.Equal(MenderPhase.Halted, trip.Phase);
        Assert.Contains("too far to talk to", trip.Status);
    }

    /// <summary>The condition is the verdict: a repair that never mends the gear (no gil) is not called done.</summary>
    [Fact]
    public void GearThatDoesNotMend_Halts()
    {
        var trip = Started(Obs(distance: 3f));
        Tick(trip, Obs(distance: 3f));
        Tick(trip, Obs(distance: 3f, menu: true));
        Tick(trip, Obs(distance: 3f, menu: true));
        Tick(trip, Obs(distance: 3f, repair: true));

        var presses = 0;
        for (var i = 0; i < 20 && trip.Active; i++)
        {
            if (Tick(trip, Obs(distance: 3f, repair: true)) == MenderCommand.RepairAll)
                presses++;
        }

        Assert.Equal(MenderTrip.RepairAttempts, presses);
        Assert.Equal(MenderPhase.Halted, trip.Phase);
        Assert.Contains("did not repair", trip.Status);
    }

    [Fact]
    public void Stop_EndsTheRepair_AndStopsAWalkInProgress()
    {
        var trip = Started();
        Tick(trip, Obs());
        Tick(trip, Obs(walk: WalkPhase.Walking));

        Assert.Equal(MenderCommand.StopWalk, trip.Stop(Obs(walk: WalkPhase.Walking)));
        Assert.False(trip.Active);
        Assert.Equal(MenderCommand.None, trip.Stop(Obs()));
    }

    /// <summary>Each of the four stars has its mender; anywhere else there is none.</summary>
    [Theory]
    [InlineData(1237u, 1052610u)]
    [InlineData(1291u, 1052641u)]
    [InlineData(1310u, 1052651u)]
    [InlineData(1319u, 1056825u)]
    public void EachStar_HasItsMender_WithinReachOfItsStandingSpot(uint territory, uint npc)
    {
        var mender = Menders.For(territory);

        Assert.NotNull(mender);
        Assert.Equal(npc, mender!.NpcId);
        Assert.InRange(Vector3.Distance(mender.At, mender.StandNear), 1f, MenderTrip.TalkWithin - 1.8f);
        Assert.Null(Menders.For(129));
    }

    // ---- the chain waits for the mender ----------------------------------------------------------------

    /// <summary>A mission is due and the gear is worn: nothing is chosen while the chain is told to wait, and it goes on after.</summary>
    [Fact]
    public void AChain_WaitsWhileItIsHeld_AndTakesTheNextMissionAfter()
    {
        var chain = new MissionChain();
        chain.Start(ChainMode.Ticked, 0, _now);
        var chosen = 0;
        Choice Choose()
        {
            chosen++;
            return new Choice(1370, "ticked");
        }

        _now = _now.AddSeconds(1);
        Assert.Equal(0u, chain.Tick(RunPhase.Idle, "run", Choose, _now, Mene, () => "gear at 19%: going to the mender"));
        Assert.Equal(0, chosen);
        Assert.Contains("going to the mender", chain.Status);
        Assert.True(chain.Active);

        _now = _now.AddSeconds(1);
        Assert.Equal(1370u, chain.Tick(RunPhase.Idle, "run", Choose, _now, Mene, () => null));
        Assert.Equal(1, chosen);
    }

    /// <summary>The hold may end the chain (the repair failed): nothing is chosen, and its reason stands.</summary>
    [Fact]
    public void AHoldThatStopsTheChain_ChoosesNothing()
    {
        var chain = new MissionChain();
        chain.Start(ChainMode.Ticked, 0, _now);

        _now = _now.AddSeconds(1);
        var started = chain.Tick(RunPhase.Idle, "run", () => new Choice(1370, "ticked"), _now, Mene, () =>
        {
            chain.Stop("Stopped — the repair did not mend it.");
            return null;
        });

        Assert.Equal(0u, started);
        Assert.False(chain.Active);
        Assert.StartsWith("Stopped — the repair did not mend it.", chain.Status);
    }

    /// <summary>The hold is asked only when a mission is due - not while one is running.</summary>
    [Fact]
    public void TheHold_IsNotAskedWhileAMissionRuns()
    {
        var chain = new MissionChain();
        chain.Start(ChainMode.Ticked, 0, _now);
        var asked = 0;

        _now = _now.AddSeconds(1);
        chain.Tick(RunPhase.Crafting, "run", () => new Choice(1370, "ticked"), _now, Mene, () =>
        {
            asked++;
            return null;
        });

        Assert.Equal(0, asked);
    }
}
