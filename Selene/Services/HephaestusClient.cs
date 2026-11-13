using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Selene.Features.Missions;

namespace Selene.Services;

/// <summary>
/// What Selene asks of Hephaestus, the plugin that makes the crafts: is it there, is it busy, what does it
/// say of itself and of the gear - and, for a mission run, <c>CraftSelected</c> and
/// <c>RequestStopAfterRun</c>. <c>ChangeJob</c> and <c>Stop</c> come with the code that calls them.
/// Fail-open: an absent Hephaestus is <see cref="HephaestusStatus.Absent"/>, never a throw. Contract:
/// the header of <c>D:\Dev\Hephaestus\Hephaestus\Ipc\HephaestusIpc.cs</c>.
/// </summary>
public sealed class HephaestusClient
{
    private readonly ICallGateSubscriber<string> _version;
    private readonly ICallGateSubscriber<bool> _isBusy;
    private readonly ICallGateSubscriber<string> _queueStatusJson;
    private readonly ICallGateSubscriber<int, bool> _craftSelected;
    private readonly ICallGateSubscriber<bool> _requestStopAfterRun;

    public HephaestusClient(IDalamudPluginInterface pluginInterface)
    {
        _version = pluginInterface.GetIpcSubscriber<string>("Hephaestus.GetVersion");
        _isBusy = pluginInterface.GetIpcSubscriber<bool>("Hephaestus.IsBusy");
        _queueStatusJson = pluginInterface.GetIpcSubscriber<string>("Hephaestus.GetQueueStatusJson");
        _craftSelected = pluginInterface.GetIpcSubscriber<int, bool>("Hephaestus.CraftSelected");
        _requestStopAfterRun = pluginInterface.GetIpcSubscriber<bool>("Hephaestus.RequestStopAfterRun");
    }

    /// <summary>What it answered last, or why it did not.</summary>
    public string Status { get; private set; } = "Idle — not asked yet.";

    /// <summary>The last thing Selene ASKED Hephaestus to do, and what came back.</summary>
    public string LastAction { get; private set; } = "Idle — nothing asked.";

    /// <summary>
    /// <c>Hephaestus.CraftSelected</c>: make <paramref name="amount"/> of the recipe selected in the open
    /// (Cosmic) crafting log. The answer is only a hint - the caller reads <c>IsBusy</c> for the verdict.
    /// </summary>
    public void CraftSelected(int amount)
    {
        try
        {
            LastAction = _craftSelected.InvokeFunc(amount)
                ? $"CraftSelected({amount}): began"
                : $"CraftSelected({amount}): refused — no log open, nothing selected, nothing can be made, or Hephaestus busy";
        }
        catch
        {
            LastAction = "CraftSelected: this Hephaestus has no such gate (an older build)";
        }
    }

    /// <summary><c>Hephaestus.RequestStopAfterRun</c>: finish the synthesis in hand, start nothing more, leave the stance.</summary>
    public void RequestStopAfterRun()
    {
        try
        {
            LastAction = _requestStopAfterRun.InvokeFunc()
                ? "RequestStopAfterRun: it will stop after the item in hand"
                : "RequestStopAfterRun: refused — Hephaestus was idle";
        }
        catch
        {
            LastAction = "RequestStopAfterRun: this Hephaestus has no such gate";
        }
    }

    public HephaestusStatus Read()
    {
        string version;
        try
        {
            version = _version.InvokeFunc();
        }
        catch
        {
            // The gate does not exist: Hephaestus is not loaded.
            Status = "Idle — Hephaestus is not loaded.";
            return HephaestusStatus.Absent;
        }

        try
        {
            var status = HephaestusStatus.Parse(_queueStatusJson.InvokeFunc(), _isBusy.InvokeFunc());
            Status = $"Hephaestus {version}: {(status.Busy ? "busy" : "idle")}"
                     + (status.ReportsGear ? $" · gear {status.GearConditionPercent}% (gate {status.GearGatePercent}%)" : " · does not report its gear gate (an older build)")
                     + (status.RunHalt != "None" ? $" · halted: {status.RunHalt}" : string.Empty)
                     + (status.Status.Length > 0 ? $" · \"{status.Status}\"" : string.Empty);
            return status;
        }
        catch
        {
            Status = $"Hephaestus {version} is loaded but did not answer.";
            return HephaestusStatus.Absent with { Available = true, Status = "Hephaestus did not answer." };
        }
    }
}
