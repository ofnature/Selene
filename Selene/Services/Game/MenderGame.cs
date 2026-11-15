using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Selene.Services.Game;

/// <summary>
/// The game's side of a repair at the mender: how worn the gear is, where the mender stands, and the
/// presses - interact, the menu's mender entry, Repair All, Yes, close. The calls are ICE's
/// (<c>Task_Repair.RepairAtNpc</c>) and Odysseus's (<c>GearRepair</c>); the numbers are checked where the
/// game's data can check them (docs/scoping-decisions.md, "Repair").
/// Fail-open: nothing pressed, and a Status saying why. Whether a press DID anything is for the caller to
/// read off the game. NONE OF IT HAS BEEN SEEN IN GAME when written (2026-11-15).
/// </summary>
public sealed unsafe class MenderGame
{
    private const string Menu = "SelectIconString";
    private const string Repair = "Repair";
    private const string YesNo = "SelectYesno";

    /// <summary>The repair window's Repair All (Odysseus's GearRepair; ICE fires the same for the gear worn).</summary>
    private const int RepairAllCallback = 0;

    private readonly IGameGui _gameGui;
    private readonly IObjectTable _objectTable;
    private readonly string _questionStart;
    private readonly string _questionEnd;
    private readonly string _menderOption;

    /// <param name="repairQuestion">
    /// The game's "Repair all displayed items for (N) gil?" (Addon 861) as the sheet has it, the figure left
    /// out: the question on screen has to start and end as that text does around the gap.
    /// </param>
    /// <param name="menderOption">The mender's entry in its NPC's menu, as the game's data has it (CustomTalk 720915's MainOption, "Repair Gear").</param>
    public MenderGame(IGameGui gameGui, IObjectTable objectTable, string repairQuestion, string menderOption = "Repair Gear")
    {
        _gameGui = gameGui;
        _objectTable = objectTable;
        _menderOption = menderOption.Trim().Length > 0 ? menderOption.Trim() : "Repair Gear";

        var text = repairQuestion.Trim();
        var gap = text.IndexOf("  ", StringComparison.Ordinal);
        _questionStart = gap > 0 ? text[..gap].Trim() : text;
        _questionEnd = gap > 0 ? text[(gap + 2)..].Trim() : string.Empty;
    }

    /// <summary>What was pressed last, or why nothing was.</summary>
    public string Status { get; private set; } = "Idle — nothing pressed.";

    /// <summary>What was found the last time the game was asked whether it is asking to pay for a repair.</summary>
    public string QuestionDetail { get; private set; } = "no question on screen";

    public bool MenuOpen => Window(Menu) != null;

    public bool RepairOpen => Window(Repair) != null;

