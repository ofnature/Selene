using System;
using System.Collections.Generic;
using System.Numerics;
using Selene.Features.Fishing;

namespace Selene.Features.Repair;

/// <summary>A star's mender: the NPC, where it stands, and the middle of the spot players stand at to talk to it.</summary>
public sealed record Mender(uint NpcId, Vector3 At, Vector3 StandNear);

/// <summary>
/// Godgyth, "Merchant &amp; Mender", at each star's hub. The ids and where each stands are ICE's
/// (<c>NpcData.cs</c>) AND CHECKED against the game's data 2026-11-15 (the name, the title, and the position
/// in each zone's <c>planevent.lgb</c>). <see cref="Mender.StandNear"/> is ICE's "circle" point, 3-4 y from
/// the NPC - it cannot be checked offline; the walk asks the mesh about it before a step is taken.
/// The NPC has seven handlers in the data - five shops, a gil shop, and the mender (CustomTalk 720915, whose
/// menu entry reads "Repair Gear"); the menu itself ends with the game's own "Nothing" (seen 2026-11-15, when
/// taking the LAST entry closed it). The entry is found by its text.
/// </summary>
public static class Menders
{
    private static readonly IReadOnlyDictionary<uint, Mender> ByTerritory = new Dictionary<uint, Mender>
    {
        [1237] = new(1052610, new Vector3(19.46f, 1.70f, 18.11f), new Vector3(16.97f, 1.69f, 15.16f)),
        [1291] = new(1052641, new Vector3(359.53f, 52.69f, -401.70f), new Vector3(357.25f, 52.69f, -405.16f)),
        [1310] = new(1052651, new Vector3(-202.42f, 0.65f, 154.33f), new Vector3(-198.98f, 0.50f, 153.69f)),
        [1319] = new(1056825, new Vector3(317.70f, 205.77f, 374.80f), new Vector3(315.01f, 205.64f, 375.59f)),
    };

    public static Mender? For(uint territoryId) => ByTerritory.TryGetValue(territoryId, out var mender) ? mender : null;
}

public enum MenderPhase
{
    Idle,

    /// <summary>Walking to a spot beside the mender (the walk is <see cref="StandWalk"/>'s).</summary>
    Walking,

    /// <summary>Interacting with the mender until its menu (or the repair window) is up.</summary>
    Talking,

    /// <summary>Taking the menu's mender entry until the repair window is up.</summary>
    Choosing,

    /// <summary>Repair All, the game's question, and the gear's condition read back.</summary>
    Repairing,

    /// <summary>Closing the repair window and the menu.</summary>
    Closing,

    Done,

    /// <summary>Stopped with the reason in <see cref="MenderTrip.Status"/>; a person takes it from here.</summary>
    Halted,
}

public enum MenderCommand
{
    None,

    /// <summary>Begin the walk to <see cref="MenderTrip.Stand"/>, to end facing the mender.</summary>
    StartWalk,

    StopWalk,

    /// <summary>Target the mender and interact.</summary>
    Interact,

    /// <summary>The menu's entry that reads as the mender's ("Repair Gear").</summary>
    ChooseMender,

    /// <summary>The repair window's Repair All.</summary>
    RepairAll,

    /// <summary>Yes to "Repair all displayed items for N gil?" - that question and no other.</summary>
    ConfirmRepair,

    /// <summary>Close the repair window, or the menu behind it.</summary>
    Close,
}

/// <param name="InWorld">There is a character in the world.</param>
/// <param name="GearPercent">The lowest condition among the gear worn.</param>
/// <param name="MenderDistance">How far the mender stands from the character; null = not in sight.</param>
/// <param name="MenuOpen">The mender's menu is on screen.</param>
/// <param name="RepairOpen">The game's repair window is on screen.</param>
/// <param name="RepairAsked">The game is asking "Repair all displayed items for N gil?" right now.</param>
/// <param name="Walk">The walk's phase.</param>
/// <param name="WalkStatus">What the walk says of itself.</param>
public readonly record struct MenderObservation(
    bool InWorld, int GearPercent, float? MenderDistance, bool MenuOpen, bool RepairOpen, bool RepairAsked,
    WalkPhase Walk, string WalkStatus);

