using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Selene.Features.Characters;
using Selene.Features.Missions;
using Selene.Features.Schedule;
using Selene.Services;
using Selene.Services.Game;
using Selene.Windows;
using Selene.Windows.Dock;

namespace Selene;

/// <summary>
/// Composition root. Feature logic lives in Features/ (pure, unit-tested, no Dalamud types); game
/// interop lives in Services/Game/ (thin fail-open adapters) - the same split as Charon and Hephaestus.
/// </summary>
public sealed class SelenePlugin : IDalamudPlugin
{
    public const string PluginVersion = "0.1.0";

    private const string CommandName = "/selene";

    /// <summary>How often the schedule is asked. It plans a day or two each time: not a per-frame job.</summary>
    private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(1);

    /// <summary>How often the character files are read again, so another client's character turns up.</summary>
    private static readonly TimeSpan RosterReloadEvery = TimeSpan.FromSeconds(30);

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;
    private readonly IFramework _framework;
    private readonly WindowSystem _windowSystem = new("Selene");
    private readonly MainWindow _mainWindow;
    private readonly EdgeDockWindow _dock;
    private readonly DockLayoutShare _dockLayout;
    private readonly SeleneConfig _config;
    private readonly CharacterReader _character;
    private readonly CharacterRoster _roster;
    private readonly MissionCatalog _catalog;
    private readonly MissionReader _missionReader = new();
    private readonly MissionWindows _missionWindows;
    private readonly MissionRun _run = new();
    private readonly HephaestusClient _hephaestus;

    /// <summary>Run and Stop are pressed in the window; the tick acts on them, with a fresh look at the game.</summary>
    private bool _runRequested;
    private bool _stopRequested;

    /// <summary>The same for every character until the settings are per character (they are not in the config yet).</summary>
    private readonly ScheduleSettings _schedule = new();

    private DateTime _nextTick;
    private DateTime _nextRosterReload;

