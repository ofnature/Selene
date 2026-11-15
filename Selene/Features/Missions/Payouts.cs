using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Selene.Features.Missions;

/// <summary>One completed mission and what it paid, as measured off the game.</summary>
/// <param name="Rank">The rank last seen before the mission ended (the game's own number: 1 bronze, 2 silver, 3 gold).</param>
/// <param name="Score">The score last seen before it ended.</param>
/// <param name="ClassPoints">How much the job's Cosmic class score rose when it ended.</param>
/// <param name="MasteryPoints">How much the job's tool mastery points rose; null = not read.</param>
/// <param name="Seconds">From the mission being seen to start until it ended; 0 = it was already going when the watch began.</param>
/// <param name="GoldStreak">The game's gold streak when it ended.</param>
public sealed record PayoutSample(
    DateTime At, uint MissionId, uint ClassJobId, int Rank, uint Score, int ClassPoints, int? MasteryPoints, int Seconds, int GoldStreak);

/// <summary>What is known of one mission from the samples.</summary>
/// <param name="ByRank">A rank's class points: the latest measured, the lowest, the highest, and how many times.</param>
/// <param name="AverageSeconds">Over the samples that were timed; 0 = none was.</param>
public sealed record PayoutSummary(
    uint MissionId, int Samples, IReadOnlyDictionary<int, (int Last, int Min, int Max, int Count)> ByRank, int AverageSeconds)
{
    /// <summary>The highest rank measured, and its latest class points.</summary>
    public (int Rank, int Points) Best
    {
        get
        {
            var rank = ByRank.Keys.Max();
            return (rank, ByRank[rank].Last);
        }
    }

    /// <summary>Class points a minute at the best rank measured; null when no sample was timed.</summary>
    public double? PerMinute => AverageSeconds > 0 ? Best.Points * 60.0 / AverageSeconds : null;

    public static IReadOnlyDictionary<uint, PayoutSummary> Summarise(IEnumerable<PayoutSample> samples)
    {
        var summaries = new Dictionary<uint, PayoutSummary>();
        foreach (var mission in samples.GroupBy(sample => sample.MissionId))
        {
            var byRank = new Dictionary<int, (int Last, int Min, int Max, int Count)>();
            foreach (var rank in mission.GroupBy(sample => sample.Rank))
            {
                var ordered = rank.OrderBy(sample => sample.At).ToList();
                byRank[rank.Key] = (ordered[^1].ClassPoints, ordered.Min(sample => sample.ClassPoints), ordered.Max(sample => sample.ClassPoints), ordered.Count);
            }

            var timed = mission.Where(sample => sample.Seconds > 0).ToList();
            summaries[mission.Key] = new PayoutSummary(
                mission.Key, mission.Count(), byRank, timed.Count == 0 ? 0 : (int)timed.Average(sample => sample.Seconds));
        }

        return summaries;
    }
}

/// <summary>
/// Watches a mission from the moment it is seen in progress to the moment it is gone, and says what it paid
/// in class points - WHOEVER RAN IT: Selene, another plugin, or the player by hand. Class points a mission are
/// in none of the game's sheets (the user's two chat logs, 2026-11-14: 120 for an A-rank gold, 75 for a B-rank
/// one - no column gives either), so they are measured: the job's class score before and after.
/// A mission that ends with no rise in class score (abandoned, failed) is not a payout and is not recorded.
/// Pure: the game's state in, a sample out when there is one.
/// </summary>
public sealed class PayoutWatch
{
    /// <summary>How long after a mission is gone the class score is given to show the rise.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(6);

    private uint _missionId;
    private uint _job;
    private DateTime _startedAt;
    private bool _sawStart;
    private bool _idleBefore;
    private uint _score;
    private int _rank;
    private int _classBefore;
    private int? _masteryBefore;
    private DateTime? _endedAt;

    public string Status { get; private set; } = "Idle — no mission watched yet.";

    /// <param name="mastery">
    /// The job's tool mastery points right now. Called ONLY when a mission is first seen and when it has
    /// ended - two snapshots, never a poll of the bags.
    /// </param>
    public PayoutSample? Observe(MissionState state, DateTime now, Func<int?> mastery)
    {
        if (!state.InZone || (_missionId != 0 && state.ClassJobId != _job))
        {
            if (_missionId != 0)
                Status = $"Mission {_missionId} was dropped from the watch — the zone or the job changed.";
            _missionId = 0;
            _idleBefore = false;
            return null;
        }

        if (state.InMission)
        {
            if (_missionId != state.CurrentMissionId || _endedAt != null)
            {
                _missionId = state.CurrentMissionId;
                _job = state.ClassJobId;
                _startedAt = now;
                _sawStart = _idleBefore; // seen with no mission just before: the start is known
                _classBefore = state.ClassScore;
                _masteryBefore = mastery();
                _endedAt = null;
                _score = 0;
                _rank = 0;
            }

            if (state.Score > 0)
                _score = state.Score;
            if (state.Rank is not (MissionRank.None or MissionRank.Failed))
                _rank = (int)state.Rank;
            Status = $"Watching mission {_missionId}: class score {_classBefore.ToString("N0", CultureInfo.InvariantCulture)} before it.";
            _idleBefore = false;
            return null;
        }

        _idleBefore = true;
        if (_missionId == 0)
            return null;

        _endedAt ??= now;
        var rise = state.ClassScore - _classBefore;
        if (rise <= 0)
        {
            if (now - _endedAt.Value > Settle)
            {
                Status = $"Mission {_missionId} ended with no class points (abandoned or failed) — not recorded.";
                _missionId = 0;
            }

            return null;
        }

        var after = mastery();
        var sample = new PayoutSample(
            now, _missionId, _job, _rank, _score, rise,
            after != null && _masteryBefore != null ? after - _masteryBefore : null,
            _sawStart ? Math.Max(1, (int)(_endedAt.Value - _startedAt).TotalSeconds) : 0,
            state.GoldStreak);
        Status = $"Mission {_missionId} paid {rise.ToString("N0", CultureInfo.InvariantCulture)} class points (rank {_rank}, score {_score.ToString("N0", CultureInfo.InvariantCulture)}).";
        _missionId = 0;
        return sample;
    }
}

