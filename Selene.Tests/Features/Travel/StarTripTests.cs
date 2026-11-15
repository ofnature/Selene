using System.Text.Json;
using Selene.Features.Travel;

namespace Selene.Tests.Features.Travel;

public class StarTripTests
{
    private const uint Auxesia = 1319;
    private const uint Phaenna = 1291;
    private const uint Limsa = 129;
    private const uint BestwaysBurrow = 959;

    private DateTime _now = new(2026, 11, 14, 21, 0, 0);

    private static TripObservation Obs(
        uint territory = Limsa, bool inWorld = true, bool home = true, bool lifestream = true, bool busy = false, bool inMission = false,
        bool working = false)
        => new(territory, inWorld, home, lifestream, busy, inMission, working);

    private TripCommand Tick(StarTrip trip, TripObservation obs, double seconds = 3)
    {
        _now = _now.AddSeconds(seconds);
        return trip.Tick(obs, _now);
    }

    private StarTrip Started()
    {
        var trip = new StarTrip();
        Assert.True(trip.Start(Obs(), Auxesia, 3, "Auxesia", _now));
        return trip;
    }

    /// <summary>Asked once, watched across both zone changes, and done only when the character stands on the star.</summary>
    [Fact]
    public void ATrip_IsAskedOfLifestream_AndIsDoneWhenTheCharacterIsOnTheStar()
    {
        var trip = Started();

        Assert.Equal(TripCommand.Go, Tick(trip, Obs()));
        Assert.Equal(TripCommand.None, Tick(trip, Obs(busy: true)));
        Assert.Equal(TripPhase.Travelling, trip.Phase);

        Assert.Equal(TripCommand.None, Tick(trip, Obs(territory: 0, inWorld: false, busy: true))); // the teleport
        Assert.Equal(TripCommand.None, Tick(trip, Obs(territory: BestwaysBurrow, busy: true)));     // the aetheryte's menu
        Assert.Equal(TripCommand.None, Tick(trip, Obs(territory: 0, inWorld: false, busy: true)));
        Assert.Equal(TripCommand.None, Tick(trip, Obs(territory: Auxesia, busy: true)));           // there, Lifestream still tidying
        Assert.True(trip.Active);

        Assert.Equal(TripCommand.None, Tick(trip, Obs(territory: Auxesia)));
        Assert.Equal(TripPhase.Done, trip.Phase);
        Assert.Equal("Done — on Auxesia.", trip.Status);
    }

    public static TheoryData<string, string> Refusals => new()
    {
        { "nostar", "no star is picked" },
        { "noworld", "no character in the world" },
        { "there", "on Auxesia already" },
        { "working", "a mission run or a chain is going" },
        { "mission", "a mission is in progress" },
        { "away", "not on its Home World" },
        { "absent", "Lifestream is not loaded" },
        { "busy", "Lifestream is busy" },
    };

    /// <summary>No trip on a guess: each thing it needs is checked first, and the reason is said.</summary>
    [Theory]
    [MemberData(nameof(Refusals))]
    public void ATrip_IsRefused_WithTheReason(string what, string reason)
    {
        var trip = new StarTrip();
        var obs = what switch
        {
            "noworld" => Obs(inWorld: false),
            "there" => Obs(territory: Auxesia),
            "working" => Obs(territory: Phaenna, working: true),
            "mission" => Obs(territory: Phaenna, inMission: true),
            "away" => Obs(home: false),
            "absent" => Obs(lifestream: false),
            "busy" => Obs(busy: true),
            _ => Obs(),
        };

        Assert.False(trip.Start(obs, what == "nostar" ? 0u : Auxesia, what == "nostar" ? -1 : 3, "Auxesia", _now));
        Assert.Contains(reason, trip.Status);
        Assert.False(trip.Active);
        Assert.Equal(TripCommand.None, Tick(trip, obs));
    }

    /// <summary>Lifestream never gets busy: it is asked twice at most, then the trip halts.</summary>
    [Fact]
    public void ATrip_ThatLifestreamNeverTakes_IsAskedTwiceAndHalts()
    {
        var trip = Started();

        var asked = 0;
        for (var i = 0; i < 12 && trip.Active; i++)
        {
            if (Tick(trip, Obs()) == TripCommand.Go)
                asked++;
        }

        Assert.Equal(StarTrip.MaxAttempts, asked);
        Assert.Equal(TripPhase.Halted, trip.Phase);
        Assert.Contains("did not take the trip", trip.Status);
    }

    /// <summary>The verdict is the zone: Lifestream idle with the character somewhere else is a failed trip, after a moment to settle.</summary>
    [Fact]
    public void ATrip_ThatEndsSomewhereElse_Halts()
    {
        var trip = Started();
        Tick(trip, Obs());
        Tick(trip, Obs(busy: true));

        Assert.Equal(TripCommand.None, Tick(trip, Obs(territory: BestwaysBurrow)));
        Assert.True(trip.Active); // a zone may still be settling

        Assert.Equal(TripCommand.None, Tick(trip, Obs(territory: BestwaysBurrow), StarTrip.IdleSettle.TotalSeconds + 1));
        Assert.Equal(TripPhase.Halted, trip.Phase);
        Assert.Contains("not on Auxesia", trip.Status);
    }

    [Fact]
    public void ATrip_ThatTakesTooLong_HaltsAndTellsLifestreamToStop()
    {
        var trip = Started();
        Tick(trip, Obs());
        Tick(trip, Obs(busy: true));

        Assert.Equal(TripCommand.Abort, Tick(trip, Obs(territory: BestwaysBurrow, busy: true), StarTrip.TripDeadline.TotalSeconds + 1));
        Assert.Equal(TripPhase.Halted, trip.Phase);
    }

    [Fact]
    public void Stop_EndsTheTrip_AndTellsABusyLifestreamToStop()
    {
        var trip = Started();
        Tick(trip, Obs());
        Tick(trip, Obs(busy: true));

        Assert.Equal(TripCommand.Abort, trip.Stop(Obs(busy: true)));
        Assert.False(trip.Active);
        Assert.Equal(TripCommand.None, trip.Stop(Obs()));
    }

    /// <summary>What Lifestream is handed: one star's entry of the Bestways Burrow aetheryte, as well-formed JSON.</summary>
    [Theory]
    [InlineData(0, "AetheryteBestwaysBurrow:1:Value")]
    [InlineData(3, "AetheryteBestwaysBurrow:4:Value")]
    public void TheAlias_NamesOneStar(int starIndex, string entry)
    {
        var alias = StarTrip.LifestreamAlias(starIndex);

        using var json = JsonDocument.Parse(alias);
        var commands = json.RootElement.GetProperty("Commands");
        Assert.Equal(3, commands.GetArrayLength());
        Assert.Equal(175, commands[0].GetProperty("Aetheryte").GetInt32());
        var options = commands[2].GetProperty("SelectOption");
        Assert.Equal(1, options.GetArrayLength());
        Assert.Contains(entry, options[0].GetString());
    }
}
