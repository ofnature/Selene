using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Selene.Features.Fishing;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace Selene.Services.Game;

/// <summary>
/// Looks straight down at the world around a point and says, look by look, where the ground is and where
/// the fishable water is. The game's own collision test (ClientStructs maps it:
/// <c>BGCollisionModule.RaycastMaterialFilter</c>), asked twice a look with a material filter - once for
/// what a character stands on, once for water that can be fished.
///
/// UNSEEN IN GAME when written (2026-11-14). The two material bits are the unknowns:
/// 0x4000 is the filter ClientStructs' own helper uses for "the world a character collides with";
/// 0x8000 as "fishable" is ICE's reading (it tests that bit on what its ray hits, through a signature scan
/// Selene does not need). If the Status says "0 water" beside a pond, 0x8000 is wrong.
/// Only what the game has loaded near the character can be looked at.
/// Fail-open: no looks, and a Status saying why.
/// </summary>
public sealed unsafe class WaterProbe
{
    /// <summary>How far out to look, and how far apart the looks are (yalms).</summary>
    public const float Radius = 45f;
    public const float Step = 1.5f;

    /// <summary>A look starts this far above the character and goes this far down.</summary>
    private const float Above = 25f;
    private const float Depth = 70f;

    private const int StandsOn = 0x4000;
    private const int Fishable = 0x8000;

    /// <summary>The layer the world's own geometry is on (ClientStructs' helper and ICE both ask for 1).</summary>
    private const int WorldLayer = 1;

    /// <summary>What the last look found, or why there was none.</summary>
    public string Status { get; private set; } = "Idle — no water looked for yet.";

    public IReadOnlyList<Column> Scan(Vector3 centre)
    {
        var columns = new List<Column>();
        try
        {
            var framework = GameFramework.Instance();
            var module = framework == null ? null : framework->BGCollisionModule;
            if (module == null)
            {
                Status = "The game's collision module is not there — nothing looked at.";
                return columns;
            }

            int water = 0, land = 0;
            for (var dx = -Radius; dx <= Radius; dx += Step)
            {
                for (var dz = -Radius; dz <= Radius; dz += Step)
                {
                    if ((dx * dx) + (dz * dz) > Radius * Radius)
                        continue;

                    var origin = new Vector3(centre.X + dx, centre.Y + Above, centre.Z + dz);
                    var column = new Column(origin.X, origin.Z, Down(module, origin, StandsOn), Down(module, origin, Fishable));
                    columns.Add(column);
                    if (column.IsWater) water++;
                    else if (column.IsLand) land++;
                }
            }

            Status = $"Looked down at {columns.Count} points within {Radius:0} y: {water} water, {land} ground, "
                     + $"{columns.Count - water - land} nothing.";
            return columns;
        }
        catch (Exception ex)
        {
            Status = $"The look failed — {ex.Message}";
            columns.Clear();
            return columns;
        }
    }

    /// <summary>The height of the first thing of that material straight below, or null.</summary>
    private static float? Down(BGCollisionModule* module, Vector3 origin, int material)
    {
        var direction = new Vector3(0f, -1f, 0f);

        // The filter is two 64-bit numbers, mask then value: "(material AND mask) == value".
        var filter = stackalloc int[4] { material, 0, material, 0 };
        RaycastHit hit = default;
        return module->RaycastMaterialFilter(&hit, &origin, &direction, Depth, WorldLayer, filter) ? hit.Point.Y : null;
    }
}
