using System.Numerics;
using Selene.Features.Fishing;

namespace Selene.Tests.Features.Fishing;

public class StandWalkTests
{
    private static readonly Vector3 Start = new(0f, 0f, 0f);
    private static readonly Vector3 Place = new(18f, 0f, 0f);
    private static readonly Vector3 OnMesh = new(17.5f, 0f, 0f);
    private const float Water = MathF.PI / 2f;

    private DateTime _now = new(2026, 11, 14, 21, 0, 0);

    private static WalkObservation Obs(
        Vector3? at, bool busy = false, ReachState reach = ReachState.NotAsked, float rotation = 0f, bool ready = true, string said = "")
        => new(at, rotation, ready, busy, said, reach, OnMesh);

    private WalkCommand Tick(StandWalk walk, WalkObservation obs, double seconds = 3)
    {
        _now = _now.AddSeconds(seconds);
        return walk.Tick(obs, _now);
    }

    private StandWalk Started()
    {
        var walk = new StandWalk();
        Assert.True(walk.Start(Obs(Start), Place, Water, _now));
        return walk;
    }

    /// <summary>The whole road: the mesh is asked, the walk goes to the mesh's own point, the character turns, done.</summary>
    [Fact]
    public void TheMeshIsAsked_ThenTheCharacterWalksThere_AndTurnsToTheWater()
    {
        var walk = Started();

        Assert.Equal(WalkCommand.AskReach, Tick(walk, Obs(Start)));
        Assert.Equal(WalkCommand.None, Tick(walk, Obs(Start, reach: ReachState.Pending)));
        Assert.Equal(WalkPhase.Asking, walk.Phase);

        Assert.Equal(WalkCommand.None, Tick(walk, Obs(Start, reach: ReachState.Found)));
        Assert.Equal(WalkPhase.Starting, walk.Phase);
        Assert.Equal(OnMesh, walk.Goal);

        Assert.Equal(WalkCommand.Move, Tick(walk, Obs(Start, reach: ReachState.Found)));
        Assert.Equal(WalkCommand.None, Tick(walk, Obs(new Vector3(5f, 0f, 0f), busy: true)));
        Assert.Equal(WalkPhase.Walking, walk.Phase);
        Tick(walk, Obs(new Vector3(9f, 0f, 0f), busy: true));
        Assert.Equal("Walking — 8.5 y to go", walk.Status);

        Assert.Equal(WalkCommand.None, Tick(walk, Obs(OnMesh)));
        Assert.Equal(WalkPhase.Facing, walk.Phase);
        Assert.Equal(WalkCommand.Face, Tick(walk, Obs(OnMesh)));

        Assert.Equal(WalkCommand.None, Tick(walk, Obs(OnMesh, rotation: Water)));
        Assert.Equal(WalkPhase.Done, walk.Phase);
        Assert.StartsWith("Done — standing 0.5 y from the place chosen", walk.Status);
        Assert.False(walk.Active);
    }

    /// <summary>The point of asking first: a place the mesh does not reach is refused before a step is taken.</summary>
    [Fact]
    public void APlaceTheMeshDoesNotReach_IsRefused_WithoutAStep()
    {
        var walk = Started();
        Tick(walk, Obs(Start));

        Assert.Equal(WalkCommand.None, Tick(walk, Obs(Start, reach: ReachState.None)));

        Assert.Equal(WalkPhase.Halted, walk.Phase);
        Assert.Contains("cannot be walked to", walk.Status);
    }

    [Theory]
    [InlineData("nobody", "no character in the world")]
    [InlineData("notready", "Ariadne is not loaded, or has no mesh")]
    [InlineData("moving", "already moving")]
    public void AWalk_IsNotStarted_WithoutWhatItNeeds(string what, string says)
    {
        var obs = what switch
        {
            "nobody" => Obs(null),
            "notready" => Obs(Start, ready: false),
            _ => Obs(Start, busy: true),
        };
        var walk = new StandWalk();

        Assert.False(walk.Start(obs, Place, Water, _now));

        Assert.False(walk.Active);
        Assert.Contains(says, walk.Status);
        Assert.Equal(WalkCommand.None, Tick(walk, obs));
    }

