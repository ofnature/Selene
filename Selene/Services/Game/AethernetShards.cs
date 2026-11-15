using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using TerritoryType = Lumina.Excel.Sheets.TerritoryType;
using WKSAetheryteObjectGroup = Lumina.Excel.Sheets.WKSAetheryteObjectGroup;

namespace Selene.Services.Game;

/// <summary>
/// Where a Cosmic zone's aethernet shards stand - the places a player arrives at, and so walks to the
/// water from. Read from the game's DATA, not from the world: which objects are shards is the sheet
/// <c>WKSAetheryteObjectGroup</c> (its first number), and where they stand is the zone's layout file
/// (<c>planlive.lgb</c>). Checked offline 2026-11-14: Auxesia's five shards (objects 2015422-2015426) are
/// placed there. COSMOLINER STOPS ARE NOT READ: where they are kept was not found.
/// Read once a zone and kept. Fail-open: no shards, and a Status saying why.
/// </summary>
public sealed class AethernetShards
{
    private readonly IDataManager _data;
    private uint _territory;
    private IReadOnlyList<Vector3> _shards = Array.Empty<Vector3>();

    public AethernetShards(IDataManager data)
    {
        _data = data;
    }

    public string Status { get; private set; } = "Idle — no zone read yet.";

    public IReadOnlyList<Vector3> For(uint territoryId)
    {
        if (territoryId == _territory)
            return _shards;

        _territory = territoryId;
        _shards = Array.Empty<Vector3>();
        try
        {
            if (territoryId == 0 || !_data.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territory))
            {
                Status = "Not in a zone.";
                return _shards;
            }

            var shardObjects = new HashSet<uint>();
            foreach (var group in _data.GetSubrowExcelSheet<WKSAetheryteObjectGroup>())
            {
                foreach (var member in group)
                {
                    if (member.Unknown0 != 0)
                        shardObjects.Add(member.Unknown0);
                }
            }

            // "ffxiv/cos_c1/hou/c1w4/level/c1w4" -> "bg/ffxiv/cos_c1/hou/c1w4/level/planlive.lgb"
            var bg = territory.Bg.ExtractText();
            var level = bg.IndexOf("/level/", StringComparison.Ordinal);
            if (level < 0)
            {
                Status = $"Zone {territoryId} has no layout path.";
                return _shards;
            }

            var path = $"bg/{bg[..(level + 7)]}planlive.lgb";
            var layout = _data.GetFile<LgbFile>(path);
            if (layout == null)
            {
                Status = $"Zone {territoryId}: {path} could not be read.";
                return _shards;
            }

            var found = new List<Vector3>();
            foreach (var layer in layout.Layers)
            {
                foreach (var placed in layer.InstanceObjects)
                {
                    if (placed.AssetType != LayerEntryType.EventObject
                        || placed.Object is not LayerCommon.EventInstanceObject eventObject
                        || !shardObjects.Contains(eventObject.ParentData.BaseId))
                    {
                        continue;
                    }

                    var at = placed.Transform.Translation;
                    found.Add(new Vector3(at.X, at.Y, at.Z));
                }
            }

            _shards = found;
            Status = found.Count == 0
                ? $"Zone {territoryId}: no aethernet shard in its layout."
                : $"Zone {territoryId}: {found.Count} aethernet shard(s) read from its layout.";
            return _shards;
        }
        catch (Exception ex)
        {
            Status = $"Zone {territoryId}: the shards could not be read — {ex.Message}";
            return _shards;
        }
    }
}
