using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Selene.Services.Game;

/// <summary>What the Stellar Missions board shows right now.</summary>
/// <param name="Missions">The mission of each row shown, in row order; a row without one is 0.</param>
/// <param name="Selected">The mission selected on the board; 0 = none.</param>
public readonly record struct BoardView(bool Open, IReadOnlyList<uint> Missions, uint Selected);

/// <summary>
/// The game's mission windows, as far as Selene touches them. The user's decision (2026-11-12): missions
/// are driven through the game's own windows, as a player's client does - not through the mission module's
/// calls. Every number below is ECommons' (docs/ice-notes.md, "The window road"), read from its source.
/// Fail-open: nothing pressed, and a Status saying why. Whether a press DID anything is for the caller to
/// read off the game.
///
/// SEEN WORKING IN GAME: <see cref="Report"/> (2026-11-12). NOT YET: everything else here.
/// </summary>
public sealed unsafe class MissionWindows
{
    private const string Hud = "WKSHud";
    private const string Board = "WKSMission";
    private const string MissionInProgress = "WKSMissionInfomation";
    private const string CosmicLog = "WKSRecipeNotebook";
    private const string YesNo = "SelectYesno";

    private const int ConfirmMissionOffset = 20;
    private const int ConfirmAddonOffset = 24;

    /// <summary>The HUD's Stellar Missions button: opens the board, or Mission in Progress while one runs.</summary>
    private const uint HudMissionsButton = 7;

    /// <summary>
    /// The board's Tool Mastery tab. A GUESS: ECommons names 17 / 18 / 19 (basic, provisional, critical) and
    /// stops there; the game's board has a fourth tab icon after them. If it is wrong the mission never
    /// appears among the rows and the run halts saying so.
    /// </summary>
    private const uint MasteryTabButton = 20;

    /// <summary>The board's other tabs, by ECommons' numbers. Unpressed in game.</summary>
    private const uint BasicTabButton = 17;
    private const uint ProvisionalTabButton = 18;
    private const uint CriticalTabButton = 19;

    private const uint LogButton = 35;

    /// <summary>
    /// The Report button's node id in "Mission in Progress". ITS NEIGHBOUR, 38, IS ABANDON: the label is
    /// read before anything is pressed, and a button that says "Abandon" is never pressed.
    /// </summary>
    private const uint ReportButton = 37;

    // The board's values (ECommons' AddonMaster.WKSMission).
    private const int BoardRowCount = 33;
    private const int BoardFirstRow = 36;
    private const int BoardRowStride = 6;
    private const int BoardSelected = 1063;
    private const int BoardSelectCallback = 12;
    private const int BoardInitiateCallback = 13;

    /// <summary>The Cosmic log's "pick this row" (Hephaestus's CraftingLogGame.OpenRecipe, from Artisan).</summary>
    private const int LogSelectCallback = 0;

    private readonly IGameGui _gameGui;

    public MissionWindows(IGameGui gameGui)
    {
        _gameGui = gameGui;
    }

    /// <summary>What was pressed last, or why nothing was.</summary>
    public string Status { get; private set; } = "Idle — nothing pressed.";

    /// <summary>The Cosmic Crafting Log is on screen.</summary>
    public bool LogOpen => Window(CosmicLog) != null;

    /// <summary>The board's rows and its selection, read off its own values.</summary>
    public BoardView ReadBoard()
    {
        try
        {
            var board = Window(Board);
            if (board == null)
                return new BoardView(false, Array.Empty<uint>(), 0);

            var rows = Math.Min(Number(board, BoardRowCount), 128u);
            var missions = new List<uint>();
            for (var row = 0; row < rows; row++)
                missions.Add(Number(board, BoardFirstRow + (row * BoardRowStride)));

            return new BoardView(true, missions, Number(board, BoardSelected));
        }
        catch
        {
            return new BoardView(false, Array.Empty<uint>(), 0);
        }
    }

    public void OpenBoard() => Press(Hud, HudMissionsButton, "Open the board");

    // [SUPERSEDED 2026-11-14 by ShowTab — the other kinds of mission sit on the board's other tabs]
    // public void ShowMasteryTab() => Press(Board, MasteryTabButton, "Tool Mastery tab");

