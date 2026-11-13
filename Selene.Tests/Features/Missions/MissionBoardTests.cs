using Selene.Features.Missions;

namespace Selene.Tests.Features.Missions;

public class MissionBoardTests
{
    private const uint Carpenter = 8;
    private const uint Blacksmith = 9;

    private static MissionInfo Mission(uint id, uint job, bool master = true, string? name = null)
        => new(id, name ?? $"Mission {id}", job, 6, master, 300, 1000, 2500, new uint[] { 9000 + id });

    /// <summary>
    /// As the game has it: a job's Master missions share tier 6 with its EX+ missions (Carpenter's 574 is
    /// "EX+: Temporary Collection Points"), and only the sheet's flag tells them apart.
    /// </summary>
    private static readonly Dictionary<uint, MissionInfo> Catalog = new[]
    {
        Mission(1395, Carpenter), Mission(1396, Carpenter), Mission(1397, Carpenter),
        Mission(574, Carpenter, master: false, name: "An EX+ mission"),
        Mission(1423, Blacksmith), Mission(1424, Blacksmith), Mission(1425, Blacksmith),
    }.ToDictionary(mission => mission.Id);

    private static MissionState State(uint job, uint current = 0, uint bonus = 0, params uint[] offered)
        => new(true, 1319, job, current, 0, MissionRank.None, offered, bonus, 0);

    /// <summary>The user's decision: the Master missions of the job in hand, and nothing else, to pick from.</summary>
    [Fact]
    public void TheList_IsTheJobsMasterMissions_AndNoOtherJobsOrTiers()
    {
        var rows = MissionBoard.Build(Catalog, State(Carpenter, 0, 0, 1395, 1396, 1397), Array.Empty<uint>());

        Assert.Equal(new uint[] { 1395, 1396, 1397 }, rows.Select(row => row.Info.Id));
        Assert.All(rows, row => Assert.True(row.Offered));
        Assert.All(rows, row => Assert.False(row.Picked));
    }

    /// <summary>Out of the zone the game says nothing: the sheets' list is still there to pick from.</summary>
    [Fact]
    public void OutOfTheZone_TheSheetsListIsStillShown_AndNothingIsOffered()
    {
        var rows = MissionBoard.Build(Catalog, MissionState.NotInZone(Blacksmith), new uint[] { 1424 });

        Assert.Equal(new uint[] { 1423, 1424, 1425 }, rows.Select(row => row.Info.Id));
        Assert.All(rows, row => Assert.False(row.Offered));
        Assert.Equal(new[] { false, true, false }, rows.Select(row => row.Picked));
    }

    [Fact]
    public void Picks_Bonus_AndTheMissionInProgress_AreMarked()
    {
        var rows = MissionBoard.Build(Catalog, State(Carpenter, current: 1396, bonus: 1397, 1395, 1396, 1397), new uint[] { 1395, 1396 });

        Assert.Equal(new[] { true, true, false }, rows.Select(row => row.Picked));
        Assert.Equal(new[] { false, false, true }, rows.Select(row => row.Bonus));
        Assert.Equal(new[] { false, true, false }, rows.Select(row => row.Current));
    }

    /// <summary>
    /// If the reading of the sheets ever goes wrong again, the game's own offer must still show - as a row
    /// that is offered and NOT Master, which the page then calls out.
    /// </summary>
    [Fact]
    public void WhatTheGameOffers_IsListed_EvenWhenTheSheetsDoNotCallItMaster()
    {
        var rows = MissionBoard.Build(Catalog, State(Carpenter, 0, 0, 574, 1395), Array.Empty<uint>());

        var odd = Assert.Single(rows, row => row.Info.Id == 574);
        Assert.True(odd.Offered);
        Assert.False(odd.Info.IsMaster);
        Assert.Equal("An EX+ mission", odd.Info.Name);
        Assert.Equal(new uint[] { 574, 1395, 1396, 1397 }, rows.Select(row => row.Info.Id));
    }

    /// <summary>The bug the first look in game found: every EX+ mission of the job was listed as Master.</summary>
    [Fact]
    public void AnExPlusMissionOfTheSameTier_IsNotListed()
    {
        var rows = MissionBoard.Build(Catalog, State(Carpenter, 0, 0, 1395, 1396, 1397), Array.Empty<uint>());

        Assert.DoesNotContain(rows, row => row.Info.Id == 574);
    }

    [Fact]
    public void AMissionTheSheetsDoNotKnow_IsListedByItsNumber()
    {
        var rows = MissionBoard.Build(Catalog, State(Carpenter, 0, 0, 4242), Array.Empty<uint>());

        var unknown = Assert.Single(rows, row => row.Info.Id == 4242);
        Assert.Equal("Mission 4242", unknown.Info.Name);
        Assert.True(unknown.Offered);
        Assert.Empty(unknown.Info.RecipeIds);
    }

    [Fact]
    public void AJobWithNoMasterMissions_HasAnEmptyList()
        => Assert.Empty(MissionBoard.Build(Catalog, MissionState.NotInZone(16), Array.Empty<uint>()));

    [Theory]
    [InlineData(7u, false)]
    [InlineData(8u, true)]
    [InlineData(15u, true)]
    [InlineData(16u, false)]
    public void CraftersAreJobsEightToFifteen(uint job, bool crafter)
        => Assert.Equal(crafter, MissionInfo.IsCrafter(job));
}
