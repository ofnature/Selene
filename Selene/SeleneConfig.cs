using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Selene.Windows.Dock;

namespace Selene;

/// <summary>One character's settings.</summary>
[Serializable]
public sealed class CharacterSettings
{
    /// <summary>
    /// The missions the user ticked on the Missions page (WKSMissionUnit row ids): the only ones Selene
    /// may ever accept. Picked by hand - Selene never adds to it.
    /// </summary>
    public List<uint> PickedMissions { get; set; } = new();

    /// <summary>
    /// The tabs ticked as a whole ("run whatever the game offers of this kind" - the user, 2026-11-14, instead
    /// of re-rolling for a ticked mission): Selene may run any mission of these kinds that the game holds.
    /// </summary>
    public List<Features.Missions.MissionCategory> WholeTabs { get; set; } = new();

    /// <summary>
    /// The rank at which a ranked mission is reported (the user, 2026-11-14: "a setting"). Master missions do
    /// not use it: they are played to the clock.
    /// </summary>
    public Features.Missions.MissionRank ReportGoal { get; set; } = Features.Missions.MissionRank.Gold;

    /// <summary>How a chain of missions picks the next one (the user, 2026-11-14: "quest chaining for different types").</summary>
    public Features.Missions.ChainMode ChainMode { get; set; } = Features.Missions.ChainMode.Ticked;

    /// <summary>A chain stops after this many missions; 0 = no limit.</summary>
    public int ChainLimit { get; set; }
}

/// <summary>
/// Root persistent configuration for Selene.
///
/// This file is ONE JSON shared by every client launched from the same XIVLauncher (last save
/// wins) - the same trap as in every plugin of the suite. Per-character settings therefore go in a
/// dictionary keyed by content id, and per-character DATA (a session's history, say) in a file of
/// its own per content id. Never put per-box state in a plain property here.
/// </summary>
[Serializable]
public sealed class SeleneConfig : IPluginConfiguration
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>
    /// Pin a "Selene" tab to a screen edge that pops the evening's schedule out while pointed at (click to
    /// pin) - Daedalus's edge dock. Off unless asked for, as in Daedalus. A preference of this PC's screen,
    /// so it is shared by its clients on purpose; the main window still opens with /selene.
    /// </summary>
    public bool EdgeDockEnabled { get; set; }

    /// <summary>Which screen edge the tab is pinned to. Dragging the tab changes it.</summary>
    public DockEdge EdgeDockEdge { get; set; } = DockEdge.Left;

    /// <summary>Where along its edge the tab sits: its centre as a fraction of the edge (0.5 = the middle).</summary>
    public float EdgeDockAlong { get; set; } = 0.5f;

    /// <summary>
    /// Per-character settings, keyed by content id (the suite's rule for a config every client of one
    /// XIVLauncher shares). Two clients changing their own settings in the same minute can still lose one
    /// change to the other's save - rare, and the same in every plugin of the suite.
    /// </summary>
    public Dictionary<ulong, CharacterSettings> Characters { get; set; } = new();

    /// <summary>The character's settings, made on first use.</summary>
    public CharacterSettings For(ulong contentId)
    {
        if (!Characters.TryGetValue(contentId, out var settings))
            Characters[contentId] = settings = new CharacterSettings();
        return settings;
    }

    /// <summary>
    /// Bring a loaded config up to <see cref="CurrentVersion"/>. Returns true when something
    /// changed (the caller then saves). A saved config OVERRIDES a property's C# default, so any
    /// future default change needs a step here.
    /// </summary>
    public bool Migrate()
    {
        if (Version >= CurrentVersion)
            return false;

        Version = CurrentVersion;
        return true;
    }
}
