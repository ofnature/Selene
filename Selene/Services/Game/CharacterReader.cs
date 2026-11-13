using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace Selene.Services.Game;

/// <summary>Who is logged in on this client. Fail-open: nobody, when the game will not say.</summary>
public sealed unsafe class CharacterReader
{
    private readonly IObjectTable _objectTable;

    public CharacterReader(IObjectTable objectTable)
    {
        _objectTable = objectTable;
    }

    /// <summary>
    /// The logged-in character's content id (0 when not logged in) - the per-character key for
    /// everything Selene keeps. Read as Hephaestus's AccountReader reads it.
    /// </summary>
    public ulong ContentId
    {
        get
        {
            try
            {
                var playerState = PlayerState.Instance();
                return playerState == null ? 0 : playerState->ContentId;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>The character's name; empty while there is no character in the world (a zone change too).</summary>
    public string Name => _objectTable.LocalPlayer?.Name.TextValue ?? string.Empty;

    /// <summary>Who it sees, or why it sees nobody.</summary>
    public string Status
    {
        get
        {
            if (ContentId == 0)
                return "Idle — not logged in.";

            var name = Name;
            return name.Length == 0 ? "Logged in — the character is not in the world yet." : $"Logged in as {name}";
        }
    }
}
