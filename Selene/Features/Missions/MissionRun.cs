using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Selene.Features.Missions;

public enum RunPhase
{
    Idle,

    /// <summary>Hephaestus has been asked to craft; waiting to see it busy.</summary>
    StartingCraft,

    /// <summary>Hephaestus is making items; the clock is watched.</summary>
    Crafting,

    /// <summary>The clock is nearly out: Hephaestus finishes the item in hand and starts no other.</summary>
    Finishing,

    /// <summary>Pressing Report until the game shows the mission gone.</summary>
    Reporting,

    Done,

    /// <summary>Stopped with the reason in <see cref="MissionRun.Status"/>; a person takes it from here.</summary>
    Halted,
}

public enum RunCommand
{
    None,

    /// <summary><c>Hephaestus.CraftSelected(<see cref="MissionRun.CraftAmount"/>)</c>.</summary>
    StartCraft,

    /// <summary><c>Hephaestus.RequestStopAfterRun</c>: the item in hand is finished, nothing new starts.</summary>
    StopAfterItem,

    /// <summary>The Report button of the game's Mission in Progress window.</summary>
    Report,
}

/// <param name="LogOpen">The Cosmic Crafting Log is on screen.</param>
public readonly record struct RunObservation(MissionState Mission, HephaestusStatus Hephaestus, bool LogOpen);

/// <summary>
/// ONE mission, ATTENDED, from the point where the user has accepted it and opened the Cosmic Crafting Log
/// on its recipe (the user, 2026-11-12: "Yes, I accept first"): start Hephaestus, watch the clock, have the
/// item in hand finished before the time is out, press Report. It never accepts and never abandons.
///
/// Pure: an observation in, at most one command out. Every outcome is judged by reading the game again -
/// Hephaestus busy, the mission gone - never by what a call returned. Every step has a pace, a deadline and
/// a cap on repeats; when one runs out the run HALTS and says why, and a person takes over.
/// </summary>
public sealed class MissionRun
{
    /// <summary>"A large amount - the clock or Stop ends it" (docs/scoping-decisions.md).</summary>
    public const int CraftAmount = 999;

    /// <summary>
    /// With this little left on the clock, no new item is started: the one in hand is finished and the
    /// mission reported. An item took up to 93 s in the user's runs (Hephaestus's notes); what the game does
    /// with a score when the clock runs out mid-craft is NOT known, so it is not allowed to come to that.
    /// </summary>
    public const int StopMarginSeconds = 100;

    public const int MaxAttempts = 3;

    public static readonly TimeSpan Pace = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan StartDeadline = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan FinishDeadline = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan ReportDeadline = TimeSpan.FromSeconds(40);

    private DateTime _deadline;
    private DateTime _nextCommandAt;
    private int _attempts;

    public RunPhase Phase { get; private set; } = RunPhase.Idle;

    /// <summary>What the run is doing, or why it is not.</summary>
    public string Status { get; private set; } = "Idle — no run started.";

    public uint MissionId { get; private set; }

    /// <summary>The score last seen while the mission was in progress.</summary>
    public uint LastScore { get; private set; }

    public bool Active => Phase is RunPhase.StartingCraft or RunPhase.Crafting or RunPhase.Finishing or RunPhase.Reporting;

    /// <summary>
    /// Begin, if everything a run needs is there. False = nothing started, and <see cref="Status"/> says
    /// what is missing.
    /// </summary>
    /// <param name="picked">The missions the user ticked: Selene runs no other.</param>
    public bool Start(RunObservation obs, IReadOnlyCollection<uint> picked, DateTime now)
    {
        if (Active)
            return false;

        var mission = obs.Mission;
        var hephaestus = obs.Hephaestus;
        var refusal =
            !mission.InZone ? "not in a Cosmic Exploration zone"
            : !mission.InMission ? "accept a mission first"
            : !picked.Contains(mission.CurrentMissionId) ? $"mission {mission.CurrentMissionId} is not one you ticked"
            : !hephaestus.Available ? "Hephaestus is not loaded"
            : hephaestus.Busy ? "Hephaestus is busy"
            : !hephaestus.ReportsGear ? "this Hephaestus does not report its gear gate — update Hephaestus"
            : !hephaestus.GearFit ? $"gear is at {hephaestus.GearConditionPercent}% (gate {hephaestus.GearGatePercent}%) — repair first"
            : !obs.LogOpen ? "open the Cosmic Crafting Log on the mission's recipe first"
            : mission.ClockSeconds is not { } clock ? "the mission's clock cannot be seen"
            : clock <= StopMarginSeconds ? $"only {clock} s left on the clock — report it yourself"
            : null;

        if (refusal != null)
        {
            Phase = RunPhase.Idle;
            Status = $"Not started — {refusal}.";
            return false;
        }

        MissionId = mission.CurrentMissionId;
        LastScore = mission.Score;
        Enter(RunPhase.StartingCraft, now, StartDeadline, "Asking Hephaestus to craft");
        return true;
    }

