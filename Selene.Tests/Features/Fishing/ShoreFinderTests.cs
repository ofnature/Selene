using System.Numerics;
using Selene.Features.Fishing;

namespace Selene.Tests.Features.Fishing;

public class ShoreFinderTests
{
    /// <summary>
    /// A square of looks, one yalm apart: ground at height 10 where x is below <paramref name="shoreX"/>, and
    /// from there on a pond whose surface is at <paramref name="surface"/> over a bed two yalms deeper.
    /// </summary>
    private static List<Column> Bank(float shoreX = 5f, float surface = 9.5f, int size = 10)
    {
        var columns = new List<Column>();
        for (var x = 0; x < size; x++)
        {
            for (var z = 0; z < size; z++)
                columns.Add(x < shoreX ? new Column(x, z, 10f, null) : new Column(x, z, surface - 2f, surface));
        }

        return columns;
    }

    [Fact]
    public void TheGroundBesideTheWater_IsAPlaceToStand_FacingTheWater()
    {
        var stands = ShoreFinder.Find(Bank());

        Assert.NotEmpty(stands);
        var edge = stands.Single(s => s.Position == new Vector3(4f, 10f, 5f));

        // The water lies towards +x: the game's rotation for that is a quarter turn (forward = sin, cos).
        Assert.Equal(MathF.PI / 2f, edge.Facing, 2);
        Assert.True(edge.WaterColumns >= ShoreFinder.MinWaterColumns);
    }

    [Fact]
    public void GroundFurtherBackThanACastReaches_IsNotOffered()
    {
        var stands = ShoreFinder.Find(Bank());

        Assert.DoesNotContain(stands, s => s.Position.X < 5f - ShoreFinder.Reach);
    }

    [Fact]
    public void NobodyStandsInTheWater()
    {
        var stands = ShoreFinder.Find(Bank());

        Assert.DoesNotContain(stands, s => s.Position.X >= 5f);
    }

    [Fact]
    public void WithNoWater_ThereIsNowhereToFish()
    {
        var dry = Bank(shoreX: 99f);

        Assert.Empty(ShoreFinder.Find(dry));
    }

    /// <summary>One wet reading in a field is a puddle or a stray hit, not a shore.</summary>
    [Fact]
    public void ASingleWetLook_IsNotAShore()
    {
        var field = Bank(shoreX: 99f);
        field[55] = new Column(field[55].X, field[55].Z, 8f, 9.5f);

        Assert.Empty(ShoreFinder.Find(field));
    }

    /// <summary>A cliff top far above the water is not a place to cast from.</summary>
    [Fact]
    public void WaterFarBelowTheFeet_IsOutOfReach()
    {
        var cliff = Bank(surface: 10f - ShoreFinder.MaxDrop - 1f);

        Assert.Empty(ShoreFinder.Find(cliff));
    }

    /// <summary>Ground under a bridge or a roof: the look from above meets the roof, and that is where one would stand.</summary>
    [Fact]
    public void GroundAboveTheWater_IsLand_NotWater()
    {
        var bridge = new Column(0f, 0f, GroundY: 12f, WaterY: 9.5f);
        var pond = new Column(1f, 0f, GroundY: 7.5f, WaterY: 9.5f);
        var surfaceReadAsGround = new Column(2f, 0f, GroundY: 9.5f, WaterY: 9.5f);

        Assert.True(bridge.IsLand);
        Assert.True(pond.IsWater);
        Assert.True(surfaceReadAsGround.IsWater);
    }
}
