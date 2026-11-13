using System;
using Dalamud.Configuration;
using Selene.Windows.Dock;

namespace Selene;

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
