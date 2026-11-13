using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.Game.WKS;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Selene.Features.Missions;

namespace Selene.Services.Game;

/// <summary>
/// The live mission state, READ ONLY: the mission in progress, its score and rank, and the Master missions
/// the game offers the job - all off <c>WKSManager</c>, which ClientStructs now maps (ICE's own signature
/// scans are not needed). Fail-open: "not in a Cosmic zone" when the game will not say. Nothing here
/// accepts, reports or abandons anything.
/// </summary>
public sealed unsafe class MissionReader
{
    /// <summary>The game keeps one block of mission state per Cosmic job, 11 of them: Carpenter (8) to Fisher (18).</summary>
    private const uint FirstCosmicJob = 8;

    /// <summary>What was read last, or why nothing was.</summary>
    public string Status { get; private set; } = "Idle — nothing read yet.";

    public MissionState Read(uint classJobId)
    {
        try
        {
            var manager = WKSManager.Instance();
            if (manager == null || !manager->IsLoaded)
            {
                Status = "Idle — not in a Cosmic Exploration zone.";
                return MissionState.NotInZone(classJobId);
            }

            ref var state = ref manager->State;
            var current = state.CurrentMission;

            var offered = new List<uint>();
            uint bonus = 0;
            var classScore = 0;
            var jobs = state.JobStates;
            var index = (int)classJobId - (int)FirstCosmicJob;
            var hasJob = index >= 0 && index < jobs.Length;
            if (hasJob)
            {
                ref var job = ref jobs[index];
                foreach (ref var master in job.MasterMissions)
                {
                    if (master.MissionUnitId != 0)
                        offered.Add(master.MissionUnitId);
                }

                bonus = job.BonusMasterMissionUnitId;
                classScore = state.Scores[index];
            }

            var clock = current.MissionUnitRowId != 0 ? ReadClock(out var clockDetail) : NoClock(out clockDetail);

            Status = $"zone {manager->TerritoryId} · job {classJobId} = state block {(hasJob ? index.ToString() : "none")}"
                     + $" · Master offered: {(offered.Count == 0 ? "none" : string.Join(", ", offered))} (bonus {bonus})"
                     + $" · current mission {current.MissionUnitRowId}, score {current.ScoreUInt}, rank {(int)current.Rank}"
                     + $" · class score {classScore} · clock: {clockDetail}";

            return new MissionState(
                true, manager->TerritoryId, classJobId, current.MissionUnitRowId, current.ScoreUInt,
                Enum.IsDefined(typeof(MissionRank), (int)current.Rank) ? (MissionRank)(int)current.Rank : MissionRank.None,
                offered, bonus, classScore, clock);
        }
        catch (Exception ex)
        {
            Status = $"The mission state could not be read — {ex.Message}";
            return MissionState.NotInZone(classJobId);
        }
    }

    private static int? NoClock(out string detail)
    {
        detail = "no mission";
        return null;
    }

    /// <summary>
    /// The mission's clock: the game shows it as one of the "to-do" entries of the zone's director, and that
    /// entry carries the moment it ends. ICE reads entry [1][1] blind; here every enabled entry is looked at
    /// and the one whose end lies within a day of the server's clock is taken - and all of them are put in
    /// the Status, so a wrong pick can be seen. UNVERIFIED IN GAME when written (2026-11-12).
    /// </summary>
    private static int? ReadClock(out string detail)
    {
        var director = UIState.Instance()->MassivePcContentTodo.Director;
        if (director == null)
        {
            detail = "no director";
            return null;
        }

        var now = Framework.GetServerTime();
        int? clock = null;
        var seen = new List<string>();
        var sets = director->MassivePcContentTodos;
        for (var set = 0; set < sets.Length; set++)
        {
            var todos = sets[set];
            for (var i = 0; i < todos.Count; i++)
            {
                var todo = todos[i];
                if (!todo.Enabled)
                    continue;

                var left = todo.EndTimestamp - now;
                var plausible = left > -3600 && left < 86400;
                seen.Add($"[{set}][{i}] type {(int)todo.Type}{(plausible ? $" ends in {left} s" : string.Empty)}");
                if (plausible && clock == null)
                    clock = (int)Math.Max(0, left);
            }
        }

        detail = seen.Count == 0 ? "no enabled entry" : string.Join("; ", seen);
        return clock;
    }
}
