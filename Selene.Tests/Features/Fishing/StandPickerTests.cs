using System.Numerics;
using Selene.Features.Fishing;

namespace Selene.Tests.Features.Fishing;

public class StandPickerTests
{
    private const ulong Mene = 0x0040000000000001UL;

    private static readonly Vector3 Shard = new(0f, 0f, 0f);

    /// <summary>A shoreline running away from the arrival point: a place every two yalms, from 10 y out.</summary>
    private static List<StandCandidate> Shore(int places = 20)
        => Enumerable.Range(0, places).Select(i => new StandCandidate(new Vector3(10f + (i * 2f), 0f, 0f), 0f, 5)).ToList();

    private static readonly Vector3[] Nobody = Array.Empty<Vector3>();

    [Fact]
    public void ThePlaceTaken_IsAmongTheNearestToTheArrivalPoint()
    {
        for (var mission = 0; mission < 50; mission++)
        {
            var choice = StandPicker.Pick(Shore(), Shard, Nobody, Mene, mission);

            Assert.NotNull(choice);
            Assert.InRange(choice!.FromArrival, 10f, 10f + StandPicker.Slack);
        }
    }

    /// <summary>The whole point: not the same stone every mission.</summary>
    [Fact]
    public void FromOneMissionToTheNext_ThePlaceVaries()
    {
        var places = Enumerable.Range(0, 50)
            .Select(mission => StandPicker.Pick(Shore(), Shard, Nobody, Mene, mission)!.Stand.Position)
            .Distinct()
            .Count();

        Assert.True(places > 1);
    }

    /// <summary>Asked twice about the same mission (a reload, a second look), the answer is the same.</summary>
    [Fact]
    public void TheSameCharacterAndMission_GetTheSamePlace()
    {
        var first = StandPicker.Pick(Shore(), Shard, Nobody, Mene, 7);
        var second = StandPicker.Pick(Shore(), Shard, Nobody, Mene, 7);

        Assert.Equal(first!.Stand, second!.Stand);
    }

    /// <summary>"They wont stand on people": a place with someone on it is passed over, however near it is.</summary>
    [Fact]
    public void APlaceSomebodyStandsOn_IsNeverTaken()
    {
        var crowd = new[] { new Vector3(10f, 0f, 0f), new Vector3(12f, 0f, 1f) };

        for (var mission = 0; mission < 50; mission++)
        {
            var choice = StandPicker.Pick(Shore(), Shard, crowd, Mene, mission);

            Assert.NotNull(choice);
            Assert.True(choice!.NearestPlayer >= StandPicker.PlayerClearance);
            Assert.True(choice.Stand.Position.X >= 14f);
        }
    }

    [Fact]
    public void WithSomebodyOnEveryPlace_NothingIsChosen()
    {
        var shore = Shore(3);
        var crowd = shore.Select(s => s.Position).ToArray();

        Assert.Null(StandPicker.Pick(shore, Shard, crowd, Mene, 1));
    }

    [Fact]
    public void WithNowhereToStand_NothingIsChosen()
    {
        Assert.Null(StandPicker.Pick(Array.Empty<StandCandidate>(), Shard, Nobody, Mene, 1));
    }

    /// <summary>The others from the same stretch go along, for when the first cast does not take.</summary>
    [Fact]
    public void TheAlternates_AreOtherPlacesFromTheSameStretch()
    {
        var choice = StandPicker.Pick(Shore(), Shard, Nobody, Mene, 3)!;

        Assert.Equal(2, choice.Alternates.Count);
        Assert.DoesNotContain(choice.Stand, choice.Alternates);
        Assert.All(choice.Alternates, a => Assert.InRange(a.Position.X, 10f, 10f + StandPicker.Slack));
    }

    [Fact]
    public void WithNobodyAbout_NoNearestPlayerIsReported()
    {
        Assert.Null(StandPicker.Pick(Shore(), Shard, Nobody, Mene, 1)!.NearestPlayer);
    }
}
