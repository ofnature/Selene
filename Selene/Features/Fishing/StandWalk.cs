using System;
using System.Numerics;

namespace Selene.Features.Fishing;

public enum WalkPhase
{
    Idle,

    /// <summary>Asking the navigation whether its mesh reaches the place. Nothing has moved yet.</summary>
    Asking,

    /// <summary>The move has been asked for; waiting to see the navigation busy.</summary>
    Starting,

    Walking,

    /// <summary>At the place; turning to the water.</summary>
    Facing,

    Done,

    /// <summary>Stopped with the reason in <see cref="StandWalk.Status"/>.</summary>
    Halted,
}

public enum WalkCommand
{
    None,

    /// <summary>Ask the navigation for the nearest point its mesh reaches, near the place.</summary>
    AskReach,

    /// <summary>Walk to <see cref="StandWalk.Goal"/>, on foot.</summary>
    Move,

    /// <summary>Stop the navigation.</summary>
    Stop,

    /// <summary>Turn the character to <see cref="StandWalk.Facing"/>.</summary>
    Face,
}

public enum ReachState
{
    NotAsked,
    Pending,

    /// <summary>The mesh reaches no point near the place.</summary>
    None,
    Found,
}

/// <param name="Position">Where the character stands; null = no character in the world.</param>
/// <param name="NavBusy">The navigation is pathfinding or following a path.</param>
/// <param name="NavResult">How the navigation says its last move ended.</param>
/// <param name="ReachPoint">The mesh's nearest point, when <paramref name="Reach"/> is <see cref="ReachState.Found"/>.</param>
public readonly record struct WalkObservation(
    Vector3? Position, float Rotation, bool NavReady, bool NavBusy, string NavResult, ReachState Reach, Vector3 ReachPoint);

/// <summary>
/// A TEST, from the Debug page: walk to the place the look for water chose and face the water (the user,
/// 2026-11-14: "how to test walking there?" - "build the walk button"). The walking is Ariadne's, the
/// suite's navigation, which is what Demeter will use for the real thing; this is the first time Selene
/// itself moves the character, and it does so ONLY on that button.
///
/// Pure: an observation in, at most one command out. The mesh is asked first, so a place that cannot be
/// reached is refused before a step is taken. Every outcome is read off the game - the navigation busy,
/// where the character stands, which way it faces - and every step has a pace, a deadline and a cap.
/// </summary>
public sealed class StandWalk
{
    /// <summary>The mesh's nearest point may lie this far from the place, on the ground plane, and no further.</summary>
    public const float MeshWithin = 2f;

    /// <summary>Standing this close to where it meant to go is "there".</summary>
    public const float ArriveWithin = 1f;

    /// <summary>Radians.</summary>
    public const float FacingWithin = 0.15f;

    public const int MaxAttempts = 3;

    public static readonly TimeSpan Pace = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan AskDeadline = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StartDeadline = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan WalkDeadline = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan FaceDeadline = TimeSpan.FromSeconds(8);

    private DateTime _deadline;
    private DateTime _nextCommandAt;
    private int _attempts;

    public WalkPhase Phase { get; private set; } = WalkPhase.Idle;

    public string Status { get; private set; } = "Idle — no walk asked for.";

    /// <summary>The place the look for water chose.</summary>
    public Vector3 Target { get; private set; }

    /// <summary>Where the walk goes: the mesh's own point nearest the place.</summary>
    public Vector3 Goal { get; private set; }

    public float Facing { get; private set; }

    public bool Active => Phase is WalkPhase.Asking or WalkPhase.Starting or WalkPhase.Walking or WalkPhase.Facing;

    public bool Start(WalkObservation obs, Vector3 target, float facing, DateTime now)
    {
        if (Active)
            return false;

        var refusal =
            obs.Position == null ? "no character in the world"
            : !obs.NavReady ? "Ariadne is not loaded, or has no mesh for this zone"
            : obs.NavBusy ? "Ariadne is already moving the character"
            : null;
        if (refusal != null)
        {
            Phase = WalkPhase.Idle;
            Status = $"Not started — {refusal}.";
            return false;
        }

        Target = Goal = target;
        Facing = facing;
        Enter(WalkPhase.Asking, now, AskDeadline, "Asking Ariadne whether its mesh reaches the place");
        return true;
    }

