using System;

namespace Selene.Features.Travel;

public enum TripPhase
{
    Idle,

    /// <summary>Lifestream has been asked; waiting to see it busy (or the zone change).</summary>
    Asking,

    /// <summary>Lifestream is on the way: the teleport to Bestways Burrow, the aetheryte, the star's entry.</summary>
    Travelling,

    Done,

    /// <summary>Stopped with the reason in <see cref="StarTrip.Status"/>; a person takes it from here.</summary>
    Halted,
}

public enum TripCommand
{
    None,

    /// <summary>Hand Lifestream the trip to <see cref="StarTrip.StarIndex"/>.</summary>
    Go,

    /// <summary><c>Lifestream.Abort</c>.</summary>
    Abort,
}

/// <param name="TerritoryId">The zone the character is in; 0 while between zones.</param>
/// <param name="InWorld">There is a character in the world (false during a zone change).</param>
/// <param name="OnHomeWorld">The character is on its Home World - the game lets nobody to a star from another.</param>
/// <param name="InMission">A Cosmic mission is in progress.</param>
/// <param name="Working">Selene is running a mission or a chain of them.</param>
public readonly record struct TripObservation(
    uint TerritoryId, bool InWorld, bool OnHomeWorld, bool LifestreamAvailable, bool LifestreamBusy, bool InMission,
    bool Working = false);

/// <summary>
/// The trip to a star (the user, 2026-11-14: "work the teleport to the moon, need to pick which moon to go
/// too also"). Lifestream does the travelling - the teleport to Bestways Burrow and the aetheryte's
/// "Travel to ..." entry for the star; this only asks, watches and judges. The verdict is the ZONE the
/// character is in, never what a call returned.
///
/// Pure: an observation in, at most one command out. A pace, a deadline and a cap a step; when one runs out
/// it HALTS and says why.
/// </summary>
public sealed class StarTrip
{
    public const int MaxAttempts = 2;

    /// <summary>The aetheryte whose own menu has the stars (Aetheryte row 175, Mare Lamentorum).</summary>
    public const uint BestwaysBurrowAetheryte = 175;

    /// <summary>
    /// What Lifestream is handed: its own "cosmic" alias with ONE star named - teleport to Bestways Burrow
    /// (skipped within 35 y of the aetheryte), interact with the aetheryte, take " Travel to (star)." (row
    /// <paramref name="starIndex"/> + 1 of <c>transport/AetheryteBestwaysBurrow</c>, which Lifestream reads
    /// in the client's language). 0 = Sinus Ardorum ... 3 = Auxesia.
    /// </summary>
    public static string LifestreamAlias(int starIndex)
        => "{\"ExportedName\":\"Selene star\",\"Enabled\":false,\"Commands\":["
           + $"{{\"Kind\":0,\"Aetheryte\":{BestwaysBurrowAetheryte},\"SkipTeleport\":35.0}},"
           + $"{{\"Kind\":6,\"DataID\":{BestwaysBurrowAetheryte},\"InteractDistance\":10.0}},"
           + $"{{\"Kind\":9,\"SelectOption\":[\"<QuestDialogueText:transport/AetheryteBestwaysBurrow:{starIndex + 1}:Value>\"],\"StopOnScreenFade\":true}}"
           + "]}";

    public static readonly TimeSpan Pace = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan AskDeadline = TimeSpan.FromSeconds(15);

    /// <summary>A teleport's cast, two zone changes and a menu between them.</summary>
    public static readonly TimeSpan TripDeadline = TimeSpan.FromSeconds(150);

    /// <summary>Lifestream idle this long, and the character still somewhere else: it gave up.</summary>
    public static readonly TimeSpan IdleSettle = TimeSpan.FromSeconds(12);

    private DateTime _deadline;
    private DateTime _nextCommandAt;
    private DateTime? _idleSince;
    private int _attempts;

    public TripPhase Phase { get; private set; } = TripPhase.Idle;

    public string Status { get; private set; } = "Idle — no trip asked for.";

