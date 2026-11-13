using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Selene.Features.Missions;
using ClassJob = Lumina.Excel.Sheets.ClassJob;
using WKSMissionUnit = Lumina.Excel.Sheets.WKSMissionUnit;

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

    public MissionCatalog(IDataManager data)
    {
        try
        {
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

                _missions[row.RowId] = new MissionInfo(
                    row.RowId, name, category == 0 ? 0 : category - 1, row.LevelGroup, row.Unknown2, row.MissionTime,
                    row.SilverStarRequirement, row.GoldStarRequirement, recipes);
            }

            foreach (var job in data.GetExcelSheet<ClassJob>())
                _jobNames[job.RowId] = job.Abbreviation.ExtractText();

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

    public string JobName(uint classJobId)
        => _jobNames.TryGetValue(classJobId, out var name) && name.Length > 0 ? name : $"job {classJobId}";
}
