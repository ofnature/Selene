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

    // [SUPERSEDED 2026-11-14 — the user: "need to wire in the other missions, like ice does and have tabs to
    //  seperate them". The list is every mission of the job now; the Master ones are one tab of it.]
    // /// <summary>The user's decision: the Master missions of the job in hand, and nothing else, to pick from.</summary>
    // [Fact]
    // public void TheList_IsTheJobsMasterMissions_AndNoOtherJobsOrTiers()
    // {
    //     var rows = MissionBoard.Build(Catalog, State(Carpenter, 0, 0, 1395, 1396, 1397), Array.Empty<uint>());
    //
    //     Assert.Equal(new uint[] { 1395, 1396, 1397 }, rows.Select(row => row.Info.Id));
    //     Assert.All(rows, row => Assert.True(row.Offered));
    //     Assert.All(rows, row => Assert.False(row.Picked));
    // }

    /// <summary>Every mission of the job in hand, and no other job's; the Master ones are one tab of it.</summary>
    [Fact]
    public void TheList_IsEveryMissionOfTheJob_AndTheMasterTabIsItsMasterMissions()
    {
        var rows = MissionBoard.Build(Catalog, State(Carpenter, 0, 0, 1395, 1396, 1397), Array.Empty<uint>());

        Assert.Equal(new uint[] { 574, 1395, 1396, 1397 }, rows.Select(row => row.Info.Id));
        Assert.All(rows, row => Assert.False(row.Picked));

        var master = MissionBoard.In(rows, MissionCategory.Master);
        Assert.Equal(new uint[] { 1395, 1396, 1397 }, master.Select(row => row.Info.Id));
        Assert.All(master, row => Assert.True(row.Offered));
    }

    /// <summary>The tabs, as the game's data sorts the missions (read from disk 2026-11-14).</summary>
    [Theory]
    [InlineData(1, false, null, MissionCategory.RankD)]
    [InlineData(2, false, null, MissionCategory.RankC)]
    [InlineData(3, false, null, MissionCategory.RankB)]
    [InlineData(4, false, null, MissionCategory.RankA)]
    [InlineData(5, false, null, MissionCategory.RankA)] // EX is listed with A
    [InlineData(6, false, MissionCategory.Sequence, MissionCategory.Sequence)]
    [InlineData(5, false, MissionCategory.Weather, MissionCategory.Weather)]
    [InlineData(4, false, MissionCategory.Timed, MissionCategory.Timed)]
    [InlineData(1, false, MissionCategory.RedAlert, MissionCategory.RedAlert)]
    [InlineData(6, true, null, MissionCategory.Master)]
    public void AMission_GoesOnTheTabOfItsKind(int tier, bool master, MissionCategory? special, MissionCategory tab)
    {
        var mission = new MissionInfo(1, "A mission", Carpenter, tier, master, 0, 0, 0, Array.Empty<uint>(), special);

        Assert.Equal(tab, mission.Category);
    }

    /// <summary>
    /// The stars, by the row ranges (the game's data has no column for it): the last row of each star and
    /// the first of the next, and a mission past the last known star belongs to none - "Other" on the page.
    /// </summary>
    [Theory]
    [InlineData(1u, 1237u)]
    [InlineData(544u, 1237u)]
    [InlineData(545u, 1291u)]
    [InlineData(1039u, 1291u)]
    [InlineData(1040u, 1310u)]
    [InlineData(1369u, 1310u)]
    [InlineData(1370u, 1319u)]
    [InlineData(1395u, 1319u)] // Carpenter's Master missions: offered on Auxesia, seen in game
    [InlineData(1702u, 1319u)]
    [InlineData(1703u, 0u)]
    [InlineData(0u, 0u)]
    public void AMission_BelongsToTheStarOfItsRowRange(uint mission, uint territory)
        => Assert.Equal(territory, MissionStars.TerritoryOf(mission));

    [Fact]
    public void AStarsTab_HoldsOnlyItsOwnMissions()
    {
        var rows = MissionBoard.Build(Catalog, State(Carpenter, 0, 0, 1395), Array.Empty<uint>());

        Assert.Equal(new uint[] { 574 }, MissionBoard.On(rows, 1291).Select(row => row.Info.Id));
        Assert.Equal(new uint[] { 1395, 1396, 1397 }, MissionBoard.On(rows, 1319).Select(row => row.Info.Id));
        Assert.Empty(MissionBoard.On(rows, 1237));
    }

    // ---- what a mission wants unlocked, and the best done on it ------------------------------------------

    private const uint InscrutableTastes = 67631;
    private const uint NoLongerACollectable = 67633;

    private static readonly Dictionary<uint, MissionInfo> Collectables = new[]
    {
        Mission(1400, Carpenter) with { Needs = new[] { new QuestNeed(InscrutableTastes, "Inscrutable Tastes") } },
        Mission(1401, Carpenter) with
        {
            Needs = new[] { new QuestNeed(InscrutableTastes, "Inscrutable Tastes"), new QuestNeed(NoLongerACollectable, "No Longer a Collectable") },
        },
        Mission(1402, Carpenter),
    }.ToDictionary(mission => mission.Id);

    /// <summary>A quest the mission asks for and the character has not done: the mission is locked, and the row says which.</summary>
    [Fact]
    public void AMissionWhoseQuestIsNotDone_IsLocked_AndNamesTheQuest()
    {
        var state = State(Carpenter) with { QuestsDone = new[] { InscrutableTastes } };

        var rows = MissionBoard.Build(Collectables, state, Array.Empty<uint>());

        Assert.False(rows.Single(row => row.Info.Id == 1400).Locked);
        var reduction = rows.Single(row => row.Info.Id == 1401);
        Assert.True(reduction.Locked);
        Assert.Equal("No Longer a Collectable", Assert.Single(reduction.Missing!).QuestName);
        Assert.False(rows.Single(row => row.Info.Id == 1402).Locked);
    }

    /// <summary>Nothing is called locked on a guess: with the quest log unread, nothing is known to be missing.</summary>
    [Fact]
    public void WithTheQuestLogUnread_NoMissionIsCalledLocked()
    {
        var rows = MissionBoard.Build(Collectables, State(Carpenter), Array.Empty<uint>());

        Assert.All(rows, row => Assert.Null(row.Missing));
        Assert.All(rows, row => Assert.False(row.Locked));
    }

    [Fact]
    public void TheBestDone_IsGoldOverCompleted_AndUnknownOutOfTheZone()
    {
        var state = State(Carpenter) with { Completed = new uint[] { 1395, 1396 }, Golded = new uint[] { 1396 } };

        var rows = MissionBoard.Build(Catalog, state, Array.Empty<uint>());

        Assert.Equal(MissionBest.Completed, rows.Single(row => row.Info.Id == 1395).Best);
        Assert.Equal(MissionBest.Gold, rows.Single(row => row.Info.Id == 1396).Best);
        Assert.Equal(MissionBest.None, rows.Single(row => row.Info.Id == 1397).Best);
        Assert.All(MissionBoard.Build(Catalog, MissionState.NotInZone(Carpenter), Array.Empty<uint>()), row => Assert.Null(row.Best));
    }

    /// <summary>A Master mission's best is its high score, where the game holds one; elsewhere it is not known.</summary>
    [Fact]
    public void AMasterMissionsHighScore_IsCarriedToItsRow()
    {
        var state = State(Carpenter, 0, 0, 1395, 1396) with { MasterBest = new Dictionary<uint, uint> { [1395] = 17620, [1396] = 0 } };

        var rows = MissionBoard.Build(Catalog, state, Array.Empty<uint>());

        Assert.Equal(17620u, rows.Single(row => row.Info.Id == 1395).HighScore);
        Assert.Equal(0u, rows.Single(row => row.Info.Id == 1396).HighScore);
        Assert.Null(rows.Single(row => row.Info.Id == 1397).HighScore);
        Assert.All(MissionBoard.Build(Catalog, State(Carpenter, 0, 0, 1395), Array.Empty<uint>()), row => Assert.Null(row.HighScore));
    }

    /// <summary>What the game holds on its other lists is marked as offered too, on its own tab.</summary>
    [Fact]
    public void WhatTheGameHoldsOnItsOtherLists_IsMarkedOffered()
    {
        var state = State(Carpenter, 0, 0, 1395) with { OfferedOther = new uint[] { 574 } };

        var rows = MissionBoard.Build(Catalog, state, Array.Empty<uint>());

        Assert.True(rows.Single(row => row.Info.Id == 574).Offered);
        Assert.True(rows.Single(row => row.Info.Id == 1395).Offered);
        Assert.False(rows.Single(row => row.Info.Id == 1396).Offered);
        Assert.Equal(new uint[] { 574 }, MissionBoard.In(rows, MissionCategory.RankA).Select(row => row.Info.Id));
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
        var rows = MissionBoard.In(
            MissionBoard.Build(Catalog, State(Carpenter, current: 1396, bonus: 1397, 1395, 1396, 1397), new uint[] { 1395, 1396 }),
            MissionCategory.Master);

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

    // [SUPERSEDED 2026-11-14 — the EX+ mission IS listed now, on its own tab; what must hold is that it is not on the Master one]
    // /// <summary>The bug the first look in game found: every EX+ mission of the job was listed as Master.</summary>
    // [Fact]
    // public void AnExPlusMissionOfTheSameTier_IsNotListed()
    // {
    //     var rows = MissionBoard.Build(Catalog, State(Carpenter, 0, 0, 1395, 1396, 1397), Array.Empty<uint>());
    //
    //     Assert.DoesNotContain(rows, row => row.Info.Id == 574);
    // }

    /// <summary>The bug the first look in game found: every EX+ mission of the job was listed as Master.</summary>
    [Fact]
    public void AnExPlusMissionOfTheSameTier_IsNotOnTheMasterTab()
    {
        var rows = MissionBoard.Build(Catalog, State(Carpenter, 0, 0, 1395, 1396, 1397), Array.Empty<uint>());

        Assert.DoesNotContain(MissionBoard.In(rows, MissionCategory.Master), row => row.Info.Id == 574);
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
