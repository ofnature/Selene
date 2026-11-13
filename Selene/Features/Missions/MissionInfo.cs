using System;
using System.Collections.Generic;

namespace Selene.Features.Missions;

/// <summary>What the game's sheets say of one Cosmic Exploration mission (WKSMissionUnit).</summary>
/// <param name="ClassJobId">The job the mission belongs to (8 = Carpenter ... 15 = Culinarian are the crafters).</param>
/// <param name="LevelGroup">The mission's tier: 1-4 = D to A, 5 = EX, 6 = EX+ AND the Master missions.</param>
/// <param name="IsMaster">
/// A tool mastery ("Master") mission: the sheet's own flag (<c>WKSMissionUnit.Unknown2</c>). Checked against
/// the game's data on 2026-11-12: 33 rows carry it, three a job for the eleven Cosmic jobs, every one named
/// "Master: ..." and none without it - and the three it gives Carpenter are the three the game's Tool Mastery
/// tab showed.
/// </param>
/// <param name="TimeLimitSeconds">The clock a mission starts with; checkpoints add to it. 0 = the mission is not timed.</param>
/// <param name="RecipeIds">The recipes the mission is made with, in the sheet's order; empty for a gatherer's.</param>
public sealed record MissionInfo(
    uint Id, string Name, uint ClassJobId, int LevelGroup, bool IsMaster, int TimeLimitSeconds, uint SilverScore, uint GoldScore,
    IReadOnlyList<uint> RecipeIds)
{
    // [SUPERSEDED 2026-11-12 by the IsMaster flag — ICE's "tier 6 = Master" is wrong: tier 6 is EX+ too
    //  (the user's screenshot: 22 EX+ missions listed beside Carpenter's three Master ones)]
    // public const int MasterLevelGroup = 6;
    // public bool IsMaster => LevelGroup == MasterLevelGroup;

    public const uint FirstCrafter = 8;
    public const uint LastCrafter = 15;

    public static bool IsCrafter(uint classJobId) => classJobId is >= FirstCrafter and <= LastCrafter;

    /// <summary>A mission the sheets do not know (or could not be read): still shown, by its number.</summary>
    public static MissionInfo Unknown(uint id, uint classJobId)
        => new(id, $"Mission {id}", classJobId, 0, false, 0, 0, 0, Array.Empty<uint>());
}

/// <summary>The game's own ranks for a mission in progress (WKSMissionModule.MissionRank).</summary>
public enum MissionRank
{
    None = 0,
    Bronze = 1,
    Silver = 2,
    Gold = 3,
    Failed = 5,
}

/// <summary>
/// The live mission state of the logged-in character's current job, read off the game each tick.
/// </summary>
/// <param name="InZone">The Cosmic Exploration manager is loaded: the character is in a Cosmic zone.</param>
/// <param name="TerritoryId">The zone, when <paramref name="InZone"/>.</param>
/// <param name="ClassJobId">The job the rest was read for.</param>
/// <param name="CurrentMissionId">The mission in progress; 0 = none.</param>
/// <param name="OfferedMaster">The Master missions the game offers this job right now (zeros left out).</param>
/// <param name="BonusMasterId">The Master mission marked as the bonus one; 0 = none.</param>
/// <param name="ClassScore">The job's Cosmic class score.</param>
/// <param name="ClockSeconds">What is left on the mission's clock; null = no running clock was found.</param>
public sealed record MissionState(
    bool InZone,
    uint TerritoryId,
    uint ClassJobId,
    uint CurrentMissionId,
    uint Score,
    MissionRank Rank,
    IReadOnlyList<uint> OfferedMaster,
    uint BonusMasterId,
    int ClassScore,
    int? ClockSeconds = null)
{
    public static MissionState NotInZone(uint classJobId)
        => new(false, 0, classJobId, 0, 0, MissionRank.None, Array.Empty<uint>(), 0, 0);

    public bool InMission => CurrentMissionId != 0;
}