    /// <summary>The star's zone.</summary>
    public uint Territory { get; private set; }

    /// <summary>The star's place among the four, 0 = Sinus Ardorum: the aetheryte's entry is row index + 1.</summary>
    public int StarIndex { get; private set; }

    public string StarName { get; private set; } = string.Empty;

    public bool Active => Phase is TripPhase.Asking or TripPhase.Travelling;

    /// <summary>Begin, if a trip can be made. False = nothing asked, and <see cref="Status"/> says why.</summary>
    public bool Start(TripObservation obs, uint territory, int starIndex, string starName, DateTime now)
    {
        if (Active)
            return false;

        var refusal =
            territory == 0 || starIndex < 0 ? "no star is picked"
            : !obs.InWorld ? "there is no character in the world"
            : obs.TerritoryId == territory ? $"the character is on {starName} already"
            : obs.Working ? "a mission run or a chain is going: stop it first"
            : obs.InMission ? "a mission is in progress: report it first"
            : !obs.OnHomeWorld ? "the character is not on its Home World: the game lets nobody to a star from another world"
            : !obs.LifestreamAvailable ? "Lifestream is not loaded (it does the travelling)"
            : obs.LifestreamBusy ? "Lifestream is busy"
            : null;

        if (refusal != null)
        {
            Phase = TripPhase.Idle;
            Status = $"Not started — {refusal}.";
            return false;
        }

        Territory = territory;
        StarIndex = starIndex;
        StarName = starName;
        Enter(TripPhase.Asking, now, AskDeadline, $"Asking Lifestream for the trip to {starName}");
        return true;
    }

    /// <summary>The user's Stop: Lifestream is told to drop what it is doing.</summary>
    public TripCommand Stop(TripObservation obs)
    {
        if (!Active)
            return TripCommand.None;

        Halt("Stopped by you");
        return obs.LifestreamBusy ? TripCommand.Abort : TripCommand.None;
    }

    public TripCommand Tick(TripObservation obs, DateTime now)
    {
        if (!Active)
            return TripCommand.None;

        var arrived = obs.InWorld && obs.TerritoryId == Territory;
        if (arrived && !obs.LifestreamBusy)
        {
            Phase = TripPhase.Done;
            Status = $"Done — on {StarName}.";
            return TripCommand.None;
        }

        if (Phase == TripPhase.Asking)
        {
            if (obs.LifestreamBusy || !obs.InWorld)
            {
                Enter(TripPhase.Travelling, now, TripDeadline, $"On the way to {StarName}");
                return TripCommand.None;
            }

            if (now < _nextCommandAt)
                return TripCommand.None;

            if (_attempts >= MaxAttempts || now > _deadline)
            {
                Halt("Lifestream did not take the trip");
                return TripCommand.None;
            }

            _attempts++;
            _nextCommandAt = now + Pace;
            return TripCommand.Go;
        }

        // Travelling.
        if (now > _deadline)
        {
            Halt($"The trip to {StarName} took too long");
            return obs.LifestreamBusy ? TripCommand.Abort : TripCommand.None;
        }

        if (obs.LifestreamBusy || !obs.InWorld)
        {
            _idleSince = null;
            return TripCommand.None;
        }

        // Lifestream is idle and the character is somewhere else: a moment for a zone to finish loading, then that is the verdict.
        _idleSince ??= now;
        if (now - _idleSince.Value > IdleSettle)
            Halt($"Lifestream stopped and the character is not on {StarName} — the star may not be open to this character, or the aetheryte's menu did not come up");
        return TripCommand.None;
    }

    private void Enter(TripPhase phase, DateTime now, TimeSpan deadline, string status)
    {
        Phase = phase;
        Status = status;
        _deadline = now + deadline;
        _nextCommandAt = now;
        _idleSince = null;
        _attempts = 0;
    }

    private void Halt(string why)
    {
        Phase = TripPhase.Halted;
        Status = $"Halted — {why}.";
    }
}