/// <summary>
/// The payouts measured on this PC: one <c>payouts/{contentId}.json</c> a character under the plugin's config
/// directory (the user, 2026-11-14: "will we track in a sheet or something?"). A FILE A CHARACTER, like the
/// roster: each client writes only its own and reads all, so two clients cannot lose each other's. What a
/// mission pays is a fact about the game, not the character, so every character's samples fill one table.
/// </summary>
public sealed class PayoutLog
{
    private readonly string _directory;
    private readonly Dictionary<ulong, List<PayoutSample>> _byCharacter = new();
    private IReadOnlyDictionary<uint, PayoutSummary> _summaries = new Dictionary<uint, PayoutSummary>();

    public PayoutLog(string configDirectory)
    {
        _directory = Path.Combine(configDirectory, "payouts");
    }

    public IReadOnlyDictionary<uint, PayoutSummary> Summaries => _summaries;

    public string Status { get; private set; } = "Idle — nothing read yet.";

    public string PathFor(ulong contentId) => Path.Combine(_directory, $"{contentId:X16}.json");

    /// <summary>Read every character's file again: this is how another client's samples turn up.</summary>
    public void Reload()
    {
        var unreadable = 0;
        if (Directory.Exists(_directory))
        {
            foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
            {
                if (!ulong.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var contentId))
                    continue;

                try
                {
                    _byCharacter[contentId] = JsonSerializer.Deserialize<List<PayoutSample>>(File.ReadAllText(path)) ?? new List<PayoutSample>();
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    unreadable++; // junk, or another client's file in mid-write: it is read at the next reload
                }
            }
        }

        Resummarise(unreadable);
    }

    /// <summary>Keep a sample for the logged-in character. Written at once: a sample is one finished mission.</summary>
    public void Add(ulong contentId, PayoutSample sample)
    {
        if (contentId == 0)
            return;

        if (!_byCharacter.TryGetValue(contentId, out var own))
            _byCharacter[contentId] = own = new List<PayoutSample>();
        own.Add(sample);

        try
        {
            // Write-then-rename, so a reader never meets a half-written file.
            Directory.CreateDirectory(_directory);
            var path = PathFor(contentId);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(own));
            File.Move(temp, path, overwrite: true);
            Resummarise(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Resummarise(0);
            Status = $"A sample could not be saved — {ex.Message}";
        }
    }

    /// <summary>Every sample as a sheet (CSV), newest last. Returns the path, or null with the reason in <see cref="Status"/>.</summary>
    public string? ExportCsv(Func<uint, string> missionName)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "payouts.csv");
            var text = new StringBuilder("When,Character,MissionId,Mission,Job,Rank,Score,ClassPoints,MasteryPoints,Seconds,GoldStreak\n");
            foreach (var (contentId, samples) in _byCharacter)
            {
                foreach (var s in samples.OrderBy(sample => sample.At))
                {
                    text.Append(CultureInfo.InvariantCulture,
                        $"{s.At:yyyy-MM-dd HH:mm:ss},{contentId:X16},{s.MissionId},\"{missionName(s.MissionId).Replace("\"", "\"\"")}\",{s.ClassJobId},{s.Rank},{s.Score},{s.ClassPoints},{s.MasteryPoints},{s.Seconds},{s.GoldStreak}\n");
                }
            }

            File.WriteAllText(path, text.ToString());
            Status = $"Exported to {path}";
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"The export failed — {ex.Message}";
            return null;
        }
    }

    private void Resummarise(int unreadable)
    {
        var all = _byCharacter.Values.SelectMany(samples => samples).ToList();
        _summaries = PayoutSummary.Summarise(all);
        Status = all.Count == 0 && unreadable == 0
            ? "Idle — no mission has been measured yet."
            : $"{all.Count} sample(s) of {_summaries.Count} mission(s) from {_byCharacter.Count} character(s)" + (unreadable > 0 ? $", {unreadable} file(s) unreadable" : string.Empty);
    }
}
