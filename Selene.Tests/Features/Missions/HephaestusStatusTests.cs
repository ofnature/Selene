using Selene.Features.Missions;

namespace Selene.Tests.Features.Missions;

public class HephaestusStatusTests
{
    /// <summary>The shape Hephaestus publishes since 2026-11-12 (QueueStatusDto, with the three gear fields).</summary>
    private const string Full = """
        {"Active":false,"CanResume":true,"Status":"gear is at 12% (gate 20%) — repair, then resume","External":true,
         "CurrentRecipeId":0,"CurrentCrafted":0,"CurrentQuantity":0,"ItemsCompleted":0,"ItemsSkipped":0,"Flagged":[],
         "RunHalt":"GearGate","GearConditionPercent":12,"GearGatePercent":20}
        """;

    [Fact]
    public void TheGearGate_IsReadAsHephaestusReportsIt()
    {
        var status = HephaestusStatus.Parse(Full, busy: false);

        Assert.True(status.Available);
        Assert.False(status.Busy);
        Assert.True(status.CanResume);
        Assert.Equal("GearGate", status.RunHalt);
        Assert.Equal(12, status.GearConditionPercent);
        Assert.Equal(20, status.GearGatePercent);
        Assert.True(status.ReportsGear);
        Assert.False(status.GearFit);
        Assert.StartsWith("gear is at 12%", status.Status);
    }

    /// <summary>Hephaestus's own rule: exactly at the gate still crafts.</summary>
    [Theory]
    [InlineData(19, false)]
    [InlineData(20, true)]
    [InlineData(100, true)]
    public void GearIsFit_AtTheGateAndAbove(int condition, bool fit)
        => Assert.Equal(fit, HephaestusStatus.Parse($$"""{"GearConditionPercent":{{condition}},"GearGatePercent":20}""", busy: false).GearFit);

    /// <summary>An older Hephaestus sends no gear fields: that is "cannot tell", and never "fit".</summary>
    [Fact]
    public void AnOlderHephaestus_DoesNotReportGear_AndIsNotCalledFit()
    {
        var status = HephaestusStatus.Parse("""{"Active":true,"CanResume":false,"Status":"Crafting 3 / 12"}""", busy: true);

        Assert.True(status.Available);
        Assert.True(status.Busy);
        Assert.False(status.ReportsGear);
        Assert.False(status.GearFit);
        Assert.Equal("None", status.RunHalt);
    }

    /// <summary>Its gate answers "{}" on any error of its own, and nothing it sends may throw into our tick.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"GearConditionPercent":"twelve","RunHalt":7,"Status":null}""")]
    public void AnythingUnreadable_IsALoadedHephaestusThatSaidNothing(string? json)
    {
        var status = HephaestusStatus.Parse(json, busy: false);

        Assert.True(status.Available);
        Assert.False(status.ReportsGear);
        Assert.False(status.GearFit);
        Assert.Equal("None", status.RunHalt);
    }

    [Fact]
    public void AnAbsentHephaestus_IsNotAvailable_AndItsGearIsNotFit()
    {
        Assert.False(HephaestusStatus.Absent.Available);
        Assert.False(HephaestusStatus.Absent.GearFit);
    }
}
