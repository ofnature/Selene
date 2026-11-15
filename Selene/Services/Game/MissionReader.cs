using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
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

    /// <summary>Where a Master mission's state keeps the number taken to be its high score.</summary>
    private const int MasterHighScoreOffset = 12;

    /// <summary>What was read last, or why nothing was.</summary>
    public string Status { get; private set; } = "Idle — nothing read yet.";

    /// <summary>Each quest asked about, and everything the game says of it: for the Debug page.</summary>
    public string QuestDetail { get; private set; } = "Idle — no quest asked about yet.";

    private static string Flag(bool value) => value ? "yes" : "no";

    /// <summary>
    /// What the GAME says of each of the job's quest-unlocked actions right now: its own status for the action
    /// (0 = it could be used this moment; anything else is the game's reason code - not learned, wrong job,
    /// not while doing this...). For the Debug page: added 2026-11-14, when the game told a Fisher "Collect is
    /// required for this mission" although the quest log read Collect's quest as done. Only meaningful for the
    /// job the character is ON.
    /// </summary>
    public string ActionDetail(IReadOnlyList<ActionNeed> actions)
    {
        try
        {
            var manager = ActionManager.Instance();
            if (manager == null)
                return "the game's action manager is not there";

            var parts = new List<string>();
            foreach (var action in actions)
            {
                if (action.ActionId == 0)
                    continue;

                var status = manager->GetActionStatus(action.Craft ? ActionType.CraftAction : ActionType.Action, action.ActionId);
                parts.Add($"{action.Action} ({action.ActionId}): {(status == 0 ? "usable now" : $"status {status}")}");
            }

            return parts.Count == 0 ? "this job has no quest-unlocked action" : string.Join(" · ", parts);
        }
        catch (Exception ex)
        {
            return $"could not be read — {ex.Message}";
        }
    }

    /// <param name="missionIds">
    /// The missions to ask the game's mission log about (completed, and completed with gold): the job's own.
    /// Null = not asked.
    /// </param>
    /// <param name="questIds">
    /// The quests to ask the game about (done or not): the ones the job's missions and actions want. Read in
    /// ANY zone - a quest is the character's, not the zone's. Null = not asked.
    /// </param>
    /// <param name="functionIds">
    /// The unlock functions to ask the game about (<c>WKSManager.IsFunctionUnlocked</c>): the ones the job's
    /// missions name. Only in a Cosmic zone. Null = not asked.
    /// </param>
    public MissionState Read(
        uint classJobId, IReadOnlyCollection<uint>? missionIds = null, IReadOnlyCollection<uint>? questIds = null,
        IReadOnlyCollection<uint>? functionIds = null)
    {
        HashSet<uint>? questsDone = null;
        try
        {
            // The quest log first: it does not need a Cosmic zone. (Quest ids are 65536 + the game's own number.)
            var quests = QuestManager.Instance();
            if (questIds != null && quests != null)
            {
                questsDone = new HashSet<uint>();
                var detail = new List<string>();
                foreach (var quest in questIds)
                {
                    var number = (ushort)(quest & 0xFFFF);
                    var complete = QuestManager.IsQuestComplete(number);
                    if (complete)
                        questsDone.Add(quest);

                    // Everything the game will say of the quest, side by side: on 2026-11-14 "complete" read true
                    // for a quest the player said was not done, and this is how the wrong one is to be found.
                    var ui = UIState.Instance();
                    detail.Add(
                        $"{quest}: complete {Flag(complete)}, accepted {Flag(quests->IsQuestAccepted(number))}, step {QuestManager.GetQuestSequence(number)}"
                        + (ui == null ? string.Empty : $", unlock link {Flag(ui->IsUnlockLinkUnlockedOrQuestCompleted(quest))}"));
                }

                QuestDetail = detail.Count == 0 ? "no quest asked about" : string.Join(" · ", detail);
            }

            var manager = WKSManager.Instance();
            if (manager == null || !manager->IsLoaded)
            {
                Status = "Idle — not in a Cosmic Exploration zone."
                         + (questsDone == null ? string.Empty : $" Quests asked about: {questIds!.Count}, done: {questsDone.Count}.");
                return MissionState.NotInZone(classJobId) with { QuestsDone = questsDone };
            }

            ref var state = ref manager->State;
            var current = state.CurrentMission;

            var offered = new List<uint>();
            var masterBest = new Dictionary<uint, uint>();
            var other = new List<uint>();
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
                    if (master.MissionUnitId == 0)
                        continue;

                    offered.Add(master.MissionUnitId);

                    // The one field a Master mission's state has over a ranked one's: four bytes at +12, private
                    // and unnamed in ClientStructs ("UnkC"). TAKEN TO BE THE HIGH SCORE the Tool Mastery tab
                    // shows - unseen in game when written (2026-11-14); the Status lists it, so a wrong guess shows.
                    fixed (WKSManager.WKSMasterMissionState* raw = &master)
                        masterBest[master.MissionUnitId] = *(uint*)((byte*)raw + MasterHighScoreOffset);
                }

                bonus = job.BonusMasterMissionUnitId;
                classScore = state.Scores[index];

                // The job's other lists, as ClientStructs names them: four ranks of three, one extra ranked
                // mission, and the sequence / provisional / red-alert lists. UNSEEN IN GAME when written
                // (2026-11-14): that they are what the board's other tabs show.
                foreach (ref var rank in job.MissionClasses)
                {
                    foreach (ref var mission in rank.Missions)
                        Add(other, mission.MissionUnitId);
                }

                Add(other, job.ExtraBasicMission.MissionUnitId);
                for (var i = 0; i < job.SequentialMissions.Count; i++)
                    Add(other, job.SequentialMissions[i].MissionUnitId);
                for (var i = 0; i < job.ProvisionalMissions.Count; i++)
                    Add(other, job.ProvisionalMissions[i].MissionUnitId);
                for (var i = 0; i < job.CriticalMissions.Count; i++)
                    Add(other, job.CriticalMissions[i].MissionUnitId);
            }

            var clock = current.MissionUnitRowId != 0 ? ReadClock(out var clockDetail) : NoClock(out clockDetail);

            // The game's own answer for each unlock function the job's missions name.
            Dictionary<uint, bool>? functions = null;
            if (functionIds != null)
            {
                functions = new Dictionary<uint, bool>();
                foreach (var function in functionIds)
                {
                    if (function is > 0 and <= byte.MaxValue)
                        functions[function] = manager->IsFunctionUnlocked((byte)function);
                }
            }

            // The game's mission log, mission by mission (ClientStructs' own helpers over the two flag arrays;
            // ICE's gold mode reads the same). UNSEEN IN GAME when written (2026-11-14).
            HashSet<uint>? completed = null, golded = null;
            if (missionIds != null)
            {
                completed = new HashSet<uint>();
                golded = new HashSet<uint>();
                foreach (var id in missionIds)
                {
                    if (manager->IsMissionGolded(id))
                        golded.Add(id);
                    if (manager->IsMissionCompleted(id))
                        completed.Add(id);
                }
            }

            Status = $"zone {manager->TerritoryId} · job {classJobId} = state block {(hasJob ? index.ToString() : "none")}"
                     + $" · Master offered: {(offered.Count == 0 ? "none" : string.Join(", ", offered.ConvertAll(id => $"{id} [+12 = {masterBest[id]}]")))} (bonus {bonus})"
                     + $" · other missions held: {(other.Count == 0 ? "none" : string.Join(", ", other))}"
                     + (golded == null ? string.Empty : $" · of the job's {missionIds!.Count} missions: {completed!.Count} completed, {golded.Count} gold")
                     + (questsDone == null ? string.Empty : $" · quests asked about: {questIds!.Count}, done: {questsDone.Count}")
                     + (functions == null || functions.Count == 0 ? string.Empty
                         : $" · unlock functions, by the game: {string.Join(", ", functions.Select(f => $"{f.Key} {(f.Value ? "open" : "LOCKED")}"))}")
                     + $" · current mission {current.MissionUnitRowId}, score {current.ScoreUInt}, rank {(int)current.Rank}"
                     + $" · class score {classScore} · clock: {clockDetail}";

            return new MissionState(
                true, manager->TerritoryId, classJobId, current.MissionUnitRowId, current.ScoreUInt,
                Enum.IsDefined(typeof(MissionRank), (int)current.Rank) ? (MissionRank)(int)current.Rank : MissionRank.None,
                offered, bonus, classScore, clock, other, completed, golded, questsDone, masterBest, state.ConsecutiveGoldCount,
                functions);
        }
        catch (Exception ex)
        {
            Status = $"The mission state could not be read — {ex.Message}";
            return MissionState.NotInZone(classJobId) with { QuestsDone = questsDone };
        }
    }

    /// <summary>
    /// How many of an item the character holds - for the job's tool mastery points, which the game keeps as
    /// an item count (ICE's reading; the item is <c>WKSScoreList[job - 8].Unknown5</c>). Asked only when a
    /// mission starts and ends, never on a timer. Null when it cannot be read.
    /// </summary>
    public int? ItemCount(uint itemId)
    {
        try
        {
            var bags = InventoryManager.Instance();
            return itemId == 0 || bags == null ? null : bags->GetInventoryItemCount(itemId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The job's cosmic tool: its stage and, a kind of data at a time, what it has, what the stage asks for and
    /// what the stage can bank - off <c>WKSManager->ResearchModule</c>, as ICE's relic mode reads it (the tool
    /// class is the job counted from one: Carpenter = 1). UNSEEN IN GAME when written (2026-11-14): the user's
    /// own table showed "20.7" for every tool of one character - stage 20, seven kinds - to compare with.
    /// Null when it cannot be read (not in a Cosmic zone, not a Cosmic job).
    /// </summary>
    public ToolState? ReadTool(uint classJobId, uint territoryId)
    {
        try
        {
            var manager = WKSManager.Instance();
            var index = (int)classJobId - (int)FirstCosmicJob;
            if (manager == null || !manager->IsLoaded || index < 0 || index >= 11)
                return null;

            var research = manager->ResearchModule;
            if (research == null || !research->IsLoaded)
                return null;

            var toolClass = (byte)(index + 1);
            var kinds = new List<ToolKind>();
            for (byte kind = 1; kind <= 7 && research->IsTypeAvailable(toolClass, kind); kind++)
            {
                kinds.Add(new ToolKind(
                    kind, research->GetCurrentAnalysis(toolClass, kind), research->GetNeededAnalysis(toolClass, kind),
                    research->GetMaxAnalysis(toolClass, kind)));
            }

            var cap = ToolState.StarCaps[^1];
            for (var star = 0; star < MissionStars.Stars.Count && star < ToolState.StarCaps.Count; star++)
            {
                if (MissionStars.Stars[star].TerritoryId == territoryId)
                    cap = ToolState.StarCaps[star];
            }

            return new ToolState(research->CurrentStages[index], cap, kinds);
        }
        catch
        {
            return null;
        }
    }

    private static void Add(List<uint> ids, uint id)
    {
        if (id != 0 && !ids.Contains(id))
            ids.Add(id);
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