/// <summary>
/// The repair between missions (the user, 2026-11-15: "need to wire in repair like ice does ... selene goes
/// and repairs prior to starting another missin"; how: "Mender at the hub"): walk to a spot beside the star's
/// mender - not the same spot every time -, talk, take the mender's entry, Repair All, answer the game's
/// question, and see the gear's condition RISE before calling it done. Gil is the character's business: when
/// there is not enough the condition does not rise, and this says so.
///
/// Pure: an observation in, at most one command out. Every outcome is read off the game; every step has a
/// pace, a deadline and a cap; when one runs out it HALTS and says why. It answers ONE dialog.
/// </summary>
public sealed class MenderTrip
{
    /// <summary>Close enough to talk to the mender.</summary>
    public const float TalkWithin = 6.5f;

    /// <summary>The gear counts as repaired at this condition.</summary>
    public const int RepairedPercent = 99;

    public const int MaxAttempts = 3;
    public const int RepairAttempts = 5;

    public static readonly TimeSpan Pace = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan WalkDeadline = TimeSpan.FromSeconds(150);
    public static readonly TimeSpan StepDeadline = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan RepairDeadline = TimeSpan.FromSeconds(30);

    private DateTime _deadline;
    private DateTime _nextCommandAt;
    private int _attempts;
    private int _before;
    private bool _walkSeen;

    public MenderPhase Phase { get; private set; } = MenderPhase.Idle;

    public string Status { get; private set; } = "Idle — no repair asked for.";

    /// <summary>Where the walk is to go: a spot beside the mender, picked by the caller's dice.</summary>
    public Vector3 Stand { get; private set; }

    public Mender? Mender { get; private set; }

    public bool Active => Phase is not (MenderPhase.Idle or MenderPhase.Done or MenderPhase.Halted);

    /// <summary>Begin, if a repair can be made here. False = nothing started, and <see cref="Status"/> says why.</summary>
    /// <param name="mender">The star's mender; null = none is known for this zone.</param>
    /// <param name="stand">A spot beside the mender to walk to.</param>
    /// <param name="blocked">Why Selene may not walk off now (a mission in progress, a run going); null = it may.</param>
    public bool Start(MenderObservation obs, Mender? mender, Vector3 stand, string? blocked, DateTime now)
    {
        if (Active)
            return false;

        var refusal =
            !obs.InWorld ? "there is no character in the world"
            : mender == null ? "no mender is known for this zone"
            : blocked != null ? blocked
            : obs.GearPercent >= RepairedPercent ? $"the gear is at {obs.GearPercent}%: nothing to repair"
            : null;

        if (refusal != null)
        {
            Phase = MenderPhase.Idle;
            Status = $"Not started — {refusal}.";
            return false;
        }

        Mender = mender;
        Stand = stand;
        _before = obs.GearPercent;
        _walkSeen = false;

        // Standing beside the mender already: no walk.
        if (obs.MenderDistance is { } near && near <= TalkWithin)
            Enter(MenderPhase.Talking, now, StepDeadline, $"Gear at {_before}% — at the mender already");
        else
            Enter(MenderPhase.Walking, now, WalkDeadline, $"Gear at {_before}% — walking to the mender");
        return true;
    }

    /// <summary>The user's Stop: it ends where it stands; a walk in progress is stopped.</summary>
    public MenderCommand Stop(MenderObservation obs)
    {
        if (!Active)
            return MenderCommand.None;

        var walking = Phase == MenderPhase.Walking && IsWalking(obs.Walk);
        Halt("Stopped by you");
        return walking ? MenderCommand.StopWalk : MenderCommand.None;
    }

