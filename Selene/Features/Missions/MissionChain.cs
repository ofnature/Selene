using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Selene.Features.Schedule;

namespace Selene.Features.Missions;

/// <summary>How the next mission is chosen when missions are run one after another.</summary>
public enum ChainMode
{
    /// <summary>The missions ticked one by one, and whatever the game offers on a tab ticked as a whole.</summary>
    Ticked,

    /// <summary>Whatever is not gold yet (ICE's "Gold Completion Grind"), reported at gold.</summary>
    GoldAll,

    /// <summary>Whatever pays most of the data the job's cosmic tool still lacks (ICE's "Relic Grind").</summary>
    LevelTool,

    /// <summary>Master missions, reported early: the most class points a minute as measured.</summary>
    ClassPoints,
}

/// <summary>One kind of a cosmic tool's data: what the tool has, what its stage asks for, and what the stage can bank.</summary>
public sealed record ToolKind(int Kind, int Current, int Needed, int Max);

/// <summary>
/// The job's cosmic tool, as the game has it (<c>WKSManager->ResearchModule</c>).
/// </summary>
/// <param name="Stage">The stage it is at (1-20).</param>
/// <param name="StarCap">The highest stage the star the character is on allows (9, 14, 17, 20).</param>
/// <param name="Kinds">The kinds of data the stage uses.</param>
public sealed record ToolState(int Stage, int StarCap, IReadOnlyList<ToolKind> Kinds)
{
    /// <summary>The top stage each star allows, in the order the stars were reached (ICE's figures; the tool sheet marks the same four stages).</summary>
    public static readonly IReadOnlyList<int> StarCaps = new[] { 9, 14, 17, 20 };

    /// <summary>Every kind the stage asks for is there, and the star allows the next stage: it can be handed in.</summary>
    public bool ReadyToHandIn => Stage < StarCap && Kinds.Count > 0 && Kinds.All(kind => kind.Current >= kind.Needed);

    /// <summary>Nothing more can be banked at this stage.</summary>
    public bool Full => Kinds.Count > 0 && Kinds.All(kind => kind.Current >= kind.Max);

    /// <summary>
    /// What a mission's tool data is worth to THIS tool: each kind's amount times how far the tool is short of
    /// it - short of what the stage asks for first; once all of that is there, short of what can be banked.
    /// (ICE's rule for its relic mode.) Data of a kind that is full is worth nothing.
    /// </summary>
    public double Worth(MissionReward reward)
    {
        var towardsNeed = Kinds.Any(kind => kind.Current < kind.Needed);
        var worth = 0.0;
        foreach (var (kind, amount) in reward.ToolData)
        {
            var have = Kinds.FirstOrDefault(k => k.Kind == kind);
            if (have == null)
                continue;

            var goal = towardsNeed ? have.Needed : have.Max;
            if (goal > 0 && have.Current < goal)
                worth += amount * (1.0 - ((double)have.Current / goal));
        }

        return worth;
    }

    public string Text => $"stage {Stage} of {StarCap} here · " + string.Join(" · ", Kinds.Select(kind =>
        $"{MissionReward.KindName(kind.Kind)} {kind.Current.ToString("N0", CultureInfo.InvariantCulture)}/{kind.Needed.ToString("N0", CultureInfo.InvariantCulture)}"));
}

/// <summary>The mission to run next, or why there is none.</summary>
/// <param name="MissionId">0 = none.</param>
/// <param name="Finished">With no mission: the mode's goal is reached (as opposed to nothing wanted being on offer).</param>
public sealed record Choice(uint MissionId, string Why, bool Finished = false);

