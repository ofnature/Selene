using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Selene.Features.Missions;
using ClassJob = Lumina.Excel.Sheets.ClassJob;
using ClassJobCategory = Lumina.Excel.Sheets.ClassJobCategory;
using CraftAction = Lumina.Excel.Sheets.CraftAction;
using GameAction = Lumina.Excel.Sheets.Action;
using Quest = Lumina.Excel.Sheets.Quest;
using Recipe = Lumina.Excel.Sheets.Recipe;
using TerritoryType = Lumina.Excel.Sheets.TerritoryType;
using WKSMissionUnit = Lumina.Excel.Sheets.WKSMissionUnit;
using WKSScoreList = Lumina.Excel.Sheets.WKSScoreList;

namespace Selene.Services.Game;

/// <summary>
/// The missions as the game's sheets describe them, read once. Fail-open: an empty catalog, and a Status
/// saying why. (The sheets are aliased one by one: <c>using Lumina.Excel.Sheets;</c> collides with
/// <c>System.Action</c>.)
/// </summary>
public sealed class MissionCatalog
{
    private readonly Dictionary<uint, MissionInfo> _missions = new();
    private readonly Dictionary<uint, string> _jobNames = new();
    private readonly Dictionary<uint, string> _starNames = new();
    private readonly Dictionary<uint, IReadOnlyList<ActionNeed>> _jobUnlocks = new();
    private readonly IDataManager _data;

