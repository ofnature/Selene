using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Selene.Features.Missions;

public enum RunPhase
{
    Idle,

    /// <summary>Opening the game's Stellar Missions board.</summary>
    OpeningBoard,

    /// <summary>The board is open; the mission has to be among the rows it shows (the Tool Mastery tab).</summary>
    FindingMission,

    /// <summary>Selecting the mission's row.</summary>
    Selecting,

    /// <summary>Pressing Initiate until the game shows the mission in progress.</summary>
    Initiating,

    /// <summary>Opening the Cosmic Crafting Log from the Mission in Progress window.</summary>
    OpeningLog,

    /// <summary>Selecting the mission's recipe in the log, once.</summary>
    SelectingRecipe,

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

    /// <summary>The Stellar Missions button of the Cosmic HUD.</summary>
    OpenBoard,

    /// <summary>The board's Tool Mastery tab.</summary>
    ShowMasteryTab,

    /// <summary>Select the mission's row on the board.</summary>
    SelectMission,

    /// <summary>The board's Initiate, for the selected mission.</summary>
    InitiateMission,

    /// <summary>Yes to the game's "Commence selected mission?" - that question, for this mission, and no other.</summary>
    ConfirmMission,

    /// <summary>The Cosmic Crafting Log button of the Mission in Progress window.</summary>
    OpenLog,

    /// <summary>Select the first recipe of the open Cosmic Crafting Log (a Master mission has one).</summary>
    SelectRecipe,

    /// <summary><c>Hephaestus.CraftSelected(<see cref="MissionRun.CraftAmount"/>)</c>.</summary>
    StartCraft,

    /// <summary><c>Hephaestus.RequestStopAfterRun</c>: the item in hand is finished, nothing new starts.</summary>
    StopAfterItem,

    /// <summary>The Report button of the game's Mission in Progress window.</summary>
    Report,
}

/// <param name="LogOpen">The Cosmic Crafting Log is on screen.</param>
/// <param name="BoardOpen">The Stellar Missions board is on screen.</param>
/// <param name="BoardMissions">The missions in the rows the board shows right now.</param>
/// <param name="BoardSelected">The mission selected on the board; 0 = none.</param>
/// <param name="ConfirmMission">The mission the game is asking to commence right now ("Commence selected mission?"); 0 = it is not asking.</param>
public readonly record struct RunObservation(
    MissionState Mission, HephaestusStatus Hephaestus, bool LogOpen,
    bool BoardOpen = false, IReadOnlyList<uint>? BoardMissions = null, uint BoardSelected = 0, uint ConfirmMission = 0)
{
    public bool Lists(uint missionId) => BoardMissions != null && BoardMissions.Contains(missionId);
}

