using System.Collections.Generic;
using System.Linq;

namespace Selene.Features.Missions;

/// <summary>One mission on the Missions page.</summary>
/// <param name="Picked">The user ticked it: Selene may run it (once running missions is built).</param>
/// <param name="Offered">The game offers it to this job right now.</param>
/// <param name="Bonus">The game marks it as the bonus Master mission.</param>
/// <param name="Current">It is the mission in progress.</param>
/// <param name="Best">The best the character has done on it, by the game's mission log; null = the game was not asked (not in a Cosmic zone).</param>
/// <param name="Missing">The quests the mission asks for that the character has not done; empty = none; null = the quests were not read.</param>
/// <param name="HighScore">A Master mission's highest score, as the game keeps it; null = not read, or not a Master mission the game holds.</param>
/// <param name="Payout">What the mission has been measured to pay in class points; null = never measured.</param>
public sealed record MissionRow(
    MissionInfo Info, bool Picked, bool Offered, bool Bonus, bool Current, MissionBest? Best = null,
    IReadOnlyList<QuestNeed>? Missing = null, uint? HighScore = null, PayoutSummary? Payout = null, bool? GameLocked = null)
{
    // [SUPERSEDED 2026-11-14 — the quest log read "Inscrutable Tastes" as done on a Fisher whose player said it
    //  was not. The game's own answer for the mission's unlock function goes first now; the quest log only
    //  where the game was not asked.]
    // /// <summary>A quest it asks for is known to be missing: the game will not let the character take it.</summary>
    // public bool Locked => Missing is { Count: > 0 };

    /// <summary>
    /// The mission cannot be taken: the game says its unlock function is locked (<see cref="GameLocked"/>) - or,
    /// where the game was not asked, the quest log reads a quest it wants as not done.
    /// </summary>
    public bool Locked => GameLocked ?? Missing is { Count: > 0 };
}

/// <summary>The best result the game's mission log holds for a mission.</summary>
public enum MissionBest
{
    /// <summary>Never completed.</summary>
    None,

    /// <summary>Completed, but not with a gold star.</summary>
    Completed,

    Gold,
}

/// <summary>
/// The pick list for one job: its missions as the sheets name them, TOGETHER WITH whatever the game itself
/// offers. Listing both means a wrong reading of the sheets (or of the game) is seen, never hidden - which
/// is how ICE's "tier 6 = Master" was caught on 2026-11-12, the first time the page was looked at. Pure.
/// </summary>
public static class MissionBoard
{
    // [SUPERSEDED 2026-11-14 — the user: "need to wire in the other missions, like ice does and have tabs to
    //  seperate them". The list was the job's Master missions only; it is every mission of the job now, and the
    //  page shows one MissionCategory at a time.]
    // public static IReadOnlyList<MissionRow> Build(
    //     IReadOnlyDictionary<uint, MissionInfo> catalog, MissionState state, IReadOnlyCollection<uint> picked)
    // {
    //     var ids = catalog.Values
    //         .Where(mission => mission.IsMaster && mission.ClassJobId == state.ClassJobId)
    //         .Select(mission => mission.Id)
    //         .Concat(state.OfferedMaster)
    //         .Where(id => id != 0)
    //         .Distinct()
    //         .OrderBy(id => id);
    //
    //     return ids
    //         .Select(id => new MissionRow(
    //             catalog.TryGetValue(id, out var info) ? info : MissionInfo.Unknown(id, state.ClassJobId),
    //             picked.Contains(id),
    //             state.OfferedMaster.Contains(id),
    //             state.BonusMasterId == id,
    //             state.CurrentMissionId == id))
    //         .ToList();
    // }

