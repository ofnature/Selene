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

    [Fact]
    public void AnOlderConfig_IsBroughtUpToDate_Once()
    {
        var config = new SeleneConfig { Version = 0 };

        Assert.True(config.Migrate());
        Assert.Equal(SeleneConfig.CurrentVersion, config.Version);
        Assert.False(config.Migrate());
    }
}