    /// <summary>
    /// The user's Stop: the run ends here and the mission is left as it is. If Hephaestus is crafting it is
    /// told to finish the item in hand and start no other.
    /// </summary>
    public RunCommand Stop(RunObservation obs)
    {
        if (!Active)
            return RunCommand.None;

        Halt("Stopped by you — the mission is still in progress: report it yourself");
        return obs.Hephaestus.Busy ? RunCommand.StopAfterItem : RunCommand.None;
    }

    public RunCommand Tick(RunObservation obs, DateTime now)
    {
        if (!Active)
            return RunCommand.None;

        var mission = obs.Mission;
        var busy = obs.Hephaestus.Busy;

        // The mission is no longer ours to work on.
        if (!mission.InMission || mission.CurrentMissionId != MissionId)
        {
            if (Phase == RunPhase.Reporting)
            {
                Phase = RunPhase.Done;
                Status = $"Done — reported with a score of {Number(LastScore)}.";
                return RunCommand.None;
            }

            Halt("The mission ended before Selene reported it");
            return busy ? RunCommand.StopAfterItem : RunCommand.None;
        }

        LastScore = mission.Score;
        var clock = mission.ClockSeconds;

        switch (Phase)
        {
            case RunPhase.StartingCraft:
                if (busy)
                {
                    Enter(RunPhase.Crafting, now, TimeSpan.MaxValue, "Crafting");
                    return RunCommand.None;
                }

                return Try(RunCommand.StartCraft, now, "Hephaestus did not start crafting");

            case RunPhase.Crafting:
                if (clock is not { } left || left <= StopMarginSeconds)
                {
                    Enter(RunPhase.Finishing, now, FinishDeadline,
                        clock == null ? "The clock cannot be seen — finishing the item in hand" : $"{clock} s left — finishing the item in hand");
                    return busy ? RunCommand.StopAfterItem : RunCommand.None;
                }

                if (!busy)
                {
                    // Hephaestus stopped by itself: out of materials, worn gear, a failed start.
                    var why = obs.Hephaestus.RunHalt != "None" ? obs.Hephaestus.RunHalt : "it stopped";
                    if (mission.Score == 0)
                    {
                        Halt($"Hephaestus stopped before anything was scored ({why})");
                        return RunCommand.None;
                    }

                    Enter(RunPhase.Reporting, now, ReportDeadline, $"Hephaestus stopped ({why}) — reporting what was made");
                    return RunCommand.None;
                }

                Status = $"Crafting — score {Number(mission.Score)}, {left / 60}:{left % 60:00} left";
                return RunCommand.None;

            case RunPhase.Finishing:
                if (!busy)
                {
                    Enter(RunPhase.Reporting, now, ReportDeadline, "Reporting");
                    return RunCommand.None;
                }

                if (now > _deadline)
                    Halt("Hephaestus did not finish the item in time — report the mission yourself");
                return RunCommand.None;

            case RunPhase.Reporting:
                if (busy)
                {
                    // Still leaving the crafting stance: the game takes no report yet.
                    if (now > _deadline)
                        Halt("Hephaestus did not let go of the character — report the mission yourself");
                    return RunCommand.None;
                }

                return Try(RunCommand.Report, now, "The report did not go through");

            default:
                return RunCommand.None;
        }
    }

    /// <summary>The same command again only after <see cref="Pace"/>, at most <see cref="MaxAttempts"/> times, never past the deadline.</summary>
    private RunCommand Try(RunCommand command, DateTime now, string failure)
    {
        if (now < _nextCommandAt)
            return RunCommand.None;

        if (_attempts >= MaxAttempts || now > _deadline)
        {
            Halt($"{failure} — take it from here");
            return RunCommand.None;
        }

        _attempts++;
        _nextCommandAt = now + Pace;
        return command;
    }

    private void Enter(RunPhase phase, DateTime now, TimeSpan deadline, string status)
    {
        Phase = phase;
        Status = status;
        _deadline = deadline == TimeSpan.MaxValue ? DateTime.MaxValue : now + deadline;
        _nextCommandAt = now;
        _attempts = 0;
    }

    private void Halt(string why)
    {
        Phase = RunPhase.Halted;
        Status = $"Halted — {why}.";
    }

    private static string Number(uint value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
