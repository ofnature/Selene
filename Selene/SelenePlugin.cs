using System;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Selene.Features.Characters;
using Selene.Features.Schedule;
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

    /// <summary>The same for every character until the settings are per character (they are not in the config yet).</summary>
    private readonly ScheduleSettings _schedule = new();

    private DateTime _nextTick;
    private DateTime _nextRosterReload;

    public SelenePlugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework,
        IObjectTable objectTable,
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

        _mainWindow = new MainWindow(_config, SaveConfig);

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

        var rows = ScheduleBoard.Build(_roster.Names, contentId, _schedule, now);
        var mine = rows.FirstOrDefault(row => row.IsThisCharacter);
        _mainWindow.View = new ScheduleView(now, contentId != 0, _schedule, rows, new[]
        {
            ("Schedule", mine?.Decision.Status ?? "Idle — no character to schedule."),
            ("Character", _character.Status),
            ("Roster", _roster.Status),
        });
    }

    private void OnCommand(string command, string arguments) => ToggleMainWindow();

    private void ToggleMainWindow() => _mainWindow.IsOpen = !_mainWindow.IsOpen;
}