    public MissionCatalog(IDataManager data)
    {
        _data = data;
        try
        {
            var recipeSheet = data.GetExcelSheet<Recipe>();
            var questSheet = data.GetExcelSheet<Quest>();
            foreach (var row in data.GetExcelSheet<WKSMissionUnit>())
            {
                var name = row.Name.ExtractText();
                if (row.RowId == 0 || string.IsNullOrWhiteSpace(name))
                    continue;

                // ClassJobCategory rows 9..19 are the single-job categories of jobs 8..18: job = category - 1.
                // Seen in game 2026-11-12: category 9's Master missions are the ones offered to Carpenter (8).
                var category = row.ClassJobCategory[0].RowId;
                var recipes = row.WKSMissionRecipe.ValueNullable is { } missionRecipe
                    ? missionRecipe.Recipe.Select(recipe => recipe.RowId).Where(id => id != 0).ToList()
                    : new List<uint>();

                // What sets a mission apart from the plain ranked ones. Read from the game's data 2026-11-14: a
                // condition row holds EITHER a weather OR hours, and none of the three kinds overlap.
                MissionCategory? special = null;
                if (row.IsSpecialQuest)
                    special = MissionCategory.RedAlert;
                else if (row.LockedBehind.RowId != 0)
                    special = MissionCategory.Sequence;
                else if (row.WKSMissionLotterySpecialCond.RowId != 0)
                    special = row.WKSMissionLotterySpecialCond.ValueNullable is { } need && need.WeatherRequired.RowId != 0
                        ? MissionCategory.Weather
                        : MissionCategory.Timed;

                // When a weather or timed mission is offered: the weather's name, or the Eorzean hours.
                MissionWindow? window = null;
                if (special is MissionCategory.Weather or MissionCategory.Timed && row.WKSMissionLotterySpecialCond.ValueNullable is { } offeredWhen)
                {
                    window = new MissionWindow(
                        offeredWhen.StartTimeHour, offeredWhen.EndTimeHour,
                        offeredWhen.WeatherRequired.RowId != 0 ? offeredWhen.WeatherRequired.ValueNullable?.Name.ExtractText() ?? "a weather" : string.Empty);
                }

                // More than one recipe: what to make, how many and in which order (CraftStep.Plan has the rule).
                IReadOnlyList<CraftStep>? crafts = null;
                if (recipes.Count > 1)
                {
                    var wanted = new Dictionary<uint, int>();
                    foreach (var toDoRef in row.MissionToDo)
                    {
                        if (toDoRef.RowId == 0 || toDoRef.ValueNullable is not { } toDo)
                            continue;

                        for (var i = 0; i < toDo.RequiredItem.Count; i++)
                        {
                            var item = toDo.RequiredItem[i].ValueNullable?.Item.RowId ?? 0;
                            int quantity = toDo.RequiredItemQuantity[i];
                            if (item != 0 && quantity > 0)
                                wanted[item] = wanted.GetValueOrDefault(item) + quantity;
                        }
                    }

                    var made = new List<(uint RecipeId, uint Makes, int Yield, IReadOnlyDictionary<uint, int> Uses)>();
                    foreach (var id in recipes)
                    {
                        if (!recipeSheet.TryGetRow(id, out var recipe))
                            continue;

                        var uses = new Dictionary<uint, int>();
                        for (var i = 0; i < recipe.Ingredient.Count; i++)
                        {
                            int amount = recipe.AmountIngredient[i];
                            if (recipe.Ingredient[i].RowId != 0 && amount > 0)
                                uses[recipe.Ingredient[i].RowId] = amount;
                        }

                        made.Add((id, recipe.ItemResult.RowId, recipe.AmountResult, uses));
                    }

                    crafts = CraftStep.Plan(made, wanted);
                }

                // What it pays: credits, tokens, and the data that levels the job's cosmic tool (a kind and an amount, up to three).
                MissionReward? reward = null;
                if (row.MissionReward.ValueNullable is { } pays)
                {
                    var toolData = new List<(int Kind, int Amount)>();
                    for (var i = 0; i < pays.TypeIndex.Count && i < pays.ResearchReward.Count; i++)
                    {
                        int kind = pays.TypeIndex[i];
                        int amount = pays.ResearchReward[i];
                        if (kind > 0 && amount > 0)
                            toolData.Add((kind, amount));
                    }

                    reward = new MissionReward(pays.CosmoCredits, pays.PlanetCredits, pays.Item.RowId != 0 ? pays.ItemCount : 0, toolData);
                }

                // The quests it asks for: the mission's WKSFunction row lists them, a column a star.
                IReadOnlyList<QuestNeed>? needs = null;
                var star = MissionStars.IndexOf(row.RowId);
                if (star >= 0 && row.WKSFunction.RowId != 0 && row.WKSFunction.ValueNullable is { } function)
                {
                    var asked = new List<QuestNeed>();
                    foreach (var quests in new[] { function.RequiredQuests0, function.RequiredQuests1, function.RequiredQuests2 })
                    {
                        var quest = star < quests.Count ? quests[star].RowId : 0;
                        if (quest != 0 && !asked.Exists(need => need.QuestId == quest))
                            asked.Add(new QuestNeed(quest, QuestName(questSheet, quest)));
                    }

                    needs = asked;
                }

                _missions[row.RowId] = new MissionInfo(
                    row.RowId, name, category == 0 ? 0 : category - 1, row.LevelGroup, row.Unknown2, row.MissionTime,
                    row.SilverStarRequirement, row.GoldStarRequirement, recipes, special, crafts, reward, needs, row.WKSFunction.RowId, window);
            }

            foreach (var job in data.GetExcelSheet<ClassJob>())
                _jobNames[job.RowId] = job.Abbreviation.ExtractText();

            // The stars' names, as the game names their zones.
            var territories = data.GetExcelSheet<TerritoryType>();
            foreach (var (territoryId, _) in MissionStars.Stars)
            {
                if (territories.TryGetRow(territoryId, out var territory) && territory.PlaceName.ValueNullable is { } place)
                    _starNames[territoryId] = place.Name.ExtractText();
            }

            var masters = _missions.Values.Count(mission => mission.IsMaster);
            Status = $"{_missions.Count} missions read, {masters} of them Master";
        }
        catch (Exception ex)
        {
            _missions.Clear();
            Status = $"The mission sheets could not be read — {ex.Message}";
        }
    }