    /// <summary>Where the character stands is the verdict, not what the navigation says.</summary>
    [Fact]
    public void AWalkThatEndsShort_Halts_AndSaysHowFarAndWhatAriadneSaid()
    {
        var walk = Started();
        Tick(walk, Obs(Start));
        Tick(walk, Obs(Start, reach: ReachState.Found));
        Tick(walk, Obs(Start, reach: ReachState.Found));
        Tick(walk, Obs(new Vector3(5f, 0f, 0f), busy: true));

        Tick(walk, Obs(new Vector3(12f, 0f, 0f), said: "stuck (5.5y short)"));

        Assert.Equal(WalkPhase.Halted, walk.Phase);
        Assert.Contains("5.5 y short", walk.Status);
        Assert.Contains("stuck (5.5y short)", walk.Status);
    }

    [Fact]
    public void AriadneThatNeverStarts_IsAskedThreeTimes_ThenTheWalkHalts()
    {
        var walk = Started();
        Tick(walk, Obs(Start));
        Tick(walk, Obs(Start, reach: ReachState.Found));
        var asked = 0;

        for (var i = 0; i < 12 && walk.Active; i++)
        {
            if (Tick(walk, Obs(Start, reach: ReachState.Found), 1) == WalkCommand.Move)
                asked++;
        }

        Assert.Equal(StandWalk.MaxAttempts, asked);
        Assert.Equal(WalkPhase.Halted, walk.Phase);
        Assert.Contains("did not start walking", walk.Status);
    }

    [Fact]
    public void AWalkThatTakesTooLong_IsStopped()
    {
        var walk = Started();
        Tick(walk, Obs(Start));
        Tick(walk, Obs(Start, reach: ReachState.Found));
        Tick(walk, Obs(Start, reach: ReachState.Found));
        Tick(walk, Obs(new Vector3(1f, 0f, 0f), busy: true));

        var command = Tick(walk, Obs(new Vector3(2f, 0f, 0f), busy: true), StandWalk.WalkDeadline.TotalSeconds + 1);

        Assert.Equal(WalkCommand.Stop, command);
        Assert.Equal(WalkPhase.Halted, walk.Phase);
    }

    [Fact]
    public void Stop_EndsTheWalk_AndStopsAriadneIfItIsMoving()
    {
        var walk = Started();
        Tick(walk, Obs(Start));
        Tick(walk, Obs(Start, reach: ReachState.Found));
        Tick(walk, Obs(Start, reach: ReachState.Found));
        Tick(walk, Obs(new Vector3(5f, 0f, 0f), busy: true));

        Assert.Equal(WalkCommand.Stop, walk.Stop(Obs(new Vector3(6f, 0f, 0f), busy: true)));

        Assert.Equal(WalkPhase.Halted, walk.Phase);
        Assert.Contains("Stopped by you", walk.Status);
        Assert.Equal(WalkCommand.None, walk.Stop(Obs(new Vector3(6f, 0f, 0f))));
    }

    /// <summary>Facing is the short way round: a little past a full turn is still "facing it".</summary>
    [Fact]
    public void ARotationAFullTurnAway_CountsAsFacing()
    {
        var walk = Started();
        Tick(walk, Obs(Start));
        Tick(walk, Obs(Start, reach: ReachState.Found));
        Tick(walk, Obs(Start, reach: ReachState.Found));
        Tick(walk, Obs(new Vector3(5f, 0f, 0f), busy: true));
        Tick(walk, Obs(OnMesh));

        Tick(walk, Obs(OnMesh, rotation: Water - MathF.Tau + 0.05f));

        Assert.Equal(WalkPhase.Done, walk.Phase);
    }
}
