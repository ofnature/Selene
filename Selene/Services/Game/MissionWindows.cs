using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Selene.Services.Game;

/// <summary>
/// The game's mission windows, as far as Selene touches them: whether the Cosmic Crafting Log is on screen,
/// and the Report button of "Mission in Progress". The user's decision (2026-11-12): missions are driven
/// through the game's own windows, as a player's client does - not through the mission module's calls.
/// Fail-open: nothing pressed, and a Status saying why.
/// </summary>
public sealed unsafe class MissionWindows
{
    private const string CosmicLog = "WKSRecipeNotebook";
    private const string MissionInProgress = "WKSMissionInfomation";

    /// <summary>
    /// The Report button's node id in "Mission in Progress" - ECommons' number (docs/ice-notes.md). ITS
    /// NEIGHBOUR, 38, IS ABANDON: the label is read before anything is pressed, and a button that says
    /// "Abandon" is never pressed. NOT PRESSED IN GAME when written.
    /// </summary>
    private const uint ReportButton = 37;

    private readonly IGameGui _gameGui;

    public MissionWindows(IGameGui gameGui)
    {
        _gameGui = gameGui;
    }

    /// <summary>What was pressed last, or why nothing was.</summary>
    public string Status { get; private set; } = "Idle — nothing pressed.";

    /// <summary>The Cosmic Crafting Log is on screen.</summary>
    public bool LogOpen
    {
        get
        {
            try
            {
                var addon = _gameGui.GetAddonByName(CosmicLog);
                return !addon.IsNull && addon.IsVisible;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Press Report, the way a click does: the button's own event, handed to its window. Whether the
    /// mission was reported is for the caller to read off the game - this only says what it pressed.
    /// </summary>
    public void Report()
    {
        try
        {
            var addon = _gameGui.GetAddonByName(MissionInProgress);
            if (addon.IsNull || !addon.IsVisible)
            {
                Status = "Report: the Mission in Progress window is not open.";
                return;
            }

            var unit = (AtkUnitBase*)addon.Address;
            var button = unit->GetComponentButtonById(ReportButton);
            if (button == null)
            {
                Status = $"Report: the window has no button {ReportButton}.";
                return;
            }

            var label = button->ButtonTextNode != null ? button->ButtonTextNode->NodeText.ToString() : string.Empty;
            if (label.Contains("Abandon", StringComparison.OrdinalIgnoreCase))
            {
                Status = $"Report: button {ReportButton} reads \"{label}\" — NOT pressed. The window's layout has changed.";
                return;
            }

            var owner = button->AtkComponentBase.OwnerNode;
            if (owner == null || !button->IsEnabled || !owner->AtkResNode.IsVisible())
            {
                Status = $"Report: the button \"{label}\" is not enabled.";
                return;
            }

            var evt = owner->AtkResNode.AtkEventManager.Event;
            if (evt == null)
            {
                Status = $"Report: the button \"{label}\" has no click event.";
                return;
            }

            unit->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, null);
            Status = $"Report: pressed \"{label}\".";
        }
        catch (Exception ex)
        {
            Status = $"Report: could not be pressed — {ex.Message}";
        }
    }
}
