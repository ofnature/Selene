using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;

namespace Selene.Services.Game;

/// <summary>
/// Where the other players the client can see are standing - so Selene does not stand on anyone (the user,
/// 2026-11-14: "they wont stand on people"). POSITIONS ONLY: no name, no id, nothing kept.
/// Fail-open: nobody, and a Status saying why.
/// </summary>
public sealed class NearbyPlayers
{
    private readonly IObjectTable _objectTable;

    public NearbyPlayers(IObjectTable objectTable)
    {
        _objectTable = objectTable;
    }

    public string Status { get; private set; } = "Idle — nobody looked for yet.";

    public IReadOnlyList<Vector3> Others()
    {
        var others = new List<Vector3>();
        try
        {
            var me = _objectTable.LocalPlayer;
            if (me == null)
            {
                Status = "No character in the world — nobody looked for.";
                return others;
            }

            foreach (var thing in _objectTable)
            {
                if (thing is IPlayerCharacter && thing.Address != me.Address)
                    others.Add(thing.Position);
            }

            Status = others.Count == 0 ? "No other player in sight." : $"{others.Count} other player(s) in sight.";
            return others;
        }
        catch (Exception ex)
        {
            Status = $"Could not look — {ex.Message}";
            others.Clear();
            return others;
        }
    }
}