    /// <summary>Every mission of the job, in id order. The page shows them a <see cref="MissionCategory"/> at a time.</summary>
    /// <param name="payouts">What each mission has been measured to pay (the payout log); null = none known.</param>
    public static IReadOnlyList<MissionRow> Build(
        IReadOnlyDictionary<uint, MissionInfo> catalog, MissionState state, IReadOnlyCollection<uint> picked,
        IReadOnlyDictionary<uint, PayoutSummary>? payouts = null)
    {
        var offered = state.Offered.Where(id => id != 0).ToHashSet();
        var ids = catalog.Values
            .Where(mission => mission.ClassJobId == state.ClassJobId)
            .Select(mission => mission.Id)
            .Concat(offered)
            .Distinct()
            .OrderBy(id => id);

        return ids
            .Select(id => new MissionRow(
                catalog.TryGetValue(id, out var info) ? info : MissionInfo.Unknown(id, state.ClassJobId),
                picked.Contains(id),
                offered.Contains(id),
                state.BonusMasterId == id,
                state.CurrentMissionId == id,
                state.Golded == null || state.Completed == null ? null
                    : state.Golded.Contains(id) ? MissionBest.Gold
                    : state.Completed.Contains(id) ? MissionBest.Completed
                    : MissionBest.None,
                Missing(catalog.TryGetValue(id, out var known) ? known : null, state.QuestsDone),
                state.MasterBest != null && state.MasterBest.TryGetValue(id, out var high) ? high : null,
                payouts != null && payouts.TryGetValue(id, out var payout) ? payout : null,
                GameLocked(catalog.TryGetValue(id, out var named) ? named : null, state.Functions)))
            .ToList();
    }

    /// <summary>The game's own answer: true = the mission's unlock function is locked; null = it names none, or the game was not asked.</summary>
    public static bool? GameLocked(MissionInfo? mission, IReadOnlyDictionary<uint, bool>? functions)
        => mission is { UnlockFunction: not 0 } && functions != null && functions.TryGetValue(mission.UnlockFunction, out var unlocked)
            ? !unlocked
            : null;

    /// <summary>The quests a mission asks for that are not done; null when the quests were not read.</summary>
    public static IReadOnlyList<QuestNeed>? Missing(MissionInfo? mission, IReadOnlyCollection<uint>? questsDone)
    {
        if (questsDone == null)
            return null;

        return mission?.Needs == null
            ? System.Array.Empty<QuestNeed>()
            : mission.Needs.Where(need => !questsDone.Contains(need.QuestId)).ToList();
    }

    /// <summary>The rows of one tab.</summary>
    public static IReadOnlyList<MissionRow> In(IReadOnlyList<MissionRow> rows, MissionCategory category)
        => rows.Where(row => row.Info.Category == category).ToList();

    /// <summary>The rows of one star (by its territory; 0 = the missions no star is known for).</summary>
    public static IReadOnlyList<MissionRow> On(IReadOnlyList<MissionRow> rows, uint territoryId)
        => rows.Where(row => MissionStars.TerritoryOf(row.Info.Id) == territoryId).ToList();
}

/// <summary>
/// Which star a mission belongs to (the user, 2026-11-14: "need the mission seperated by moon"). THE GAME'S
/// DATA HAS NO COLUMN FOR IT (looked for that day: no column of WKSMissionUnit is constant a star, and most
/// missions carry no place name), so this is the row-id ranges ICE falls back on, hard-coded. What supports
/// them: the missions come in four blocks of the same shape (the same counts a job and tier in each), every
/// Master mission lies in the last one, and the game offered Carpenter's 1395-1397 on Auxesia.
/// A NEW STAR needs a row here: until then its missions belong to no star (territory 0) and the page shows
/// them on an "Other" tab, so they are not hidden.
/// </summary>
public static class MissionStars
{
    /// <summary>Each star's territory and the last mission row that is its own, in order.</summary>
    public static readonly IReadOnlyList<(uint TerritoryId, uint LastMission)> Stars = new (uint, uint)[]
    {
        (1237, 544),   // Sinus Ardorum
        (1291, 1039),  // Phaenna
        (1310, 1369),  // Oizys
        (1319, 1702),  // Auxesia
    };

    /// <summary>The star's place in the order they were reached (0 = Sinus Ardorum); -1 when no star is known.</summary>
    public static int IndexOf(uint missionId)
    {
        if (missionId == 0)
            return -1;

        for (var i = 0; i < Stars.Count; i++)
        {
            if (missionId <= Stars[i].LastMission)
                return i;
        }

        return -1;
    }

    public static uint TerritoryOf(uint missionId)
    {
        if (missionId == 0)
            return 0;

        foreach (var (territory, last) in Stars)
        {
            if (missionId <= last)
                return territory;
        }

        return 0;
    }
}
