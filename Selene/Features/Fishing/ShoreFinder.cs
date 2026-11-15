using System;
using System.Collections.Generic;
using System.Numerics;

namespace Selene.Features.Fishing;

/// <summary>
/// One vertical look at the world, from above: the height of the ground there and of the fishable water
/// there, each null when the look met none.
/// </summary>
public readonly record struct Column(float X, float Z, float? GroundY, float? WaterY)
{
    /// <summary>A look that meets the water's own surface as "ground" still reads as water.</summary>
    private const float Tolerance = 0.05f;

    /// <summary>Water is what is on top here: there is water, and no ground above it.</summary>
    public bool IsWater => WaterY is { } water && (GroundY is not { } ground || water >= ground - Tolerance);

    /// <summary>Ground to stand on: there is ground, and it is not under water.</summary>
    public bool IsLand => GroundY != null && !IsWater;
}

/// <summary>A place at the water's edge to fish from.</summary>
/// <param name="Facing">The game's rotation that looks at the water: forward = (sin, 0, cos).</param>
/// <param name="WaterColumns">How many looks within reach were water - a measure of how open the water is.</param>
public sealed record StandCandidate(Vector3 Position, float Facing, int WaterColumns);

/// <summary>
/// Finds the water's edge in a set of looks (the user, 2026-11-14: "have selene find the water" - no
/// recorded spots). Pure: the looks come from <c>Services/Game/WaterProbe</c>.
/// THE NUMBERS ARE FIRST GUESSES, unseen in game: how close the water must be for a cast to take is the
/// game's rule and is only proven by the first cast (Demeter reports "cannot fish here").
/// </summary>
public static class ShoreFinder
{
    /// <summary>The water must begin within this many yalms of the feet.</summary>
    public const float Reach = 3f;

    /// <summary>Fewer water looks than this within reach is a puddle or a stray reading, not a shore.</summary>
    public const int MinWaterColumns = 3;

    /// <summary>The water's surface may lie this far below the feet (a bank, a pier) and no further.</summary>
    public const float MaxDrop = 8f;

    /// <summary>...and not above them: that would be standing in it.</summary>
    public const float MaxRise = 0.5f;

    public static IReadOnlyList<StandCandidate> Find(IReadOnlyList<Column> columns)
    {
        var water = new List<Column>();
        foreach (var column in columns)
        {
            if (column.IsWater)
                water.Add(column);
        }

        var stands = new List<StandCandidate>();
        if (water.Count == 0)
            return stands;

        foreach (var land in columns)
        {
            if (!land.IsLand)
                continue;

            var feet = land.GroundY!.Value;
            var count = 0;
            float sumX = 0f, sumZ = 0f;
            foreach (var wet in water)
            {
                var dx = wet.X - land.X;
                var dz = wet.Z - land.Z;
                if (Math.Abs(dx) > Reach || Math.Abs(dz) > Reach || (dx * dx) + (dz * dz) > Reach * Reach)
                    continue;

                var surface = wet.WaterY!.Value;
                if (surface < feet - MaxDrop || surface > feet + MaxRise)
                    continue;

                count++;
                sumX += dx;
                sumZ += dz;
            }

            // Water all round (a rock in the pond) gives no direction to face: the sums cancel.
            if (count < MinWaterColumns || ((sumX * sumX) + (sumZ * sumZ)) < 0.0001f)
                continue;

            stands.Add(new StandCandidate(new Vector3(land.X, feet, land.Z), MathF.Atan2(sumX, sumZ), count));
        }

        return stands;
    }
}