    /// <summary>The user's Stop: the walk ends where the character is.</summary>
    public WalkCommand Stop(WalkObservation obs)
    {
        if (!Active)
            return WalkCommand.None;

        Halt("Stopped by you");
        return obs.NavBusy ? WalkCommand.Stop : WalkCommand.None;
    }

    public WalkCommand Tick(WalkObservation obs, DateTime now)
    {
        if (!Active)
            return WalkCommand.None;

        if (obs.Position is not { } position)
        {
            Halt("The character left the world");
            return obs.NavBusy ? WalkCommand.Stop : WalkCommand.None;
        }

        switch (Phase)
        {
            case WalkPhase.Asking:
                switch (obs.Reach)
                {
                    case ReachState.Found:
                        Goal = obs.ReachPoint;
                        Enter(WalkPhase.Starting, now, StartDeadline,
                            $"The mesh reaches {Flat(Goal, Target):0.0} y from the place — asking Ariadne to walk there");
                        return WalkCommand.None;

                    case ReachState.None:
                        return HaltNone($"Ariadne's mesh reaches no point within {MeshWithin:0} y of the place — it cannot be walked to");

                    case ReachState.Pending:
                        if (now > _deadline)
                            Halt("Ariadne did not answer about its mesh");
                        return WalkCommand.None;

                    default:
                        return Try(WalkCommand.AskReach, now, "Ariadne could not be asked about its mesh");
                }

            case WalkPhase.Starting:
                if (obs.NavBusy)
                {
                    Enter(WalkPhase.Walking, now, WalkDeadline, "Walking");
                    return WalkCommand.None;
                }

                return Try(WalkCommand.Move, now, $"Ariadne did not start walking ({Said(obs)})");

            case WalkPhase.Walking:
                if (obs.NavBusy)
                {
                    if (now > _deadline)
                    {
                        Halt("The walk took too long");
                        return WalkCommand.Stop;
                    }

                    Status = $"Walking — {Flat(position, Goal):0.0} y to go";
                    return WalkCommand.None;
                }

                // The navigation has let go: where the character stands is the verdict, not what it said.
                var shortBy = Flat(position, Goal);
                if (shortBy > ArriveWithin)
                    return HaltNone($"The walk ended {shortBy:0.0} y short of the place ({Said(obs)})");

                Enter(WalkPhase.Facing, now, FaceDeadline, "At the place — turning to the water");
                return WalkCommand.None;

            default: // Facing
                if (MathF.Abs(Turn(obs.Rotation, Facing)) <= FacingWithin)
                {
                    Phase = WalkPhase.Done;
                    Status = $"Done — standing {Flat(position, Target):0.0} y from the place chosen, facing the water.";
                    return WalkCommand.None;
                }

                return Try(WalkCommand.Face, now, "The character would not turn to the water");
        }
    }

    /// <summary>The same command again only after <see cref="Pace"/>, at most <see cref="MaxAttempts"/> times, never past the deadline.</summary>
    private WalkCommand Try(WalkCommand command, DateTime now, string failure)
    {
        if (now < _nextCommandAt)
            return WalkCommand.None;

        if (_attempts >= MaxAttempts || now > _deadline)
            return HaltNone(failure);

        _attempts++;
        _nextCommandAt = now + Pace;
        return command;
    }

    private void Enter(WalkPhase phase, DateTime now, TimeSpan deadline, string status)
    {
        Phase = phase;
        Status = status;
        _deadline = now + deadline;
        _nextCommandAt = now;
        _attempts = 0;
    }

    private WalkCommand HaltNone(string why)
    {
        Halt(why);
        return WalkCommand.None;
    }

    private void Halt(string why)
    {
        Phase = WalkPhase.Halted;
        Status = $"Halted — {why}.";
    }

    private static string Said(WalkObservation obs) => obs.NavResult.Length == 0 ? "Ariadne said nothing" : $"Ariadne: {obs.NavResult}";

    private static float Flat(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>The turn from one rotation to another, the short way round: -pi to pi.</summary>
    private static float Turn(float from, float to)
    {
        var turn = (to - from) % MathF.Tau;
        if (turn > MathF.PI) turn -= MathF.Tau;
        if (turn < -MathF.PI) turn += MathF.Tau;
        return turn;
    }
}
