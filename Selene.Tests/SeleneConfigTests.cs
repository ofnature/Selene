namespace Selene.Tests;

public class SeleneConfigTests
{
    [Fact]
    public void AFreshConfig_IsCurrent_AndNeedsNoMigration()
    {
        var config = new SeleneConfig();

        Assert.Equal(SeleneConfig.CurrentVersion, config.Version);
        Assert.False(config.Migrate());
    }

    /// <summary>Multibox: one config for every client of a launcher, so a character's picks are its own.</summary>
    [Fact]
    public void EachCharacter_HasItsOwnSettings_MadeOnFirstUse()
    {
        var config = new SeleneConfig();

        config.For(1).PickedMissions.Add(1395);

        Assert.Equal(new uint[] { 1395 }, config.For(1).PickedMissions);
        Assert.Empty(config.For(2).PickedMissions);
        Assert.Equal(2, config.Characters.Count);
    }

    [Fact]
    public void AnOlderConfig_IsBroughtUpToDate_Once()
    {
        var config = new SeleneConfig { Version = 0 };

        Assert.True(config.Migrate());
        Assert.Equal(SeleneConfig.CurrentVersion, config.Version);
        Assert.False(config.Migrate());
    }
}