    /// <summary>
    /// Bring up the board tab a kind of mission is on. ECommons' numbers: 17 basic (the ranked missions),
    /// 18 provisional (weather, hour and sequence missions - THAT THE SEQUENCE ONES ARE THERE IS AN
    /// ASSUMPTION), 19 critical (red alerts); 20 for Tool Mastery is the guess it always was.
    /// NONE OF THE FOUR HAS BEEN PRESSED IN GAME: the board has so far always opened on the right tab.
    /// </summary>
    public void ShowTab(Features.Missions.MissionCategory category)
    {
        switch (category)
        {
            case Features.Missions.MissionCategory.Master:
                Press(Board, MasteryTabButton, "Tool Mastery tab");
                break;
            case Features.Missions.MissionCategory.RedAlert:
                Press(Board, CriticalTabButton, "Critical tab");
                break;
            case Features.Missions.MissionCategory.Sequence:
            case Features.Missions.MissionCategory.Weather:
            case Features.Missions.MissionCategory.Timed:
                Press(Board, ProvisionalTabButton, "Provisional tab");
                break;
            default:
                Press(Board, BasicTabButton, "Basic missions tab");
                break;
        }
    }

    /// <summary>Select the mission's row: what a click on it sends.</summary>
    public void Select(uint missionId) => BoardCallback(BoardSelectCallback, missionId, "Select", mustBeSelected: false);

    /// <summary>Initiate - only for the mission the board shows as selected.</summary>
    public void Initiate(uint missionId) => BoardCallback(BoardInitiateCallback, missionId, "Initiate", mustBeSelected: true);

    /// <summary>What was found the last time the game was asked whether it is confirming a mission.</summary>
    public string ConfirmDetail { get; private set; } = "no question on screen";

    /// <summary>
    /// The mission the game is asking to commence right now ("Commence selected mission?", seen 2026-11-12
    /// after Initiate), or 0. Told apart from every other yes/no WITHOUT its text: the mission agent keeps
    /// the id of the dialog it opened and the mission it is about (ClientStructs' AgentWKSMission.MissionData,
    /// fields ConfirmMissionId at 20 and ConfirmAddonId at 24 - private there, so read by offset).
    /// UNVERIFIED IN GAME when written: if the ids do not match, this says 0 and nothing is answered.
    /// </summary>
    public uint ConfirmingMission()
    {
        try
        {
            var yesNo = Window(YesNo);
            if (yesNo == null)
            {
                ConfirmDetail = "no question on screen";
                return 0;
            }

            var agent = AgentWKSMission.Instance();
            if (agent == null || agent->Data == null)
            {
                ConfirmDetail = $"a question is up (window {yesNo->Id}); the mission agent has no data";
                return 0;
            }

            var data = (byte*)agent->Data;
            var missionId = *(uint*)(data + ConfirmMissionOffset);
            var addonId = *(uint*)(data + ConfirmAddonOffset);
            var ours = addonId != 0 && addonId == yesNo->Id;
            ConfirmDetail = $"a question is up (window {yesNo->Id}); the mission agent's is window {addonId}, mission {missionId}"
                            + (ours ? " — the same" : " — NOT the same");
            return ours ? missionId : 0;
        }
        catch (Exception ex)
        {
            ConfirmDetail = $"could not be read — {ex.Message}";
            return 0;
        }
    }

    /// <summary>Yes to "Commence selected mission?" - checked again here: that question, for that mission.</summary>
    public void ConfirmMission(uint missionId)
    {
        try
        {
            if (missionId == 0 || ConfirmingMission() != missionId)
            {
                Status = $"Confirm: the game is not asking about mission {missionId} — NOT answered.";
                return;
            }

            Window(YesNo)->FireCallbackInt(0);
            Status = $"Confirm: answered Yes to commencing mission {missionId}.";
        }
        catch (Exception ex)
        {
            Status = $"Confirm: failed — {ex.Message}";
        }
    }

    /// <summary>The Cosmic Crafting Log, from Mission in Progress - which is brought up first if it is not there.</summary>
    public void OpenLog()
    {
        if (Window(MissionInProgress) == null)
            Press(Hud, HudMissionsButton, "Open Mission in Progress");
        else
            Press(MissionInProgress, LogButton, "Cosmic Crafting Log");
    }

    /// <summary>Pick the first row of the open Cosmic Crafting Log - a Master mission has one recipe.</summary>
    public void SelectRecipe()
    {
        try
        {
            var log = Window(CosmicLog);
            if (log == null)
            {
                Status = "Select recipe: the Cosmic Crafting Log is not open.";
                return;
            }

            var values = stackalloc AtkValue[2];
            values[0].SetInt(LogSelectCallback);
            values[1].SetInt(0);
            log->FireCallback(2, values, false);
            Status = "Select recipe: picked the log's first row.";
        }
        catch (Exception ex)
        {
            Status = $"Select recipe: failed — {ex.Message}";
        }
    }

