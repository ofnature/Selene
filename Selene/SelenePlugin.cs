using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Selene.Features.Characters;
using Selene.Features.Fishing;
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
    private readonly MissionRun _run;
    private readonly HephaestusClient _hephaestus;
    private readonly WaterProbe _water = new();
    private readonly NearbyPlayers _players;
    private readonly AethernetShards _shards;
    private readonly ShoreOverlay _shoreOverlay;
    private readonly AriadneClient _ariadne;
    private readonly StandWalk _walk = new();
    private readonly PayoutWatch _payoutWatch = new();
    private readonly PayoutLog _payouts;
    private readonly MissionChain _chain = new();

    /// <summary>"Start chain" / "Stop after this mission" were pressed on the Missions page; the tick acts on them.</summary>
    private bool _chainStartRequested;
    private bool _chainStopAfterRequested;

    /// <summary>The place the last look for water chose, and its facing: where the walk test goes.</summary>
    private (Vector3 At, float Facing)? _chosen;

    /// <summary>"Walk to the chosen spot" / "Stop walking" were pressed on the Debug page; the tick acts on them.</summary>
    private bool _walkRequested;
    private bool _walkStopRequested;

    // [SUPERSEDED 2026-11-14 by JobData / DataFor — kept for more than one job since the class picker]
    // /// <summary>The ids of the missions of the job the character is on: made once a job, asked about every tick.</summary>
    // private IReadOnlyCollection<uint> _jobMissions = Array.Empty<uint>();
    // private uint _jobMissionsFor = uint.MaxValue;
    //
    // /// <summary>The job's quest-unlocked actions, the quests to ask about, and the ones last read as done.</summary>
    // private IReadOnlyList<ActionNeed> _jobUnlocks = Array.Empty<ActionNeed>();
    // private IReadOnlyCollection<uint> _jobQuests = Array.Empty<uint>();

    /// <summary>What is asked of the game about one job, made once a job: its missions, its quest-unlocked actions, the quests behind both.</summary>
    private sealed record JobData(
        IReadOnlyCollection<uint> Missions, IReadOnlyList<ActionNeed> Unlocks, IReadOnlyCollection<uint> Quests,
        IReadOnlyCollection<uint> Functions);

    /// <summary>The game's own answer for the unlock functions of the character's OWN job's missions: what Run goes by first.</summary>
    private IReadOnlyDictionary<uint, bool>? _functions;

    private readonly Dictionary<uint, JobData> _jobData = new();

    /// <summary>The quests of the character's OWN job last read as done: what Run goes by.</summary>
    private IReadOnlyCollection<uint>? _questsDone;

    /// <summary>The class picked in the sidebar for the Missions page; 0 = the one the character is on.</summary>
    private uint _viewJob;

    /// <summary>"Change to ..." was pressed in the sidebar: the job asked for; 0 = nothing asked.</summary>
    private uint _changeJobRequested;
    private string? _classNote;

    /// <summary>The eleven Cosmic jobs, Carpenter to Fisher, for the sidebar's picker.</summary>
    private IReadOnlyList<(uint JobId, string Name)> _classes = Array.Empty<(uint, string)>();

    /// <summary>Run and Stop are pressed in the window; the tick acts on them, with a fresh look at the game.</summary>
    private bool _runRequested;
    private bool _stopRequested;

    /// <summary>"Look for water here" was pressed on the Debug page; the tick does the looking.</summary>
    private bool _waterRequested;
    private string _shore = "Idle — press \"Look for water here\" below.";

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
        _payouts = new PayoutLog(pluginInterface.GetPluginConfigDirectory());

        _catalog = new MissionCatalog(data);
        _hephaestus = new HephaestusClient(pluginInterface);
        _missionWindows = new MissionWindows(gameGui);
        _players = new NearbyPlayers(objectTable);
        _shards = new AethernetShards(data);

        _mainWindow = new MainWindow(_config, SaveConfig, PickMission,
            startRun: () => { _runRequested = true; _nextTick = default; },
            stopRun: () => { _stopRequested = true; _nextTick = default; });
        _shoreOverlay = new ShoreOverlay(gameGui);
        _mainWindow.LookForWater = () => { _waterRequested = true; _nextTick = default; };
        _mainWindow.HideWaterMarks = () => _shoreOverlay.Marks = null;
        _ariadne = new AriadneClient(pluginInterface);
        _mainWindow.WalkToSpot = () => { _walkRequested = true; _nextTick = default; };
        _mainWindow.StopWalk = () => { _walkStopRequested = true; _nextTick = default; };
        _classes = Enumerable.Range((int)MissionInfo.FirstCrafter, 11).Select(id => ((uint)id, _catalog.JobName((uint)id))).ToList();
        _mainWindow.SelectClass = picked => { _viewJob = picked; _classNote = null; _nextTick = default; };
        _mainWindow.ChangeClass = wanted => { _changeJobRequested = wanted; _nextTick = default; };
        _mainWindow.StartChain = () => { _chainStartRequested = true; _nextTick = default; };
        _mainWindow.StopChainAfter = () => { _chainStopAfterRequested = true; _nextTick = default; };
        _mainWindow.SetChain = SetChain;
        _mainWindow.ExportPayouts = () =>
        {
            _payouts.ExportCsv(id => _catalog.Missions.TryGetValue(id, out var mission) ? mission.Name.Trim() : $"Mission {id}");
            _nextTick = default;
        };
        _mainWindow.SetWholeTab = SetWholeTab;
        _mainWindow.SetReportGoal = SetReportGoal;
        _run = new MissionRun(PlanFor);

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

        // The walk test: a character is not left walking with nobody watching where it stops.
        if (_walk.Active)
            _ariadne.Stop();

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
        _shoreOverlay.Draw();
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
            _payouts.Reload(); // another client's measurements turn up the same way
            _nextRosterReload = now + RosterReloadEvery;
        }

        var contentId = _character.ContentId;
        _roster.Remember(contentId, _character.Name);

        // Missions: READ ONLY. What the game says of the job the character is on, the picks, and Hephaestus.
        var job = _character.ClassJobId;
        // [SUPERSEDED 2026-11-14 — the job's missions are handed over, to be asked about in the game's mission log]
        // var missionState = _missionReader.Read(job);
        // [SUPERSEDED 2026-11-14 by DataFor — the Missions page can show ANOTHER class than the one the character is
        //  on (the user: "maybe need a class picker on the left column with a select and change class"), so what
        //  is kept a job is kept for more than one]
        // if (job != _jobMissionsFor)
        // {
        //     var ofJob = _catalog.Missions.Values.Where(mission => mission.ClassJobId == job).ToList();
        //     _jobMissions = ofJob.Select(mission => mission.Id).ToList();
        //
        //     // The quests to ask about: what the job's missions want, and what unlocks the job's actions.
        //     _jobUnlocks = _catalog.JobUnlocks(job);
        //     _jobQuests = ofJob.SelectMany(mission => mission.Needs ?? Array.Empty<QuestNeed>()).Select(need => need.QuestId)
        //         .Concat(_jobUnlocks.Select(unlock => unlock.QuestId))
        //         .Distinct()
        //         .ToList();
        //     _jobMissionsFor = job;
        // }
        //
        // var missionState = _missionReader.Read(job, _jobMissions, _jobQuests);
        // _questsDone = missionState.QuestsDone;

        // The class the Missions page shows: the one picked in the sidebar, else the one the character is on.
        // The game keeps every Cosmic job's lists and score whatever job is on, so another class can be read.
        // (Read first, so the reader's Status - the Debug page's Missions line - is the character's own job's.)
        if (_viewJob == job)
            _viewJob = 0; // the character is on the picked class now: the page follows the character again
        var viewJob = _viewJob != 0 ? _viewJob : job;
        var viewData = DataFor(viewJob);
        var viewState = viewJob == job ? null : _missionReader.Read(viewJob, viewData.Missions, viewData.Quests, viewData.Functions);

        var mineData = DataFor(job);
        var missionState = _missionReader.Read(job, mineData.Missions, mineData.Quests, mineData.Functions);
        _questsDone = missionState.QuestsDone;
        _functions = missionState.Functions;
        viewState ??= missionState;

        // What a finished mission paid, whoever ran it: the class score before and after, kept a file a character.
        if (_payoutWatch.Observe(missionState, now, () => _missionReader.ItemCount(_catalog.MasteryItemId(job))) is { } paid)
            _payouts.Add(contentId, paid);
        var picks = contentId != 0 && _config.Characters.TryGetValue(contentId, out var settings)
            ? settings.PickedMissions
            : (IReadOnlyCollection<uint>)Array.Empty<uint>();
        var hephaestus = _hephaestus.Read();
        var character = contentId != 0 && _config.Characters.TryGetValue(contentId, out var kept) ? kept : null;
        var wholeTabs = character?.WholeTabs ?? new List<MissionCategory>();

        // What Run may take: the runnable missions ticked one by one, and whatever the game holds of a kind
        // whose tab is ticked as a whole.
        var runnable = picks.Where(Runnable)
            .Concat(missionState.Offered.Where(id => Runnable(id) && wholeTabs.Contains(_catalog.Missions[id].Category)))
            .Distinct()
            .ToList();

        if (_waterRequested)
        {
            _waterRequested = false;
            LookForWater(missionState.TerritoryId, contentId, now);
        }

        // The walk test (the Debug page's buttons): to the place the last look chose, through Ariadne.
        WalkObservation WalkSees() => new(
            _character.Position, _character.Rotation, _ariadne.Ready, _ariadne.Busy, _ariadne.LastResult, _ariadne.Reach, _ariadne.ReachPoint);
        if (_walkStopRequested)
        {
            ExecuteWalk(_walk.Stop(WalkSees()));
        }
        else if (_walkRequested && _chosen is { } spot && !_walk.Active)
        {
            _ariadne.ForgetReach(); // each walk asks the mesh about its own place
            _walk.Start(WalkSees(), spot.At, spot.Facing, now);
        }

        _walkRequested = _walkStopRequested = false;
        ExecuteWalk(_walk.Tick(WalkSees(), now));

        // The attended run: Selene accepts a ticked mission (or takes over one accepted by hand), opens the
        // log, crafts, watches the clock and reports.
        var board = _missionWindows.ReadBoard();
        var observation = new RunObservation(
            missionState, hephaestus, _missionWindows.LogOpen, board.Open, board.Missions, board.Selected,
            _missionWindows.ConfirmingMission());
        // The cosmic tool of the character's job (for the "level the tool" chain) and of the class shown.
        var tool = missionState.InZone ? _missionReader.ReadTool(job, missionState.TerritoryId) : null;
        var viewTool = viewJob == job ? tool : viewState.InZone ? _missionReader.ReadTool(viewJob, viewState.TerritoryId) : null;

        if (_chainStopAfterRequested)
            _chain.StopAfterThis();
        if (_chainStartRequested && !_chain.Active && !_stopRequested)
            _chain.Start(character?.ChainMode ?? ChainMode.Ticked, character?.ChainLimit ?? 0, now);
        _chainStartRequested = _chainStopAfterRequested = false;

        if (_stopRequested)
            _chain.Stop("Stopped by you.");
        if (_stopRequested)
            Execute(_run.Stop(observation));
        // [SUPERSEDED 2026-11-14 — every kind of mission can be ticked now; only Master crafting ones are run]
        // else if (_runRequested)
        //     _run.Start(observation, picks, now);
        else if (_runRequested)
            _run.Start(observation, runnable, now);
        _runRequested = _stopRequested = false;
        Execute(_run.Tick(observation, now));

        // The chain: when a mission is due, which one - and the run is asked to take exactly that one.
        var next = _chain.Tick(_run.Phase, _run.Status, () => ChooseNext(missionState, picks, wholeTabs, tool, contentId, now), now, contentId);
        if (next != 0 && !_run.Start(observation, new[] { next }, now))
            _chain.Stop($"Stopped — the mission was not started: {_run.Status}");

        // The sidebar's "Change to ..." was pressed: Hephaestus changes the job (the careful change its own queue
        // makes). Never under a run or with a mission in progress; what came of it shows as the character's job.
        if (_changeJobRequested != 0)
        {
            var wanted = _changeJobRequested;
            _changeJobRequested = 0;
            _classNote =
                _run.Active ? "Not changed — a mission run is going."
                : missionState.InMission ? "Not changed — a mission is in progress: report it first."
                : !hephaestus.Available ? "Not changed — Hephaestus is not loaded (it does the change)."
                : hephaestus.Busy ? "Not changed — Hephaestus is busy."
                : null;
            if (_classNote == null)
            {
                _hephaestus.ChangeJob(wanted);
                _classNote = $"Asked Hephaestus for {_catalog.JobName(wanted)}.";
            }
        }

        // [SUPERSEDED 2026-11-14 by the class picker — the page showed the character's own job only]
        // _mainWindow.Missions = new MissionView(
        //     job, _catalog.JobName(job), missionState, MissionBoard.Build(_catalog.Missions, missionState, picks, _payouts.Summaries), hephaestus,
        //     _run.Active, _run.Status,
        //     CanAccept: missionState.Offered.Any(runnable.Contains),
        //     ReportGoal: character?.ReportGoal ?? MissionRank.Gold,
        //     WholeTabs: wholeTabs,
        //     StarName: _catalog.StarName,
        //     JobUnlocks: _jobUnlocks,
        //     QuestsDone: _questsDone);
        _mainWindow.Missions = new MissionView(
            viewJob, _catalog.JobName(viewJob), viewState, MissionBoard.Build(_catalog.Missions, viewState, picks, _payouts.Summaries), hephaestus,
            _run.Active, _run.Status,
            CanAccept: viewJob == job && missionState.Offered.Any(runnable.Contains),
            ReportGoal: character?.ReportGoal ?? MissionRank.Gold,
            WholeTabs: wholeTabs,
            StarName: _catalog.StarName,
            JobUnlocks: viewData.Unlocks,
            QuestsDone: viewState.QuestsDone,
            CurrentJobId: job,
            Classes: _classes,
            ClassNote: _classNote,
            Tool: viewTool,
            ChainMode: character?.ChainMode ?? ChainMode.Ticked,
            ChainLimit: character?.ChainLimit ?? 0,
            ChainActive: _chain.Active,
            ChainStatus: _chain.Status);

        var rows = ScheduleBoard.Build(_roster.Names, contentId, _schedule, now);
        var mine = rows.FirstOrDefault(row => row.IsThisCharacter);
        _mainWindow.View = new ScheduleView(now, contentId != 0, _schedule, rows, new[]
        {
            ("Schedule", mine?.Decision.Status ?? "Idle — no character to schedule."),
            ("Character", _character.Status),
            ("Roster", _roster.Status),
            ("Mission sheets", _catalog.Status),
            ("Missions", _missionReader.Status),
            ("Quests", _missionReader.QuestDetail),
            ("Actions", _missionReader.ActionDetail(mineData.Unlocks)),
            ("Hephaestus", _hephaestus.Status),
            ("Mission run", _run.Status),
            ("Chain", _chain.Status),
            ("Asked of Heph.", _hephaestus.LastAction),
            ("Windows", _missionWindows.Status),
            ("Confirmation", _missionWindows.ConfirmDetail),
            ("Water", _water.Status),
            ("Shore", _shore),
            ("Shards", _shards.Status),
            ("Players", _players.Status),
            ("Payouts", $"{_payouts.Status} · {_payoutWatch.Status}"),
            ("Walk", _chosen == null && !_walk.Active && _walk.Phase == WalkPhase.Idle
                ? "Idle — look for water first: no place is chosen."
                : _walk.Status),
            ("Ariadne", _ariadne.Status),
            ("Asked of Ari.", _ariadne.LastAction),
        });
    }

    private JobData DataFor(uint job)
    {
        if (_jobData.TryGetValue(job, out var known))
            return known;

        var ofJob = _catalog.Missions.Values.Where(mission => mission.ClassJobId == job).ToList();
        var unlocks = _catalog.JobUnlocks(job);

        // The quests to ask about: what the job's missions want, and what unlocks the job's actions.
        var quests = ofJob.SelectMany(mission => mission.Needs ?? Array.Empty<QuestNeed>()).Select(need => need.QuestId)
            .Concat(unlocks.Select(unlock => unlock.QuestId))
            .Distinct()
            .ToList();
        var functions = ofJob.Select(mission => mission.UnlockFunction).Where(function => function != 0).Distinct().ToList();
        return _jobData[job] = new JobData(ofJob.Select(mission => mission.Id).ToList(), unlocks, quests, functions);
    }

    /// <summary>The one thing the walk test asked for this tick. What came of it is read off the game next tick.</summary>
    private void ExecuteWalk(WalkCommand command)
    {
        switch (command)
        {
            case WalkCommand.AskReach: _ariadne.AskReach(_walk.Target, StandWalk.MeshWithin); break;
            case WalkCommand.Move: _ariadne.MoveTo(_walk.Goal); break;
            case WalkCommand.Stop: _ariadne.Stop(); break;
            case WalkCommand.Face: _character.Face(_walk.Facing); break;
        }
    }

    /// <summary>
    /// A LOOK ONLY - nothing moves. Finds the water's edge around the character and says where Selene would
    /// stand to fish: nearest the aethernet shard a player would walk in from, clear of other players.
    /// What the Debug page shows of it is how the two material bits in <see cref="WaterProbe"/> get checked.
    /// </summary>
    private void LookForWater(uint territoryId, ulong contentId, DateTime now)
    {
        if (_character.Position is not { } me)
        {
            _shore = "No character in the world — nothing looked for.";
            return;
        }

        // [SUPERSEDED 2026-11-14 — the looks are kept for the markers drawn over the game]
        // var stands = ShoreFinder.Find(_water.Scan(me));
        var looks = _water.Scan(me);
        var stands = ShoreFinder.Find(looks);
        _shoreOverlay.Marks = null;
        _chosen = null;
        var shards = _shards.For(territoryId);
        var others = _players.Others();

        var arrival = shards.Count == 0 ? me : shards.OrderBy(shard => Vector3.DistanceSquared(shard, me)).First();
        // [SUPERSEDED 2026-11-14 by the two sentences further down]
        // var from = shards.Count == 0
        //     ? "where you stand (no aethernet shard known here)"
        //     : $"the nearest aethernet shard, {Vector3.Distance(arrival, me):0} y from you";

        // The minute stands in for "this mission": a second look in the same minute gives the same place.
        var choice = StandPicker.Pick(stands, arrival, others, contentId, (int)(now.Ticks / TimeSpan.TicksPerMinute));
        if (choice == null)
        {
            _shore = stands.Count == 0
                ? "No place to stand at the water within reach of here."
                : $"{stands.Count} place(s) at the water, every one with somebody on it.";
            return;
        }

        var at = choice.Stand.Position;
        var player = choice.NearestPlayer is { } gap ? $"nearest player {gap:0.0} y" : "nobody about";

        _chosen = (at, choice.Stand.Facing);

        // What was found, drawn over the game until the next look or "Hide the markers".
        _shoreOverlay.Marks = new ShoreMarks(
            at, choice.Stand.Facing,
            choice.Alternates.Select(alternate => alternate.Position).ToList(),
            stands.Select(stand => stand.Position).ToList(),
            looks.Where(look => look.IsWater).Select(look => new Vector3(look.X, look.WaterY!.Value, look.Z)).ToList());

        // [SUPERSEDED 2026-11-14 — the first look in game read "399 y from the nearest aethernet shard, 418 y from
        //  you": two distances run together]
        // _shore = $"{stands.Count} place(s) at the water. Would stand at ({at.X:0.0}, {at.Y:0.0}, {at.Z:0.0}) facing "
        //          + $"{choice.Stand.Facing:0.00} — {Vector3.Distance(at, me):0.0} y from you, {choice.FromArrival:0} y from {from}; "
        //          + $"{player}; {choice.Alternates.Count} alternate(s). Nothing moves: this is only a look.";
        var shard = shards.Count == 0
            ? "No aethernet shard is known here, so the nearest place to you was sought."
            : $"The nearest aethernet shard is {Vector3.Distance(arrival, me):0} y from you and {choice.FromArrival:0} y from that place.";
        _shore = $"{stands.Count} place(s) at the water. Would stand at ({at.X:0.0}, {at.Y:0.0}, {at.Z:0.0}) facing "
                 + $"{choice.Stand.Facing:0.00}, {Vector3.Distance(at, me):0.0} y from you. {shard} "
                 + $"{char.ToUpperInvariant(player[0])}{player[1..]}; {choice.Alternates.Count} alternate(s). Nothing moves: this is only a look.";
    }

    // [SUPERSEDED 2026-11-14, the same day — the user: "should be all missions". Every kind of crafting mission
    //  is run now, but a red alert (it needs a walk to a hand-in point).]
    // /// <summary>
    // /// What Run takes today: a Master mission of a crafting job. The other kinds are listed and can be ticked,
    // /// but nothing runs them yet (they are untimed, scored to a rank, some with two or three recipes).
    // /// </summary>
    // private bool Runnable(uint missionId)
    //     => _catalog.Missions.TryGetValue(missionId, out var mission) && mission.IsMaster && MissionInfo.IsCrafter(mission.ClassJobId);

    /// <summary>
    /// What Run takes: a crafting job's mission with something to craft - Master, ranked, sequence, weather or
    /// timed. NOT a red alert (a walk to a hand-in point, which Selene cannot do), and not a gatherer's or
    /// Fisher's (those wait on Demeter).
    /// </summary>
    private bool Runnable(uint missionId)
        => _catalog.Missions.TryGetValue(missionId, out var mission)
           && MissionInfo.IsCrafter(mission.ClassJobId)
           && mission.Category != MissionCategory.RedAlert
           && mission.RecipeIds.Count > 0
           // Locked: the game's own answer for the mission's unlock function first; the quest log where it was not asked.
           && !(MissionBoard.GameLocked(mission, _functions) ?? MissionBoard.Missing(mission, _questsDone) is { Count: > 0 });

    /// <summary>
    /// How a mission is run. A Master mission: to the clock, its one recipe in the Cosmic log - as run since
    /// 2026-11-12. Any other: reported at the character's goal rank, with no clock to wait on when it has
    /// none, and by recipe id when it has several recipes.
    /// </summary>
    private MissionPlan PlanFor(uint missionId)
    {
        // [SUPERSEDED 2026-11-14 by the chain's modes — a mode can set the rank a mission is reported at]
        // if (!_catalog.Missions.TryGetValue(missionId, out var mission) || mission.IsMaster)
        //     return MissionPlan.Master;
        //
        // var goal = _config.Characters.TryGetValue(_character.ContentId, out var kept) ? kept.ReportGoal : MissionRank.Gold;
        // return new MissionPlan(mission.TimeLimitSeconds > 0, goal, mission.Crafts);
        if (!_catalog.Missions.TryGetValue(missionId, out var mission))
            return MissionPlan.Master;

        var mode = _chain.Active ? _chain.Mode : (ChainMode?)null;
        if (mission.IsMaster)
        {
            // For class points a Master mission is reported as soon as the game ranks it (ICE's "quick turn-in":
            // class points look fixed a report, so the sooner the better) - still never past the clock's margin.
            // THAT A MASTER MISSION'S RANK REACHES 3 AT ITS FIRST OBJECTIVE IS ICE'S READING, UNSEEN.
            return mode == ChainMode.ClassPoints ? new MissionPlan(Timed: true, ReportAt: MissionRank.Gold) : MissionPlan.Master;
        }

        var goal = mode == ChainMode.GoldAll ? MissionRank.Gold
            : _config.Characters.TryGetValue(_character.ContentId, out var kept) ? kept.ReportGoal
            : MissionRank.Gold;
        return new MissionPlan(mission.TimeLimitSeconds > 0, goal, mission.Crafts);
    }

    /// <summary>
    /// The chain's next mission. With one already in progress it is that one (if Selene can run it); else the
    /// mode's choice among what the game offers this job on this star.
    /// </summary>
    private Choice ChooseNext(
        MissionState state, IReadOnlyCollection<uint> picks, IReadOnlyCollection<MissionCategory> wholeTabs, ToolState? tool,
        ulong contentId, DateTime now)
    {
        if (!state.InZone)
            return new Choice(0, "not in a Cosmic Exploration zone");
        if (state.InMission)
        {
            return Runnable(state.CurrentMissionId)
                ? new Choice(state.CurrentMissionId, "the mission in progress")
                : new Choice(0, $"mission {state.CurrentMissionId} is in progress and is not one Selene can run");
        }

        var here = MissionBoard.On(MissionBoard.Build(_catalog.Missions, state, picks, _payouts.Summaries), state.TerritoryId);
        return MissionChooser.Choose(
            _chain.Mode, here, mission => Runnable(mission.Id), wholeTabs, tool, contentId, (now.DayOfYear * 1000) + _chain.Completed);
    }

    /// <summary>The chain's mode or limit was changed on the Missions page: kept for the logged-in character.</summary>
    private void SetChain(ChainMode mode, int limit)
    {
        var contentId = _character.ContentId;
        if (contentId == 0)
            return;

        var settings = _config.For(contentId);
        settings.ChainMode = mode;
        settings.ChainLimit = Math.Clamp(limit, 0, 999);
        SaveConfig();
        _nextTick = default;
    }

    /// <summary>A tab was ticked or unticked as a whole on the Missions page: kept for the logged-in character.</summary>
    private void SetWholeTab(MissionCategory category, bool whole)
    {
        var contentId = _character.ContentId;
        if (contentId == 0)
            return;

        var tabs = _config.For(contentId).WholeTabs;
        tabs.Remove(category);
        if (whole)
            tabs.Add(category);
        SaveConfig();
        _nextTick = default;
    }

    /// <summary>The rank at which ranked missions are reported was changed on the Missions page.</summary>
    private void SetReportGoal(MissionRank goal)
    {
        var contentId = _character.ContentId;
        if (contentId == 0)
            return;

        _config.For(contentId).ReportGoal = goal;
        SaveConfig();
        _nextTick = default;
    }

    /// <summary>The one thing the run asked for this tick. What came of it is read off the game next tick.</summary>
    private void Execute(RunCommand command)
    {
        switch (command)
        {
            case RunCommand.OpenBoard: _missionWindows.OpenBoard(); break;
            // [SUPERSEDED 2026-11-14 — the tab is the one the mission's kind is on]
            // case RunCommand.ShowMasteryTab: _missionWindows.ShowMasteryTab(); break;
            case RunCommand.ShowMasteryTab:
                _missionWindows.ShowTab(_catalog.Missions.TryGetValue(_run.MissionId, out var sought) ? sought.Category : MissionCategory.Master);
                break;
            case RunCommand.SelectMission: _missionWindows.Select(_run.MissionId); break;
            case RunCommand.InitiateMission: _missionWindows.Initiate(_run.MissionId); break;
            case RunCommand.ConfirmMission: _missionWindows.ConfirmMission(_run.MissionId); break;
            case RunCommand.OpenLog: _missionWindows.OpenLog(); break;
            case RunCommand.SelectRecipe: _missionWindows.SelectRecipe(); break;
            case RunCommand.StartCraft: _hephaestus.CraftSelected(MissionRun.CraftAmount); break;
            case RunCommand.StartCraftStep:
                if (_run.Step is { } step)
                    _hephaestus.CraftItem(step.RecipeId, step.Count);
                break;
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
