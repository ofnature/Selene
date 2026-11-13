using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Selene.Features.Characters;

/// <summary>
/// The characters this PC has seen logged in with Selene loaded: one <c>characters/{contentId}.json</c>
/// each under the plugin's config directory, holding the name.
///
/// Deliberately NOT in <see cref="SeleneConfig"/>: that file is shared by every client launched from
/// the same XIVLauncher and the last save wins, so two clients logging in together would lose one of
/// the two. A file per content id means each client only ever writes its own, and reads the others'.
/// </summary>
public sealed class CharacterRoster
{
    private readonly string _directory;
    private Dictionary<ulong, string> _names = new();

    public CharacterRoster(string configDirectory)
    {
        _directory = Path.Combine(configDirectory, "characters");
    }

    /// <summary>Content id to name, as of the last <see cref="Reload"/> and <see cref="Remember"/>.</summary>
    public IReadOnlyDictionary<ulong, string> Names => _names;

    /// <summary>What the roster knows, or why it knows nothing.</summary>
    public string Status { get; private set; } = "Idle — nothing read yet.";

    public string PathFor(ulong contentId) => Path.Combine(_directory, $"{contentId:X16}.json");

    /// <summary>Read every character file again: this is how another client's character turns up.</summary>
    public void Reload()
    {
        var names = new Dictionary<ulong, string>();
        var unreadable = 0;

        if (Directory.Exists(_directory))
        {
            foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
            {
                if (!ulong.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var contentId))
                    continue;

                try
                {
                    var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path));
                    if (!string.IsNullOrWhiteSpace(entry?.Name))
                        names[contentId] = entry.Name;
                    else
                        unreadable++;
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    unreadable++; // junk, or another client's file in mid-write: it is read at the next reload
                }
            }
        }

        _names = names;
        Status = names.Count == 0 && unreadable == 0
            ? "Idle — no character has logged in with Selene loaded yet."
            : $"{names.Count} known" + (unreadable > 0 ? $", {unreadable} unreadable" : string.Empty);
    }

    /// <summary>Note the logged-in character. Writes only when it is new or was renamed.</summary>
    public void Remember(ulong contentId, string name)
    {
        if (contentId == 0 || string.IsNullOrWhiteSpace(name))
            return;
        if (_names.TryGetValue(contentId, out var known) && known == name)
            return;

        try
        {
            // Write-then-rename, so a reader never meets a half-written file.
            Directory.CreateDirectory(_directory);
            var path = PathFor(contentId);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Entry(name)));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not save {name} — {ex.Message}";
            return;
        }

        _names[contentId] = name;
        Status = $"{_names.Count} known";
    }

    private sealed record Entry(string Name);
}