/// <summary>
/// Picks the next mission for a mode, among what the game is OFFERING and Selene can run. It never re-rolls
/// (the user's rule): when nothing wanted is on offer it says so, and the chain stops. Pure.
/// </summary>
public static class MissionChooser
{
    /// <param name="rows">The job's missions on the star the character is on, as the Missions page has them.</param>
    /// <param name="runnable">What Selene can run at all: a crafting mission with a recipe, not a red alert, not locked.</param>
    public static Choice Choose(
        ChainMode mode, IReadOnlyList<MissionRow> rows, Func<MissionInfo, bool> runnable,
        IReadOnlyCollection<MissionCategory> wholeTabs, ToolState? tool, ulong seed, int salt)
    {
        var offered = rows.Where(row => row.Offered && runnable(row.Info)).ToList();
        switch (mode)
        {
            case ChainMode.GoldAll:
            {
                // A Master mission has no star rating: it is never "gold" and is left out.
                var left = rows.Where(row => !row.Info.IsMaster && runnable(row.Info) && row.Best != MissionBest.Gold).ToList();
                if (rows.Any(row => row.Best == null))
                    return new Choice(0, "the game's mission log cannot be read here");
                if (left.Count == 0)
                    return new Choice(0, "every mission of this class on this star that Selene can run is gold", Finished: true);

                // The lower ranks first, as ICE walks them: they turn the board over fastest.
                var next = offered.Where(row => left.Contains(row)).OrderBy(row => TabOrder(row.Info.Category)).ThenBy(row => row.Info.Id).FirstOrDefault();
                return next == null
                    ? new Choice(0, $"{left.Count} mission(s) are not gold yet, but the game is offering none of them")
                    : new Choice(next.Info.Id, $"not gold yet ({left.Count} left)");
            }

            case ChainMode.LevelTool:
            {
                if (tool == null)
                    return new Choice(0, "the cosmic tool's state cannot be read");
                if (tool.ReadyToHandIn)
                    return new Choice(0, $"the tool has everything stage {tool.Stage} asks for: hand it in at Researchingway (Selene cannot do that yet)", Finished: true);
                if (tool.Stage >= tool.StarCap && tool.Full)
                    return new Choice(0, $"the tool is at stage {tool.Stage}, the top this star allows, and full", Finished: true);

                var best = offered
                    .Where(row => !row.Info.IsMaster && row.Info.Reward != null)
                    .Select(row => (Row: row, Worth: tool.Worth(row.Info.Reward!)))
                    .Where(scored => scored.Worth > 0)
                    .OrderByDescending(scored => scored.Worth).ThenBy(scored => scored.Row.Info.Id)
                    .FirstOrDefault();
                return best.Row == null
                    ? new Choice(0, "no mission on offer pays the data the tool still lacks")
                    : new Choice(best.Row.Info.Id, $"pays the most of what the tool lacks ({best.Row.Info.Reward!.ToolDataText})");
            }

            case ChainMode.ClassPoints:
            {
                // The ticked Master missions; with none ticked, any of them.
                var masters = offered.Where(row => row.Info.IsMaster).ToList();
                var wanted = masters.Any(row => row.Picked) ? masters.Where(row => row.Picked).ToList() : masters;
                if (wanted.Count == 0)
                    return new Choice(0, "no Master mission is on offer");

                // One not measured yet goes first - there is nothing to compare it by; then the best a minute.
                var unmeasured = wanted.Where(row => row.Payout?.PerMinute == null).OrderBy(row => row.Info.Id).FirstOrDefault();
                if (unmeasured != null)
                    return new Choice(unmeasured.Info.Id, "not measured yet: run once to learn what it pays a minute");

                var top = wanted.OrderByDescending(row => row.Payout!.PerMinute!.Value).First();
                return new Choice(top.Info.Id, $"the most class points a minute measured ({top.Payout!.PerMinute!.Value:N0})");
            }

            default:
            {
                var wanted = offered.Where(row => row.Picked || wholeTabs.Contains(row.Info.Category)).ToList();
                if (wanted.Count == 0)
                    return new Choice(0, "none of the missions ticked is on offer (tick a whole tab to take whatever comes)");

                // The bonus Master mission first, as a single Run does; else one of them by the dice, so the
                // order is not the same every evening.
                var bonus = wanted.FirstOrDefault(row => row.Bonus);
                if (bonus != null)
                    return new Choice(bonus.Info.Id, "the bonus Master mission");

                var dice = new SeededRandom(seed, salt);
                var picked = wanted[Math.Min((int)(dice.NextDouble() * wanted.Count), wanted.Count - 1)];
                return new Choice(picked.Info.Id, wanted.Count == 1 ? "the one ticked mission on offer" : $"one of {wanted.Count} ticked missions on offer");
            }
        }
    }

    private static int TabOrder(MissionCategory category) => category switch
    {
        MissionCategory.RankD => 0,
        MissionCategory.RankC => 1,
        MissionCategory.RankB => 2,
        MissionCategory.RankA => 3,
        _ => 4,
    };
}

