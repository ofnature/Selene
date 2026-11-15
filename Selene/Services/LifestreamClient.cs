using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Selene.Services;

/// <summary>
/// What Selene asks of Lifestream, the plugin that travels: is it there, is it busy, and the trip to a
/// Cosmic star. Fail-open: an absent Lifestream is "not available", never a throw.
///
/// THE TRIP is Lifestream's own "cosmic" alias (its <c>StaticAlias.CosmicExploration</c>, read from the
/// decompiled 2.6.0.13) with ONE star named instead of four: teleport to the Bestways Burrow aetheryte
/// (175; skipped within 35 y of it), interact with the aetheryte, pick " Travel to (star)." - the row of
/// <c>transport/AetheryteBestwaysBurrow</c>, which Lifestream reads in the client's language. Handed over
/// through <c>Lifestream.EnqueueCustomAliasFromString</c>. Unlike <c>/li cosmic</c> it does NOT go to the
/// Home World first: the caller refuses the trip from another world.
/// NOT SEEN IN GAME when written (2026-11-14): that a hand-written alias is taken, and what the menu shows
/// for a star the character has not opened.
/// </summary>
public sealed class LifestreamClient
{
    private readonly ICallGateSubscriber<bool> _isBusy;
    private readonly ICallGateSubscriber<string, bool, int?, int?, object> _enqueueAlias;
    private readonly ICallGateSubscriber<object> _abort;

    public LifestreamClient(IDalamudPluginInterface pluginInterface)
    {
        _isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        _enqueueAlias = pluginInterface.GetIpcSubscriber<string, bool, int?, int?, object>("Lifestream.EnqueueCustomAliasFromString");
        _abort = pluginInterface.GetIpcSubscriber<object>("Lifestream.Abort");
    }

    /// <summary>What it answered last, or why it did not.</summary>
    public string Status { get; private set; } = "Idle — not asked yet.";

    /// <summary>The last thing Selene ASKED Lifestream to do.</summary>
    public string LastAction { get; private set; } = "Idle — nothing asked.";

    public bool Available { get; private set; }

    public bool Busy { get; private set; }

    /// <summary>Ask Lifestream whether it is there and busy; the answer is kept in <see cref="Available"/> and <see cref="Busy"/>.</summary>
    public void Read()
    {
        try
        {
            Busy = _isBusy.InvokeFunc();
            Available = true;
            Status = Busy ? "Lifestream: busy" : "Lifestream: idle";
        }
        catch
        {
            Available = Busy = false;
            Status = "Idle — Lifestream is not loaded.";
        }
    }

    /// <summary>The trip to a star. Whether it began shows in <see cref="Busy"/>; whether it arrived, in the zone.</summary>
    public void GoToStar(int starIndex, string starName)
    {
        try
        {
            _enqueueAlias.InvokeAction(Features.Travel.StarTrip.LifestreamAlias(starIndex), true, null, null);
            LastAction = $"The trip to {starName}: handed to Lifestream (Bestways Burrow, then the aetheryte's entry {starIndex + 1})";
        }
        catch (Exception ex)
        {
            LastAction = $"The trip to {starName}: Lifestream did not take it — {ex.Message}";
        }
    }

    public void Abort()
    {
        try
        {
            _abort.InvokeAction();
            LastAction = "Abort: Lifestream told to drop what it was doing";
        }
        catch
        {
            LastAction = "Abort: this Lifestream has no such gate";
        }
    }
}
