using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Selene.Features.Schedule;

namespace Selene.Features.Fishing;

/// <summary>The place chosen, the others that would have done, and the figures behind the choice.</summary>
/// <param name="Alternates">Up to two more from the same pool, should the first cast not take there.</param>
/// <param name="FromArrival">Yalms from the arrival point, in a straight line on the ground plane.</param>
/// <param name="NearestPlayer">Yalms to the nearest other player; null when nobody is about.</param>
public sealed record StandChoice(StandCandidate Stand, IReadOnlyList<StandCandidate> Alternates, float FromArrival, float? NearestPlayer);

/// <summary>
/// Chooses where to stand among the places <see cref="ShoreFinder"/> found. The user's rule (2026-11-14):
/// "find closest spot to a warp point/cosmoliner spot which would be more human like, also they wont stand
/// on people so would stand a couple yalms from the nearest person". So: nobody within
/// <see cref="PlayerClearance"/>; then the places nearest where a player would walk in from; and among those
/// within <see cref="Slack"/> of the nearest, one by the dice - so it is not the same stone every time.
/// Distances are straight lines: the path a walk would take is not known here.
/// </summary>
public static class StandPicker
{
    /// <summary>"A couple yalms from the nearest person."</summary>
    public const float PlayerClearance = 2.5f;

    /// <summary>Places this much further from the arrival point than the nearest one are as good as it.</summary>
    public const float Slack = 6f;

    /// <param name="seed">The character; with <paramref name="salt"/> (something that differs from one mission to
    /// the next) it decides which of the good places is taken.</param>
    public static StandChoice? Pick(
        IReadOnlyList<StandCandidate> candidates, Vector3 arrival, IReadOnlyList<Vector3> others, ulong seed, int salt)
    {
        var free = candidates
            .Where(c => !others.Any(o => Flat(c.Position, o) < PlayerClearance))
            .Select(c => (Stand: c, Distance: Flat(c.Position, arrival)))
            .OrderBy(c => c.Distance)
            .ToList();
        if (free.Count == 0)
            return null;

        var pool = free.Where(c => c.Distance <= free[0].Distance + Slack).ToList();
        var dice = new SeededRandom(seed, salt);
        var index = Math.Min((int)(dice.NextDouble() * pool.Count), pool.Count - 1);
        var chosen = pool[index];

        var alternates = pool.Where((_, i) => i != index).Take(2).Select(c => c.Stand).ToList();
        float? nearest = others.Count == 0 ? null : others.Min(o => Flat(chosen.Stand.Position, o));
        return new StandChoice(chosen.Stand, alternates, chosen.Distance, nearest);
    }

    private static float Flat(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }
}