/// <summary>
/// ONE mission, ATTENDED: accept it on the game's board, open the Cosmic Crafting Log, start Hephaestus,
/// watch the clock, have the item in hand finished before the time is out, press Report. It runs a mission
/// the user TICKED and no other, and it never abandons one. (First built from the craft onward - the user,
/// 2026-11-12: "Yes, I accept first" - and run once that way; then: "have selene accept the mission and
/// open the log itself".)
///
/// Pure: an observation in, at most one command out. Every outcome is judged by reading the game again -
/// the board open, the mission in progress, Hephaestus busy, the mission gone - never by what a call
/// returned. Every step has a pace, a deadline and a cap on repeats; when one runs out the run HALTS and
/// says why, and a person takes over. It answers ONE dialog - the game's own "Commence selected mission?"
/// for the mission it went for; any other that comes up stops it at that step.
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

    /// <summary>
    /// Report gets more tries than a step: one may go on bringing the Mission in Progress window up first
    /// (the second full run halted with that window not on screen), and a mission left unreported runs out.
    /// </summary>
    public const int ReportAttempts = 5;

    public static readonly TimeSpan Pace = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan StepDeadline = TimeSpan.FromSeconds(20);
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

    public bool Active => Phase is not (RunPhase.Idle or RunPhase.Done or RunPhase.Halted);

    /// <summary>The run is still getting the mission accepted: it is not in progress yet.</summary>
    private bool Accepting => Phase is RunPhase.OpeningBoard or RunPhase.FindingMission or RunPhase.Selecting or RunPhase.Initiating;

    /// <summary>
    /// Begin, if everything a run needs is there. With a mission already in progress it is finished from
    /// where it stands; with none, a ticked mission the game offers is accepted first - the bonus one if it
    /// is ticked, else the first. False = nothing started, and <see cref="Status"/> says what is missing.
    /// </summary>
    /// <param name="picked">The missions the user ticked: Selene runs no other.</param>
    public bool Start(RunObservation obs, IReadOnlyCollection<uint> picked, DateTime now)
    {
        if (Active)
            return false;

        var mission = obs.Mission;
        var hephaestus = obs.Hephaestus;
        var offeredAndPicked = mission.OfferedMaster.Where(picked.Contains).ToList();

        // Checked BEFORE anything is accepted: no mission is taken on gear, or a crafter, that cannot do it.
        var refusal =
            !mission.InZone ? "not in a Cosmic Exploration zone"
            : mission.InMission && !picked.Contains(mission.CurrentMissionId) ? $"mission {mission.CurrentMissionId} is not one you ticked"
            : !mission.InMission && offeredAndPicked.Count == 0 ? "none of the missions you ticked is offered right now"
            : !hephaestus.Available ? "Hephaestus is not loaded"
            : hephaestus.Busy ? "Hephaestus is busy"
            : !hephaestus.ReportsGear ? "this Hephaestus does not report its gear gate — update Hephaestus"
            : !hephaestus.GearFit ? $"gear is at {hephaestus.GearConditionPercent}% (gate {hephaestus.GearGatePercent}%) — repair first"
            : !mission.InMission ? null
            : mission.ClockSeconds is not { } clock ? "the mission's clock cannot be seen"
            : clock <= StopMarginSeconds ? $"only {clock} s left on the clock — report it yourself"
            : null;

        if (refusal != null)
        {
            Phase = RunPhase.Idle;
            Status = $"Not started — {refusal}.";
            return false;
        }

        if (mission.InMission)
        {
            MissionId = mission.CurrentMissionId;
            LastScore = mission.Score;
            if (obs.LogOpen)
                Enter(RunPhase.StartingCraft, now, StartDeadline, "Asking Hephaestus to craft");
            else
                Enter(RunPhase.OpeningLog, now, StepDeadline, "Opening the Cosmic Crafting Log");
            return true;
        }

        MissionId = offeredAndPicked.Contains(mission.BonusMasterId) ? mission.BonusMasterId : offeredAndPicked[0];
        LastScore = 0;
        Enter(RunPhase.OpeningBoard, now, StepDeadline, $"Opening the mission board for mission {MissionId}");
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

        Halt(obs.Mission.InMission
            ? "Stopped by you — the mission is still in progress: report it yourself"
            : "Stopped by you — no mission was accepted");
        return obs.Hephaestus.Busy ? RunCommand.StopAfterItem : RunCommand.None;
    }

    public RunCommand Tick(RunObservation obs, DateTime now)
    {
        if (!Active)
            return RunCommand.None;

        return Accepting ? TickAccepting(obs, now) : TickMission(obs, now);
    }

    /// <summary>From the board to the mission in progress.</summary>
    private RunCommand TickAccepting(RunObservation obs, DateTime now)
    {
        var mission = obs.Mission;
        if (mission.InMission)
        {
            if (mission.CurrentMissionId != MissionId)
            {
                Halt($"Another mission ({mission.CurrentMissionId}) is in progress, not the one Selene went for");
                return RunCommand.None;
            }

            Enter(RunPhase.OpeningLog, now, StepDeadline, "Accepted — opening the Cosmic Crafting Log");
            return RunCommand.None;
        }

        switch (Phase)
        {
            case RunPhase.OpeningBoard:
                if (obs.BoardOpen)
                {
                    Enter(RunPhase.FindingMission, now, StepDeadline, "Looking for the mission on the board");
                    return RunCommand.None;
                }

                return Try(RunCommand.OpenBoard, now, "The mission board did not open");

            case RunPhase.FindingMission:
                if (!obs.BoardOpen)
                    return HaltNone("The mission board closed");
                if (obs.Lists(MissionId))
                {
                    Enter(RunPhase.Selecting, now, StepDeadline, "Selecting the mission");
                    return RunCommand.None;
                }

                return Try(RunCommand.ShowMasteryTab, now, "The mission is not among the board's rows");

            case RunPhase.Selecting:
                if (!obs.BoardOpen || !obs.Lists(MissionId))
                    return HaltNone("The mission left the board");
                if (obs.BoardSelected == MissionId)
                {
                    Enter(RunPhase.Initiating, now, StepDeadline, "Initiating the mission");
                    return RunCommand.None;
                }

                return Try(RunCommand.SelectMission, now, "The mission could not be selected");

            default: // Initiating
                // "Commence selected mission?" (seen in game 2026-11-12): answered Yes only when the game
                // says the question is about THIS mission. Any other question is left for a person.
                if (obs.ConfirmMission != 0)
                {
                    if (obs.ConfirmMission != MissionId)
                        return HaltNone($"The game asks to commence mission {obs.ConfirmMission}, not the one Selene went for");

                    Status = "Confirming the mission";
                    return Try(RunCommand.ConfirmMission, now, "The mission's confirmation would not take");
                }

                if (!obs.BoardOpen || obs.BoardSelected != MissionId || !obs.Lists(MissionId))
                {
                    // The board closes once the game takes the mission; give the mission a moment to show.
                    if (now > _deadline)
                        Halt("The mission was not accepted — a confirmation may be waiting for you");
                    return RunCommand.None;
                }

                return Try(RunCommand.InitiateMission, now, "The mission was not accepted — a confirmation may be waiting for you");
        }
    }

    /// <summary>From the mission in progress to its report.</summary>
    private RunCommand TickMission(RunObservation obs, DateTime now)
    {
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
            case RunPhase.OpeningLog:
                if (obs.LogOpen)
                {
                    Enter(RunPhase.SelectingRecipe, now, StepDeadline, "Selecting the recipe");
                    return RunCommand.None;
                }

                return Try(RunCommand.OpenLog, now, "The Cosmic Crafting Log did not open");

            case RunPhase.SelectingRecipe:
                // Once: what a click on the row does. Whether it took shows in Hephaestus starting or refusing.
                Enter(RunPhase.StartingCraft, now, StartDeadline, "Asking Hephaestus to craft");
                _nextCommandAt = now + Pace;
                return RunCommand.SelectRecipe;

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
                        return HaltNone($"Hephaestus stopped before anything was scored ({why})");

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

                return Try(RunCommand.Report, now, "The report did not go through", ReportAttempts);

            default:
                return RunCommand.None;
        }
    }

    /// <summary>The same command again only after <see cref="Pace"/>, at most <see cref="MaxAttempts"/> times, never past the deadline.</summary>
    private RunCommand Try(RunCommand command, DateTime now, string failure, int attempts = MaxAttempts)
    {
        if (now < _nextCommandAt)
            return RunCommand.None;

        if (_attempts >= attempts || now > _deadline)
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

    private RunCommand HaltNone(string why)
    {
        Halt(why);
        return RunCommand.None;
    }

    private void Halt(string why)
    {
        Phase = RunPhase.Halted;
        Status = $"Halted — {why}.";
    }

    private static string Number(uint value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
