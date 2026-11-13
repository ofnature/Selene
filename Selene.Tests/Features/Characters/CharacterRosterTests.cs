using Selene.Features.Characters;

namespace Selene.Tests.Features.Characters;

public sealed class CharacterRosterTests : IDisposable
{
    private const ulong Mene = 0x0040_0000_1234_5678UL;
    private const ulong Pandia = 0x0040_0000_8765_4321UL;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "selene-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void NothingOnDisk_IsAnEmptyRoster_ThatSaysWhy()
    {
        var roster = new CharacterRoster(_directory);

        roster.Reload();

        Assert.Empty(roster.Names);
        Assert.StartsWith("Idle — ", roster.Status);
    }

    [Fact]
    public void ARememberedCharacter_IsThereAfterAReload_AndInAFileOfItsOwn()
    {
        var roster = new CharacterRoster(_directory);

        roster.Remember(Mene, "Mene");

        Assert.True(File.Exists(roster.PathFor(Mene)));
        Assert.EndsWith("0040000012345678.json", roster.PathFor(Mene));

        var afterRestart = new CharacterRoster(_directory);
        afterRestart.Reload();
        Assert.Equal("Mene", afterRestart.Names[Mene]);
    }

    /// <summary>The multibox case: two clients, one config directory - each writes its own, both see both.</summary>
    [Fact]
    public void TwoClients_EachWriteTheirOwn_AndSeeEachOtherOnReload()
    {
        var one = new CharacterRoster(_directory);
        var other = new CharacterRoster(_directory);

        one.Remember(Mene, "Mene");
        other.Remember(Pandia, "Pandia");
        one.Reload();
        other.Reload();

        Assert.Equal(2, one.Names.Count);
        Assert.Equal(2, other.Names.Count);
        Assert.Equal("2 known", one.Status);
    }

    [Fact]
    public void ARename_IsWritten_AndTheSameNameAgain_IsNot()
    {
        var roster = new CharacterRoster(_directory);
        roster.Remember(Mene, "Mene");
        var written = File.GetLastWriteTimeUtc(roster.PathFor(Mene));
        File.SetLastWriteTimeUtc(roster.PathFor(Mene), written.AddHours(-1));

        roster.Remember(Mene, "Mene");
        Assert.Equal(written.AddHours(-1), File.GetLastWriteTimeUtc(roster.PathFor(Mene)));

        roster.Remember(Mene, "Mene Renamed");
        roster.Reload();
        Assert.Equal("Mene Renamed", roster.Names[Mene]);
    }

    /// <summary>Not logged in, or the character not in the world yet: nothing to remember, nothing written.</summary>
    [Fact]
    public void NobodyAndNoName_AreNotRemembered()
    {
        var roster = new CharacterRoster(_directory);

        roster.Remember(0, "Mene");
        roster.Remember(Mene, string.Empty);

        Assert.Empty(roster.Names);
        Assert.False(Directory.Exists(Path.Combine(_directory, "characters")));
    }

    /// <summary>A file that is junk, or not a character's, costs that one entry - never the roster.</summary>
    [Fact]
    public void JunkFiles_AreCounted_AndTheRestIsRead()
    {
        var roster = new CharacterRoster(_directory);
        roster.Remember(Mene, "Mene");
        File.WriteAllText(roster.PathFor(Pandia), "{ not json");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(roster.PathFor(Pandia))!, "notes.json"), "{}");

        roster.Reload();

        Assert.Equal("Mene", Assert.Single(roster.Names).Value);
        Assert.Equal("1 known, 1 unreadable", roster.Status);
    }
}