    public MenderCommand Tick(MenderObservation obs, DateTime now)
    {
        if (!Active)
            return MenderCommand.None;

        if (!obs.InWorld)
            return HaltNone("The character left the world");

        switch (Phase)
        {
            case MenderPhase.Walking:
                if (IsWalking(obs.Walk))
                {
                    _walkSeen = true;
                    if (now > _deadline)
                    {
                        Halt("The walk to the mender took too long");
                        return MenderCommand.StopWalk;
                    }

                    Status = obs.MenderDistance is { } left ? $"Walking to the mender — {left:0} y away" : "Walking to the mender";
                    return MenderCommand.None;
                }

                // A walk's verdict counts only for the walk THIS trip began (the last trip's is still on the walk).
                if (_walkSeen && obs.Walk == WalkPhase.Done)
                {
                    Enter(MenderPhase.Talking, now, StepDeadline, "At the mender");
                    return MenderCommand.None;
                }

                if (_walkSeen && obs.Walk == WalkPhase.Halted)
                    return HaltNone($"The walk to the mender failed ({obs.WalkStatus})");

                return Try(MenderCommand.StartWalk, now, $"The walk to the mender did not start ({obs.WalkStatus})");

            case MenderPhase.Talking:
                if (obs.MenuOpen || obs.RepairOpen)
                {
                    Enter(MenderPhase.Choosing, now, StepDeadline, "The mender's menu is up");
                    return MenderCommand.None;
                }

                if (obs.MenderDistance is not { } distance)
                    return HaltNone("The mender is not in sight");
                if (distance > TalkWithin)
                    return HaltNone($"The mender is {distance:0.0} y away — too far to talk to");

                return Try(MenderCommand.Interact, now, "The mender did not answer");

            case MenderPhase.Choosing:
                if (obs.RepairOpen)
                {
                    Enter(MenderPhase.Repairing, now, RepairDeadline, "The repair window is up");
                    return MenderCommand.None;
                }

                if (!obs.MenuOpen)
                {
                    // Between the menu closing and the repair window opening.
                    if (now > _deadline)
                        Halt("The repair window did not open — the Debug page's Mender line says what the menu held");
                    return MenderCommand.None;
                }

                return Try(MenderCommand.ChooseMender, now, "The repair window did not open — the Debug page's Mender line says what the menu held");

            case MenderPhase.Repairing:
                if (obs.GearPercent > _before && obs.GearPercent >= RepairedPercent)
                {
                    Enter(MenderPhase.Closing, now, StepDeadline, $"Repaired to {obs.GearPercent}% — closing the window");
                    return MenderCommand.None;
                }

                if (obs.RepairAsked)
                    return Try(MenderCommand.ConfirmRepair, now, "The gear did not repair — not enough gil?", RepairAttempts);
                if (obs.RepairOpen)
                    return Try(MenderCommand.RepairAll, now, "The gear did not repair — not enough gil?", RepairAttempts);

                if (now > _deadline)
                    Halt("The repair window closed before the gear was repaired");
                return MenderCommand.None;

            default: // Closing
                if (!obs.RepairOpen && !obs.MenuOpen)
                {
                    Phase = MenderPhase.Done;
                    Status = $"Done — gear repaired from {_before}% to {obs.GearPercent}%.";
                    return MenderCommand.None;
                }

                return Try(MenderCommand.Close, now, "The mender's windows would not close", RepairAttempts);
        }
    }

    private static bool IsWalking(WalkPhase walk)
        => walk is WalkPhase.Asking or WalkPhase.Starting or WalkPhase.Walking or WalkPhase.Facing;

    /// <summary>The same command again only after <see cref="Pace"/>, a few times at most, never past the deadline.</summary>
    private MenderCommand Try(MenderCommand command, DateTime now, string failure, int attempts = MaxAttempts)
    {
        if (now < _nextCommandAt)
            return MenderCommand.None;

        if (_attempts >= attempts || now > _deadline)
            return HaltNone(failure);

        _attempts++;
        _nextCommandAt = now + Pace;
        return command;
    }

    private void Enter(MenderPhase phase, DateTime now, TimeSpan deadline, string status)
    {
        Phase = phase;
        Status = status;
        _deadline = now + deadline;
        _nextCommandAt = now;
        _attempts = 0;
    }

    private MenderCommand HaltNone(string why)
    {
        Halt(why);
        return MenderCommand.None;
    }

    private void Halt(string why)
    {
        Phase = MenderPhase.Halted;
        Status = $"Halted — {why}.";
    }
}
