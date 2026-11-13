using System.Collections.Generic;
using System.Linq;

namespace Selene.Features.Missions;

/// <summary>One mission on the Missions page.</summary>
/// <param name="Picked">The user ticked it: Selene may run it (once running missions is built).</param>
/// <param name="Offered">The game offers it to this job right now.</param>
/// <param name="Bonus">The game marks it as the bonus Master mission.</param>
/// <param name="Current">It is the mission in progress.</param>
public sealed record MissionRow(MissionInfo Info, bool Picked, bool Offered, bool Bonus, bool Current);

/// <summary>
/// The pick list for one job: its Master missions as the sheets name them, TOGETHER WITH whatever the game
/// itself offers as one. The two should be the same three; listing both means a wrong reading of the
/// sheets (or of the game) is seen, never hidden - which is how ICE's "tier 6 = Master" was caught on
/// 2026-11-12, the first time the page was looked at. Pure.
/// </summary>
public static class MissionBoard
{
    public static IReadOnlyList<MissionRow> Build(
        IReadOnlyDictionary<uint, MissionInfo> catalog, MissionState state, IReadOnlyCollection<uint> picked)
    {
        var ids = catalog.Values
            .Where(mission => mission.IsMaster && mission.ClassJobId == state.ClassJobId)
            .Select(mission => mission.Id)
            .Concat(state.OfferedMaster)
            .Where(id => id != 0)
            .Distinct()
            .OrderBy(id => id);

        return ids
            .Select(id => new MissionRow(
                catalog.TryGetValue(id, out var info) ? info : MissionInfo.Unknown(id, state.ClassJobId),
                picked.Contains(id),
                state.OfferedMaster.Contains(id),
                state.BonusMasterId == id,
                state.CurrentMissionId == id))
            .ToList();
    }
}