    /// <summary>
    /// The lowest condition among the gear worn, in percent (30,000 is pristine) - read as Odysseus reads it.
    /// 100 when the game will not say: nothing is repaired on a bad read.
    /// </summary>
    public int GearPercent
    {
        get
        {
            try
            {
                var manager = InventoryManager.Instance();
                var container = manager == null ? null : manager->GetInventoryContainer(InventoryType.EquippedItems);
                if (container == null || !container->IsLoaded)
                    return 100;

                var lowest = 100;
                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0)
                        continue;

                    var percent = (int)Math.Round(slot->Condition / 300.0);
                    if (percent < lowest)
                        lowest = percent;
                }

                return lowest;
            }
            catch
            {
                return 100;
            }
        }
    }

    /// <summary>How far the mender stands from the character; null when it is not in sight or cannot be talked to.</summary>
    public float? Distance(uint npcId)
        => _objectTable.LocalPlayer is { } me && Find(npcId) is { } mender ? Vector3.Distance(me.Position, mender.Position) : null;

    /// <summary>Target the mender and interact, as a click on it does.</summary>
    public void Interact(uint npcId)
    {
        try
        {
            if (Find(npcId) is not { } mender)
            {
                Status = $"Interact: NPC {npcId} is not in sight.";
                return;
            }

            var targets = TargetSystem.Instance();
            if (targets == null)
            {
                Status = "Interact: the game's target system is not there.";
                return;
            }

            var thing = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)mender.Address;
            targets->Target = thing;
            targets->InteractWithObject(thing, true);
            Status = $"Interact: asked for \"{mender.Name.TextValue}\" (NPC {npcId}).";
        }
        catch (Exception ex)
        {
            Status = $"Interact: failed — {ex.Message}";
        }
    }

    // [SUPERSEDED 2026-11-15 — the first try in game: the menu has EIGHT entries (five shops, "Purchase Items",
    //  "Repair Gear", "Nothing"), the last being the game's own cancel - which is what was pressed, and the menu
    //  closed. The mender's entry is now found by its own text.]
    // /// <summary>
    // /// The menu's LAST entry. In the game's data the mender's menu is five shops, a gil shop and then the
    // /// mender (ICE presses entry 6 of 7); the last is taken so that a character with fewer shops open still
    // /// gets the mender. What it opened is the caller's to read: the repair window, or not.
    // /// </summary>
    // public void ChooseMender() { ... menu->FireCallbackInt(count - 1); ... }

    /// <summary>
    /// The menu's entry that reads as the mender's own handler does in the game's data (CustomTalk 720915,
    /// "Repair Gear", in the client's language) - wherever it sits (seen in game 2026-11-15: seventh of eight,
    /// before the game's own "Nothing"). With no such entry NOTHING is pressed, and the Status lists what the
    /// menu holds. What it opened is the caller's to read: the repair window, or not.
    /// </summary>
    public void ChooseMender()
    {
        try
        {
            var menu = Window(Menu);
            if (menu == null)
            {
                Status = "Mender entry: the menu is not open.";
                return;
            }

            var popup = &((AddonSelectIconString*)menu)->PopupMenu.PopupMenu;
            var count = popup->EntryCount;
            if (count <= 0 || popup->EntryNames == null)
            {
                Status = "Mender entry: the menu has no entries.";
                return;
            }

            var labels = new string[count];
            var found = -1;
            for (var i = 0; i < count; i++)
            {
                labels[i] = popup->EntryNames[i].ToString().Trim();
                if (found < 0 && string.Equals(labels[i], _menderOption, StringComparison.OrdinalIgnoreCase))
                    found = i;
            }

            if (found < 0)
            {
                Status = $"Mender entry: no entry reads \"{_menderOption}\" — NOT pressed. The menu holds: {string.Join(" | ", labels)}.";
                return;
            }

            menu->FireCallbackInt(found);
            Status = $"Mender entry: took entry {found + 1} of {count}, \"{labels[found]}\".";
        }
        catch (Exception ex)
        {
            Status = $"Mender entry: failed — {ex.Message}";
        }
    }

    public void RepairAll()
    {
        try
        {
            var repair = Window(Repair);
            if (repair == null)
            {
                Status = "Repair All: the repair window is not open.";
                return;
            }

            repair->FireCallbackInt(RepairAllCallback);
            Status = "Repair All: sent.";
        }
        catch (Exception ex)
        {
            Status = $"Repair All: failed — {ex.Message}";
        }
    }

    /// <summary>
    /// The game is asking "Repair all displayed items for N gil?" right now - told by the question's own
    /// text (the Addon sheet's, in the client's language), with the repair window up. Any other yes/no is not it.
    /// </summary>
    public bool RepairAsked()
    {
        try
        {
            var yesNo = Window(YesNo);
            if (yesNo == null)
            {
                QuestionDetail = "no question on screen";
                return false;
            }

            var asked = MissionWindows.QuestionText(yesNo);
            var ours = RepairOpen && _questionStart.Length > 0
                       && asked.StartsWith(_questionStart, StringComparison.Ordinal) && asked.EndsWith(_questionEnd, StringComparison.Ordinal);
            QuestionDetail = $"a question is up: \"{asked.Replace("\n", " / ").Replace("\r", string.Empty)}\"" + (ours ? " — the repair question" : " — NOT the repair question");
            return ours;
        }
        catch (Exception ex)
        {
            QuestionDetail = $"could not be read — {ex.Message}";
            return false;
        }
    }

    /// <summary>Yes to the repair's price - checked again here: that question and no other.</summary>
    public void ConfirmRepair()
    {
        try
        {
            if (!RepairAsked())
            {
                Status = "Confirm repair: the game is not asking about a repair — NOT answered.";
                return;
            }

            Window(YesNo)->FireCallbackInt(0);
            Status = "Confirm repair: answered Yes.";
        }
        catch (Exception ex)
        {
            Status = $"Confirm repair: failed — {ex.Message}";
        }
    }

    /// <summary>Close the repair window; with that gone, the mender's menu behind it.</summary>
    public void Close()
    {
        try
        {
            if (Window(Repair) is var repair && repair != null)
            {
                repair->Close(true);
                Status = "Close: closed the repair window.";
            }
            else if (Window(Menu) is var menu && menu != null)
            {
                menu->FireCallbackInt(-1);
                Status = "Close: closed the mender's menu.";
            }
            else
            {
                Status = "Close: nothing to close.";
            }
        }
        catch (Exception ex)
        {
            Status = $"Close: failed — {ex.Message}";
        }
    }

    private IGameObject? Find(uint npcId)
    {
        foreach (var thing in _objectTable)
        {
            if (thing.ObjectKind == ObjectKind.EventNpc && thing.BaseId == npcId && thing.IsTargetable)
                return thing;
        }

        return null;
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
}