    // [SUPERSEDED 2026-11-12 — the second full run halted here: "Report: the WKSMissionInfomation window is not open."
    //  With the mission accepted by Selene the window was not up at the end, as it had been when the user accepted by hand.]
    // public void Report() => Press(MissionInProgress, ReportButton, "Report", never: "Abandon");

    /// <summary>
    /// Press Report - bringing Mission in Progress up first if it is not on screen (the caller's next try
    /// then finds it). Never a button whose label says Abandon.
    /// </summary>
    public void Report()
    {
        if (Window(MissionInProgress) == null)
            Press(Hud, HudMissionsButton, "Open Mission in Progress (to report)");
        else
            Press(MissionInProgress, ReportButton, "Report", never: "Abandon");
    }

    private void BoardCallback(int callback, uint missionId, string what, bool mustBeSelected)
    {
        try
        {
            var board = Window(Board);
            if (board == null)
            {
                Status = $"{what}: the mission board is not open.";
                return;
            }

            var view = ReadBoard();
            var row = -1;
            for (var i = 0; i < view.Missions.Count; i++)
            {
                if (view.Missions[i] == missionId)
                {
                    row = i;
                    break;
                }
            }

            if (missionId == 0 || row < 0)
            {
                Status = $"{what}: mission {missionId} is not among the board's rows.";
                return;
            }

            if (mustBeSelected && view.Selected != missionId)
            {
                Status = $"{what}: the board has mission {view.Selected} selected, not {missionId} — NOT pressed.";
                return;
            }

            var values = stackalloc AtkValue[3];
            values[0].SetInt(callback);
            values[1].SetInt((int)missionId);
            values[2].SetInt(row);
            board->FireCallback(3, values, true);
            Status = $"{what}: sent for mission {missionId}, row {row}.";
        }
        catch (Exception ex)
        {
            Status = $"{what}: failed — {ex.Message}";
        }
    }

    /// <summary>
    /// Press a window's button the way a click does: the button's own event, handed to its window
    /// (ECommons' ClickButtonIfEnabled).
    /// </summary>
    /// <param name="never">A word the button's label must not carry.</param>
    private void Press(string window, uint buttonId, string what, string? never = null)
    {
        try
        {
            var unit = Window(window);
            if (unit == null)
            {
                Status = $"{what}: the {window} window is not open.";
                return;
            }

            var button = unit->GetComponentButtonById(buttonId);
            if (button == null)
            {
                Status = $"{what}: {window} has no button {buttonId}.";
                return;
            }

            var label = button->ButtonTextNode != null ? button->ButtonTextNode->NodeText.ToString() : string.Empty;
            if (never != null && label.Contains(never, StringComparison.OrdinalIgnoreCase))
            {
                Status = $"{what}: button {buttonId} reads \"{label}\" — NOT pressed. The window's layout has changed.";
                return;
            }

            var owner = button->AtkComponentBase.OwnerNode;
            if (owner == null || !button->IsEnabled || !owner->AtkResNode.IsVisible())
            {
                Status = $"{what}: button {buttonId} \"{label}\" is not enabled.";
                return;
            }

            var evt = owner->AtkResNode.AtkEventManager.Event;
            if (evt == null)
            {
                Status = $"{what}: button {buttonId} \"{label}\" has no click event.";
                return;
            }

            unit->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, null);
            Status = $"{what}: pressed \"{label}\" ({window} button {buttonId}).";
        }
        catch (Exception ex)
        {
            Status = $"{what}: could not be pressed — {ex.Message}";
        }
    }

    /// <summary>A window that is on screen, or null.</summary>
    private AtkUnitBase* Window(string name)
    {
        try
        {
            var addon = _gameGui.GetAddonByName(name);
            return addon.IsNull || !addon.IsVisible ? null : (AtkUnitBase*)addon.Address;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>One of a window's values as a number; 0 when it is not there or is not one.</summary>
    private static uint Number(AtkUnitBase* unit, int index)
    {
        if (unit->AtkValues == null || index < 0 || index >= unit->AtkValuesCount)
            return 0;

        var value = unit->AtkValues[index];
        return value.Type is AtkValueType.UInt or AtkValueType.Int ? value.UInt : 0;
    }
}