/// <summary>
/// Missions one after another (the user, 2026-11-14: "now to build the quest chaining for different types").
/// It only decides WHEN the next mission starts and WHICH (through the chooser); the mission itself is
/// <see cref="MissionRun"/>'s. Between two missions it waits a while that is not the same every time - the
/// regularity a flat pace has is what this plugin exists to avoid - and now and then longer.
/// It ends when the mode's goal is reached, when nothing wanted is on offer (no re-rolling), at a limit the
/// user set, when a mission's run halts or is refused (worn gear, Hephaestus busy), or when it is stopped.
/// Pure: the run's phase and the clock in, at most "start this mission" out.
/// </summary>
public sealed class MissionChain
{
    /// <summary>Seconds between a report and the next mission's board: the usual range, and the longer one taken about one time in eight.</summary>
    // [SUPERSEDED 2026-11-14 — the first chain in game (Gold all, Blacksmith): the user found the wait before the
    //  next mission odd and asked for it shorter]
    // public static readonly (int Min, int Max) Pause = (5, 22);
    // public static readonly (int Min, int Max) LongPause = (40, 130);
    // public const double LongPauseChance = 1.0 / 6.0;
    // The user, the same day: "max of 60s" - no wait between two missions is ever longer than a minute.
    public static readonly (int Min, int Max) Pause = (3, 9);
    public static readonly (int Min, int Max) LongPause = (20, 60);
    public const double LongPauseChance = 1.0 / 8.0;

    private DateTime _nextAt;
    private bool _ran;
    private bool _stopAfter;

    public bool Active { get; private set; }

    public ChainMode Mode { get; private set; }

    /// <summary>Missions reported since the chain began.</summary>
    public int Completed { get; private set; }

    /// <summary>Stop after this many missions; 0 = no limit.</summary>
    public int Limit { get; private set; }

    public string Status { get; private set; } = "Idle — no chain started.";

    public void Start(ChainMode mode, int limit, DateTime now)
    {
        Active = true;
        Mode = mode;
        Limit = Math.Max(0, limit);
        Completed = 0;
        _ran = false;
        _stopAfter = false;
        _nextAt = now;
        Status = "Starting";
    }

    /// <summary>End it now. The mission in progress, if any, is the run's to stop.</summary>
    public void Stop(string why)
    {
        if (!Active)
            return;

        Active = false;
        Status = $"{why} {Summary}";
    }

    /// <summary>Let the mission in progress finish and be reported, then end.</summary>
    public void StopAfterThis()
    {
        if (Active)
            _stopAfter = true;
    }

    /// <param name="run">The mission run's phase.</param>
    /// <param name="runStatus">What the run says of itself.</param>
    /// <param name="choose">The next mission for the mode; asked only when one is due.</param>
    /// <returns>The mission to start now; 0 = none.</returns>
    public uint Tick(RunPhase run, string runStatus, Func<Choice> choose, DateTime now, ulong seed)
    {
        if (!Active)
            return 0;

        var running = run is not (RunPhase.Idle or RunPhase.Done or RunPhase.Halted);
        if (running)
        {
            _ran = true;
            Status = $"Mission {Completed + 1}{(Limit > 0 ? $" of {Limit}" : string.Empty)}: {runStatus}{(_stopAfter ? " (stopping after it)" : string.Empty)}";
            return 0;
        }

        if (_ran)
        {
            // The mission this chain started has ended, one way or the other.
            _ran = false;
            if (run == RunPhase.Halted)
            {
                Stop($"Stopped — the mission's run halted ({runStatus})");
                return 0;
            }

            Completed++;
            if (Limit > 0 && Completed >= Limit)
            {
                Stop("Done — the number of missions asked for.");
                return 0;
            }

            if (_stopAfter)
            {
                Stop("Stopped after the mission, as asked.");
                return 0;
            }

            // A person does not open the board the instant a report lands - and not after the same wait each time.
            var dice = new SeededRandom(seed, unchecked((int)(now.Ticks / TimeSpan.TicksPerSecond)));
            var (min, max) = dice.NextDouble() < LongPauseChance ? LongPause : Pause;
            var wait = min + (int)(dice.NextDouble() * (max - min + 1));
            _nextAt = now + TimeSpan.FromSeconds(wait);
            Status = $"{Completed} done — the next mission in {wait} s";
            return 0;
        }

        if (now < _nextAt)
        {
            Status = $"{Completed} done — the next mission in {Math.Max(1, (int)(_nextAt - now).TotalSeconds)} s";
            return 0;
        }

        var choice = choose();
        if (choice.MissionId == 0)
        {
            Stop(choice.Finished ? $"Done — {choice.Why}." : $"Stopped — {choice.Why}.");
            return 0;
        }

        Status = $"Mission {Completed + 1}: taking {choice.MissionId} — {choice.Why}";
        _ran = true; // if the run refuses it, the caller stops the chain
        return choice.MissionId;
    }

    private string Summary => Completed == 1 ? "(1 mission reported)" : $"({Completed} missions reported)";
}
