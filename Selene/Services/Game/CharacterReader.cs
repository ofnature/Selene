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

    /// <summary>The job the character is on; 0 while there is no character in the world.</summary>
    public uint ClassJobId => _objectTable.LocalPlayer?.ClassJob.RowId ?? 0;

    /// <summary>Where the character stands; null while there is no character in the world.</summary>
    public System.Numerics.Vector3? Position => _objectTable.LocalPlayer?.Position;

    /// <summary>Which way the character faces (the game's rotation); 0 while there is no character.</summary>
    public float Rotation => _objectTable.LocalPlayer?.Rotation ?? 0f;

    /// <summary>The zone the character is in, any zone (the mission reader only knows the Cosmic ones); 0 when the game will not say.</summary>
    public uint TerritoryId
    {
        get
        {
            try
            {
                var game = FFXIVClientStructs.FFXIV.Client.Game.GameMain.Instance();
                return game == null ? 0 : game->CurrentTerritoryTypeId;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>The character is on its Home World; false while there is no character in the world.</summary>
    public bool OnHomeWorld
        => _objectTable.LocalPlayer is { } player && player.HomeWorld.RowId != 0 && player.HomeWorld.RowId == player.CurrentWorld.RowId;

    /// <summary>
    /// Turn the character to a rotation, as Demeter turns it to the water (<c>GameObject.SetRotation</c>).
    /// Whether it turned is read back from <see cref="Rotation"/>. Only the walk test calls this.
    /// </summary>
    public void Face(float rotation)
    {
        try
        {
            if (_objectTable.LocalPlayer is { } player)
                ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address)->SetRotation(rotation);
        }
        catch
        {
            // Fail-open: the caller reads the rotation and sees it did not turn.
        }
    }

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
