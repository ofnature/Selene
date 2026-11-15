using System;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Selene.Features.Fishing;

namespace Selene.Services;

/// <summary>
/// Ariadne, the suite's navigation - the one thing that moves the character inside a zone - as far as
/// Selene's walk test needs it. The gates and their meaning are read off Demeter's client of the same
/// plugin (<c>D:\Dev\Demeter\Demeter\Services\Ipc\AriadneIpc.cs</c>); NONE has been called from Selene in
/// game. Fail-open: a missing Ariadne is "not ready", never an exception. Ariadne's own session switches
/// (prefer flying, use aetherytes) are never touched: they are shared by every plugin that uses it.
/// </summary>
public sealed class AriadneClient
{
    private readonly ICallGateSubscriber<bool> _isConnected;
    private readonly ICallGateSubscriber<int> _zoneStatus;
    private readonly ICallGateSubscriber<Vector3, bool, bool> _moveTo;
    private readonly ICallGateSubscriber<bool> _pathfindInProgress;
    private readonly ICallGateSubscriber<bool> _isRunning;
    private readonly ICallGateSubscriber<string> _lastResult;
    private readonly ICallGateSubscriber<object> _stop;
    private readonly ICallGateSubscriber<Vector3, float, float, Task<Vector3?>> _nearestReachable;

    private Task<Vector3?>? _reach;

    public AriadneClient(IDalamudPluginInterface pluginInterface)
    {
        _isConnected = pluginInterface.GetIpcSubscriber<bool>("Ariadne.IsConnected");
        _zoneStatus = pluginInterface.GetIpcSubscriber<int>("Ariadne.ZoneStatus");
        _moveTo = pluginInterface.GetIpcSubscriber<Vector3, bool, bool>("Ariadne.SimpleMove.PathfindAndMoveTo");
        _pathfindInProgress = pluginInterface.GetIpcSubscriber<bool>("Ariadne.SimpleMove.PathfindInProgress");
        _isRunning = pluginInterface.GetIpcSubscriber<bool>("Ariadne.Path.IsRunning");
        _lastResult = pluginInterface.GetIpcSubscriber<string>("Ariadne.SimpleMove.LastResult");
        _stop = pluginInterface.GetIpcSubscriber<object>("Ariadne.Path.Stop");
        _nearestReachable = pluginInterface.GetIpcSubscriber<Vector3, float, float, Task<Vector3?>>("Ariadne.Query.Mesh.NearestPointReachable");
    }

    /// <summary>The last thing Selene ASKED Ariadne to do, and what came back.</summary>
    public string LastAction { get; private set; } = "Idle — nothing asked.";

    /// <summary>Connected, with this zone's mesh in hand (zone status 2 or 3).</summary>
    public bool Ready => Try(() => _isConnected.InvokeFunc() && _zoneStatus.InvokeFunc() is 2 or 3, false);

    /// <summary>A move is under way: pathfinding or following the path.</summary>
    public bool Busy => Try(() => _pathfindInProgress.InvokeFunc() || _isRunning.InvokeFunc(), false);

    /// <summary>How Ariadne says the last move ended ("goal reached", "stuck (N y short)", "no path (...)"...).</summary>
    public string LastResult => Try(() => _lastResult.InvokeFunc() ?? string.Empty, string.Empty);

    /// <summary>What it is, in a line, for the Debug page.</summary>
    public string Status
    {
        get
        {
            var connected = Try(() => (bool?)_isConnected.InvokeFunc(), null);
            if (connected == null)
                return "Idle — Ariadne is not loaded.";

            var zone = Try(() => _zoneStatus.InvokeFunc(), -1);
            return $"{(connected.Value ? "connected" : "not connected")} · zone status {zone} · {(Busy ? "moving" : "idle")} · last: \"{LastResult}\"";
        }
    }

    /// <summary>Ask for the nearest point the mesh reaches near a place. The answer comes off the frame: read <see cref="Reach"/>.</summary>
    public void AskReach(Vector3 near, float within)
    {
        try
        {
            _reach = _nearestReachable.InvokeFunc(near, within, within);
            LastAction = $"NearestPointReachable(({near.X:0.0}, {near.Y:0.0}, {near.Z:0.0}), {within:0}): asked";
        }
        catch
        {
            _reach = Task.FromResult<Vector3?>(null);
            LastAction = "NearestPointReachable: this Ariadne has no such gate, or is not loaded";
        }
    }

    /// <summary>Forget the last question, so the next walk asks its own.</summary>
    public void ForgetReach() => _reach = null;

    public ReachState Reach => _reach switch
    {
        null => ReachState.NotAsked,
        { IsCompleted: false } => ReachState.Pending,
        { IsCompletedSuccessfully: true, Result: not null } => ReachState.Found,
        _ => ReachState.None,
    };

    public Vector3 ReachPoint => _reach is { IsCompletedSuccessfully: true, Result: { } point } ? point : default;

    /// <summary>Pathfind and walk there, on foot. The answer is only a hint: the caller reads <see cref="Busy"/>.</summary>
    public void MoveTo(Vector3 destination)
    {
        try
        {
            LastAction = _moveTo.InvokeFunc(destination, false)
                ? $"PathfindAndMoveTo(({destination.X:0.0}, {destination.Y:0.0}, {destination.Z:0.0})): began"
                : "PathfindAndMoveTo: refused — already pathfinding, or no character";
        }
        catch
        {
            LastAction = "PathfindAndMoveTo: Ariadne is not loaded";
        }
    }

    public void Stop()
    {
        try
        {
            _stop.InvokeAction();
            LastAction = "Path.Stop: asked";
        }
        catch
        {
            LastAction = "Path.Stop: Ariadne is not loaded";
        }
    }

    private static T Try<T>(Func<T> call, T fallback)
    {
        try
        {
            return call();
        }
        catch
        {
            return fallback;
        }
    }
}