    public SelenePlugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework,
        IObjectTable objectTable,
        IDataManager data,
        IGameGui gameGui,
        IPluginLog log)
    {
        _pluginInterface = pluginInterface;
        _commandManager = commandManager;
        _framework = framework;

        _config = pluginInterface.GetPluginConfig() as SeleneConfig ?? new SeleneConfig();
        if (_config.Migrate())
            SaveConfig();

        _character = new CharacterReader(objectTable);
        _roster = new CharacterRoster(pluginInterface.GetPluginConfigDirectory());

        _catalog = new MissionCatalog(data);
        _hephaestus = new HephaestusClient(pluginInterface);
        _missionWindows = new MissionWindows(gameGui);

        _mainWindow = new MainWindow(_config, SaveConfig, PickMission,
            startRun: () => { _runRequested = true; _nextTick = default; },
            stopRun: () => { _stopRequested = true; _nextTick = default; });

        // The edge dock, as in Daedalus: one "Selene" tab, shown while a character is logged in, popping the
        // evening out beside it. The layout is shared with Daedalus's and Charon's tabs so none cover each
        // other; tabs wanting the same spot go Daedalus (10), Charon (20), Selene (30).
        _dockLayout = new DockLayoutShare(pluginInterface, "Selene", order: 30);
        _dock = new EdgeDockWindow(
            [new EdgeDockWindow.Tab("main", "Selene", () => _mainWindow.View is { LoggedIn: true }, _mainWindow.DrawGlance, 320f)],
            _ => (_config.EdgeDockEdge, _config.EdgeDockAlong),
            (_, edge, along) =>
            {
                _config.EdgeDockEdge = edge;
                _config.EdgeDockAlong = along;
                SaveConfig();
            },
            _dockLayout);
        _mainWindow.DockStatus = () => _config.EdgeDockEnabled ? _dock.Status : "off — switched off in Settings";

        _windowSystem.AddWindow(_mainWindow);
        _windowSystem.AddWindow(_dock);
        _commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Selene window.",
        });

        _pluginInterface.UiBuilder.Draw += Draw;
        _pluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
        _pluginInterface.UiBuilder.OpenConfigUi += ToggleMainWindow;
        _framework.Update += OnFrameworkUpdate;

        log.Information("Selene v{0} loaded", PluginVersion);
    }

    public void Dispose()
    {
        // A run that is unloaded mid-craft (a plugin reload, a Debug build) would leave Hephaestus making
        // items with nobody watching the clock: have it finish the one in hand and stop.
        if (_run.Active)
            _hephaestus.RequestStopAfterRun();

        _framework.Update -= OnFrameworkUpdate;
        _pluginInterface.UiBuilder.Draw -= Draw;
        _pluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        _pluginInterface.UiBuilder.OpenConfigUi -= ToggleMainWindow;

        _commandManager.RemoveHandler(CommandName);
        _windowSystem.RemoveAllWindows();
        _dockLayout.Dispose();
    }

    private void Draw()
    {
        _dock.IsOpen = _config.EdgeDockEnabled && _dock.AnyTab();
        _windowSystem.Draw();
    }

    private void SaveConfig() => _pluginInterface.SavePluginConfig(_config);

    /// <summary>
    /// Once a second, on the framework thread: who is logged in, which characters this PC knows, and
    /// what the schedule says of each. The window paints the result; nothing acts on it yet.
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.Now;
        if (now < _nextTick)
            return;
        _nextTick = now + TickEvery;

        if (now >= _nextRosterReload)
        {
            _roster.Reload();
            _nextRosterReload = now + RosterReloadEvery;
        }

        var contentId = _character.ContentId;
        _roster.Remember(contentId, _character.Name);

        // Missions: READ ONLY. What the game says of the job the character is on, the picks, and Hephaestus.
        var job = _character.ClassJobId;
        var missionState = _missionReader.Read(job);
        var picks = contentId != 0 && _config.Characters.TryGetValue(contentId, out var settings)
            ? settings.PickedMissions
            : (IReadOnlyCollection<uint>)Array.Empty<uint>();
        var hephaestus = _hephaestus.Read();

        // The attended run: Selene accepts a ticked mission (or takes over one accepted by hand), opens the
        // log, crafts, watches the clock and reports.
        var board = _missionWindows.ReadBoard();
        var observation = new RunObservation(
            missionState, hephaestus, _missionWindows.LogOpen, board.Open, board.Missions, board.Selected,
            _missionWindows.ConfirmingMission());
        if (_stopRequested)
            Execute(_run.Stop(observation));
        else if (_runRequested)
            _run.Start(observation, picks, now);
        _runRequested = _stopRequested = false;
        Execute(_run.Tick(observation, now));

        _mainWindow.Missions = new MissionView(
            job, _catalog.JobName(job), missionState, MissionBoard.Build(_catalog.Missions, missionState, picks), hephaestus,
            _run.Active, _run.Status);

        var rows = ScheduleBoard.Build(_roster.Names, contentId, _schedule, now);
        var mine = rows.FirstOrDefault(row => row.IsThisCharacter);
        _mainWindow.View = new ScheduleView(now, contentId != 0, _schedule, rows, new[]
        {
            ("Schedule", mine?.Decision.Status ?? "Idle — no character to schedule."),
            ("Character", _character.Status),
            ("Roster", _roster.Status),
            ("Mission sheets", _catalog.Status),
            ("Missions", _missionReader.Status),
            ("Hephaestus", _hephaestus.Status),
            ("Mission run", _run.Status),
            ("Asked of Heph.", _hephaestus.LastAction),
            ("Windows", _missionWindows.Status),
            ("Confirmation", _missionWindows.ConfirmDetail),
        });
    }

    /// <summary>The one thing the run asked for this tick. What came of it is read off the game next tick.</summary>
    private void Execute(RunCommand command)
    {
        switch (command)
        {
            case RunCommand.OpenBoard: _missionWindows.OpenBoard(); break;
            case RunCommand.ShowMasteryTab: _missionWindows.ShowMasteryTab(); break;
            case RunCommand.SelectMission: _missionWindows.Select(_run.MissionId); break;
            case RunCommand.InitiateMission: _missionWindows.Initiate(_run.MissionId); break;
            case RunCommand.ConfirmMission: _missionWindows.ConfirmMission(_run.MissionId); break;
            case RunCommand.OpenLog: _missionWindows.OpenLog(); break;
            case RunCommand.SelectRecipe: _missionWindows.SelectRecipe(); break;
            case RunCommand.StartCraft: _hephaestus.CraftSelected(MissionRun.CraftAmount); break;
            case RunCommand.StopAfterItem: _hephaestus.RequestStopAfterRun(); break;
            case RunCommand.Report: _missionWindows.Report(); break;
        }
    }

    /// <summary>The user ticked or unticked a mission on the Missions page: kept for the logged-in character.</summary>
    private void PickMission(uint missionId, bool picked)
    {
        var contentId = _character.ContentId;
        if (contentId == 0)
            return;

        var picks = _config.For(contentId).PickedMissions;
        picks.Remove(missionId);
        if (picked)
            picks.Add(missionId);
        SaveConfig();
        _nextTick = default; // show it at once
    }

    private void OnCommand(string command, string arguments) => ToggleMainWindow();

    private void ToggleMainWindow() => _mainWindow.IsOpen = !_mainWindow.IsOpen;
}