    public IReadOnlyDictionary<uint, MissionInfo> Missions => _missions;

    /// <summary>What was read, or why nothing was.</summary>
    public string Status { get; }

    /// <summary>
    /// The item whose count is the job's tool mastery points (<c>WKSScoreList</c>, a row a Cosmic job from
    /// Carpenter: its <c>Unknown5</c> - items 51315 to 51325 in the game's data); 0 when there is none.
    /// </summary>
    public uint MasteryItemId(uint classJobId)
    {
        try
        {
            return classJobId is >= MissionInfo.FirstCrafter and <= 18
                   && _data.GetExcelSheet<WKSScoreList>().TryGetRow(classJobId - MissionInfo.FirstCrafter, out var row)
                ? row.Unknown5
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string QuestName(Lumina.Excel.ExcelSheet<Quest> quests, uint questId)
        => quests.TryGetRow(questId, out var quest) && quest.Name.ExtractText() is { Length: > 0 } name ? name : $"quest {questId}";

    /// <summary>
    /// The job's actions that a quest unlocks - the ones a character can be at level for and still not have
    /// ("certain actions unlocked from class quests"): from the Action sheet (gathering, fishing, and
    /// Manipulation for the crafters) and the CraftAction sheet (the specialist ones). Read once a job.
    /// Fail-open: an empty list.
    /// </summary>
    public IReadOnlyList<ActionNeed> JobUnlocks(uint classJobId)
    {
        if (_jobUnlocks.TryGetValue(classJobId, out var known))
            return known;

        var unlocks = new List<ActionNeed>();
        try
        {
            var quests = _data.GetExcelSheet<Quest>();

            // An action belongs to the job when its job category has the job's column set ("CRP", "FSH"...).
            var column = _jobNames.TryGetValue(classJobId, out var abbreviation) && abbreviation.Length > 0
                ? typeof(ClassJobCategory).GetProperty(abbreviation)
                : null;
            if (column != null && column.PropertyType == typeof(bool))
            {
                foreach (var action in _data.GetExcelSheet<GameAction>())
                {
                    if (!action.IsPlayerAction || action.UnlockLink.RowId <= 65535 || action.ClassJobCategory.ValueNullable is not { } jobs)
                        continue;

                    var name = action.Name.ExtractText();
                    if (name.Length == 0 || !(bool)column.GetValue(jobs)! || unlocks.Exists(unlock => unlock.Action == name))
                        continue;

                    unlocks.Add(new ActionNeed(name, action.ClassJobLevel, action.UnlockLink.RowId, QuestName(quests, action.UnlockLink.RowId), action.RowId));
                }
            }

            foreach (var action in _data.GetExcelSheet<CraftAction>())
            {
                if (action.ClassJob.RowId != classJobId || action.QuestRequirement.RowId == 0)
                    continue;

                var name = action.Name.ExtractText();
                if (name.Length == 0 || unlocks.Exists(unlock => unlock.Action == name))
                    continue;

                unlocks.Add(new ActionNeed(name, action.ClassJobLevel, action.QuestRequirement.RowId, QuestName(quests, action.QuestRequirement.RowId), action.RowId, Craft: true));
            }

            unlocks.Sort((a, b) => a.Level != b.Level ? a.Level.CompareTo(b.Level) : string.CompareOrdinal(a.Action, b.Action));
        }
        catch
        {
            unlocks.Clear();
        }

        _jobUnlocks[classJobId] = unlocks;
        return unlocks;
    }

    /// <summary>A star's name by its territory; "Other" for the missions no star is known for.</summary>
    public string StarName(uint territoryId)
        => territoryId == 0 ? "Other"
            : _starNames.TryGetValue(territoryId, out var name) && name.Length > 0 ? name : $"zone {territoryId}";

    public string JobName(uint classJobId)
        => _jobNames.TryGetValue(classJobId, out var name) && name.Length > 0 ? name : $"job {classJobId}";
}
