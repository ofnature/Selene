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
/// <param name="Special">
/// What sets the mission apart from the plain ranked ones, by the sheets: a red alert (<c>IsSpecialQuest</c>), a
/// link in a chain (<c>LockedBehind</c>), or one that needs a weather or an hour
/// (<c>WKSMissionLotterySpecialCond</c>). Null for a plain ranked mission and for a Master one.
/// </param>
/// <param name="Crafts">
/// What to make, in order, for a mission of MORE THAN ONE recipe: the parts first, then what the mission
/// wants, each with how many. Null for a one-recipe mission (its recipe is crafted where it is selected in
/// the Cosmic log). Read from the game's data 2026-11-14: in all 312 multi-recipe crafter missions each
/// recipe's result is either wanted by the mission or an ingredient of another of its recipes.
/// </param>
/// <param name="Reward">What the sheets say the mission pays (<c>WKSMissionReward</c>); null when there is no row.</param>
/// <param name="Needs">
/// The quests the mission asks for, by the game's own data: the mission names a <c>WKSFunction</c> row, and
/// that row lists the quests for the mission's star. Read from disk 2026-11-14: 669 missions want "Inscrutable
/// Tastes" (collectables), 34 gatherers' ones "No Longer a Collectable" too (reduction), 17 of Fisher's a
/// Fisher class quest. Null or empty = none.
/// </param>
public sealed record MissionInfo(
    uint Id, string Name, uint ClassJobId, int LevelGroup, bool IsMaster, int TimeLimitSeconds, uint SilverScore, uint GoldScore,
    IReadOnlyList<uint> RecipeIds, MissionCategory? Special = null, IReadOnlyList<CraftStep>? Crafts = null,
    MissionReward? Reward = null, IReadOnlyList<QuestNeed>? Needs = null)
{
    /// <summary>
    /// The tab the mission is listed under (the user, 2026-11-14: "wire in the other missions, like ice does
    /// and have tabs to seperate them" - ICE's own row: Red Alert, Sequence, Weather, Timed, Master, A to D).
    /// Checked against the game's data that day: the four special kinds and Master never overlap, and a plain
    /// mission is tier 1-5 (D, C, B, A, EX) - EX+ exists only as special or Master. EX is listed with A.
    /// </summary>
    public MissionCategory Category => IsMaster
        ? MissionCategory.Master
        : Special ?? LevelGroup switch
        {
            >= 4 => MissionCategory.RankA,
            3 => MissionCategory.RankB,
            2 => MissionCategory.RankC,
            _ => MissionCategory.RankD,
        };

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

/// <summary>
/// What a mission pays, as the sheet <c>WKSMissionReward</c> has it (the user, 2026-11-14: "need to see what the
/// payout for each mission is so when leveling a weapon we can max a weapon"). THE SHEET'S BASE FIGURES: what a
/// rank adds to them is not in the row.
/// </summary>
/// <param name="ToolData">
/// The data that levels the job's cosmic tool: up to three kinds (1-7, the game's I to VII) with an amount each.
/// A tool has 20 stages, each asking a total of every kind it uses (<c>WKSCosmoToolDataAmount</c>).
/// </param>
/// <param name="Tokens">The star's exploration tokens (EX+ and Master missions).</param>
public sealed record MissionReward(
    int CosmoCredits, int PlanetCredits, int Tokens, IReadOnlyList<(int Kind, int Amount)> ToolData)
{
    private static readonly string[] Roman = { "?", "I", "II", "III", "IV", "V", "VI", "VII" };

    public static string KindName(int kind) => kind >= 1 && kind < Roman.Length ? Roman[kind] : kind.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"I 8 · II 6 · III 6", lowest kind first; empty when the mission pays no tool data.</summary>
    public string ToolDataText
    {
        get
        {
            var parts = new List<string>();
            foreach (var (kind, amount) in System.Linq.Enumerable.OrderBy(ToolData, data => data.Kind))
                parts.Add($"{KindName(kind)} {amount}");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>A quest something asks for (the user, 2026-11-14: "some mission require pre quests like unlocking collection").</summary>
public sealed record QuestNeed(uint QuestId, string QuestName);

/// <summary>An action of a job that a quest unlocks ("certain actions unlocked from class quests").</summary>
/// <param name="Level">The job level the action is learned at.</param>
public sealed record ActionNeed(string Action, int Level, uint QuestId, string QuestName);

/// <summary>One recipe of a mission and how many times to make it.</summary>
public sealed record CraftStep(uint RecipeId, int Count)
{
    /// <summary>
    /// The order and the counts for a mission's recipes: what the mission wants is made as often as it asks,
    /// and a part as often as the recipes using it need - parts first. Pure, so the rule is tested without
    /// the game's sheets.
    /// </summary>
    /// <param name="recipes">Each recipe: its id, the item it makes and how many a craft, and what it uses.</param>
    /// <param name="wanted">The items the mission asks for, and how many of each.</param>
    public static IReadOnlyList<CraftStep> Plan(
        IReadOnlyList<(uint RecipeId, uint Makes, int Yield, IReadOnlyDictionary<uint, int> Uses)> recipes,
        IReadOnlyDictionary<uint, int> wanted)
    {
        var counts = new Dictionary<uint, int>();
        var order = new List<uint>();

        // A recipe's count is known once every recipe using its result has one: wanted items first, then
        // their parts, then the parts' parts. Bounded by the number of recipes - a loop in the data ends it.
        for (var pass = 0; pass <= recipes.Count && counts.Count < recipes.Count; pass++)
        {
            foreach (var recipe in recipes)
            {
                if (counts.ContainsKey(recipe.RecipeId))
                    continue;

                var users = new List<(uint RecipeId, int Each)>();
                foreach (var other in recipes)
                {
                    if (other.RecipeId != recipe.RecipeId && other.Uses.TryGetValue(recipe.Makes, out var each))
                        users.Add((other.RecipeId, each));
                }

                if (users.Exists(user => !counts.ContainsKey(user.RecipeId)))
                    continue;

                var needed = wanted.TryGetValue(recipe.Makes, out var asked) ? asked : 0;
                foreach (var user in users)
                    needed += counts[user.RecipeId] * user.Each;

                var yield = Math.Max(1, recipe.Yield);
                counts[recipe.RecipeId] = (needed + yield - 1) / yield;
                order.Add(recipe.RecipeId);
            }
        }

        // Resolved from the wanted items down, so made in the opposite order: parts first.
        order.Reverse();
        var steps = new List<CraftStep>();
        foreach (var id in order)
        {
            if (counts[id] > 0)
                steps.Add(new CraftStep(id, counts[id]));
        }

        return steps;
    }
}

/// <summary>How a run treats one mission. The default is a Master mission's: timed, played to the clock, one recipe.</summary>
/// <param name="Timed">The mission has a clock; false = it ends when the work is done.</param>
/// <param name="ReportAt">Report as soon as this rank shows (the user's setting for ranked missions); null = play to the clock.</param>
/// <param name="Steps">The crafts of a multi-recipe mission, in order; null = the one recipe selected in the Cosmic log.</param>
public sealed record MissionPlan(bool Timed = true, MissionRank? ReportAt = null, IReadOnlyList<CraftStep>? Steps = null)
{
    public static readonly MissionPlan Master = new();
}

/// <summary>The tabs of the Missions page, in the order they are drawn.</summary>
public enum MissionCategory
{
    Master,
    RankA,
    RankB,
    RankC,
    RankD,
    Sequence,
    Weather,
    Timed,
    RedAlert,
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
/// <param name="Completed">Of the missions asked about, the ones the game's log has as completed at least once. Null = not read.</param>
/// <param name="Golded">...and the ones completed with a gold star (the game's own flags, which ICE's gold mode reads). Null = not read.</param>
/// <param name="MasterBest">
/// The number the game keeps beside each offered Master mission - TAKEN TO BE its high score (the user,
/// 2026-11-14: "the best on the master missions need point totals the highest earned"). It is the one field a
/// Master mission's state has over a ranked one's, unnamed in ClientStructs. UNSEEN IN GAME when added.
/// Null = not read.
/// </param>
/// <param name="GoldStreak">The game's count of gold stars in a row ("Your gold count streak has reached 2! Reward bonuses have increased by 5%").</param>
/// <param name="QuestsDone">Of the quests asked about, the ones the character has completed (read in any zone). Null = not read.</param>
/// <param name="OfferedOther">
/// Every OTHER mission the game holds for this job right now - its ranked, sequence, provisional and red-alert
/// lists (zeros left out). Null = not read. UNSEEN IN GAME when added (2026-11-14): that these lists are what
/// the board's other tabs show.
/// </param>
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
    int? ClockSeconds = null,
    IReadOnlyList<uint>? OfferedOther = null,
    IReadOnlyCollection<uint>? Completed = null,
    IReadOnlyCollection<uint>? Golded = null,
    IReadOnlyCollection<uint>? QuestsDone = null,
    IReadOnlyDictionary<uint, uint>? MasterBest = null,
    int GoldStreak = 0)
{
    /// <summary>Everything the game offers the job: the Master missions and the rest.</summary>
    public IEnumerable<uint> Offered => OfferedOther == null ? OfferedMaster : System.Linq.Enumerable.Concat(OfferedMaster, OfferedOther);

    public static MissionState NotInZone(uint classJobId)
        => new(false, 0, classJobId, 0, 0, MissionRank.None, Array.Empty<uint>(), 0, 0);

    public bool InMission => CurrentMissionId != 0;
}
