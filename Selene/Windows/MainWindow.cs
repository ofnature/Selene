using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Selene.Features.Missions;
using Selene.Features.Schedule;
using Selene.Windows.Components;
using Selene.Windows.Dock;

namespace Selene.Windows;

/// <summary>
/// What the window shows, made on the framework thread about once a second: the window only paints.
/// </summary>
/// <param name="Now">The moment the rows were made for (the PC's local time).</param>
/// <param name="LoggedIn">A character is logged in on this client.</param>
/// <param name="Settings">The schedule settings the rows were dealt from.</param>
/// <param name="Rows">Every character this PC knows, the logged-in one first.</param>
/// <param name="Status">Each service's Status line, for the Debug page.</param>
public sealed record ScheduleView(
    DateTime Now, bool LoggedIn, ScheduleSettings Settings, IReadOnlyList<BoardRow> Rows, IReadOnlyList<(string Service, string Status)> Status);

/// <summary>What the Missions page shows, made on the same tick.</summary>
/// <param name="ClassJobId">The job the character is on; 0 = nobody in the world.</param>
/// <param name="Rows">The job's Master missions: the sheets' and the game's, with the user's picks.</param>
/// <param name="RunActive">A mission run is going.</param>
/// <param name="RunStatus">What the run is doing, or why it is not.</param>
/// <param name="CanAccept">The game holds a mission Run would take: one ticked, or one of a tab ticked as a whole.</param>
/// <param name="ReportGoal">The rank at which this character's ranked missions are reported.</param>
/// <param name="WholeTabs">The tabs ticked as a whole: "run whatever the game offers of this kind".</param>
public sealed record MissionView(
    uint ClassJobId, string JobName, MissionState State, IReadOnlyList<MissionRow> Rows, HephaestusStatus Hephaestus,
    bool RunActive, string RunStatus,
    bool CanAccept = false, MissionRank ReportGoal = MissionRank.Gold, IReadOnlyCollection<MissionCategory>? WholeTabs = null,
    Func<uint, string>? StarName = null,
    IReadOnlyList<ActionNeed>? JobUnlocks = null, IReadOnlyCollection<uint>? QuestsDone = null,
    uint CurrentJobId = 0, IReadOnlyList<(uint JobId, string Name)>? Classes = null, string? ClassNote = null)
{
    /// <summary>
    /// The page shows the class the character is ON. False = another class was picked in the sidebar: its
    /// missions, unlocks and scores are shown (<see cref="ClassJobId"/> is the class SHOWN), and nothing can be run.
    /// </summary>
    public bool OnThisClass => CurrentJobId == 0 || CurrentJobId == ClassJobId;
}

public enum MainPage
{
    Overview,
    Schedule,
    Missions,
    Spots,
    Stops,
    Settings,
    Debug,
}

/// <summary>
/// The plugin's main window, in Hephaestus's layout (the user approved docs/mockup-selene-main.html on
/// 2026-11-12): identity strip, sidebar, and a page of stat tiles and cards. Missions, Spots and Stops are
/// pages that say what was decided for them - nothing behind them is built.
/// </summary>
public sealed class MainWindow : Window
{
    private const string NothingRuns =
        "Nothing runs yet. This is what the schedule says - no mission is started or stopped by it.";

    private readonly SeleneConfig _config;
    private readonly Action _save;
    private readonly Action<uint, bool> _pickMission;
    private readonly Action _startRun;
    private readonly Action _stopRun;
    private MainPage _page = MainPage.Overview;

    /// <param name="pickMission">The user ticked (true) or unticked a mission, by its id.</param>
    /// <param name="startRun">The user pressed Run: finish the mission in progress.</param>
    /// <param name="stopRun">The user pressed Stop.</param>
    public MainWindow(SeleneConfig config, Action save, Action<uint, bool> pickMission, Action startRun, Action stopRun)
        : base("Selene##SeleneMain")
    {
        _config = config;
        _save = save;
        _pickMission = pickMission;
        _startRun = startRun;
        _stopRun = stopRun;

        Size = new Vector2(860f, 640f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640f, 420f),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    /// <summary>Set by the plugin's tick; null until the first one.</summary>
    public ScheduleView? View { get; set; }

    /// <summary>Set by the plugin's tick; null until the first one.</summary>
    public MissionView? Missions { get; set; }

    /// <summary>The edge dock's own Status line, for the Debug page.</summary>
    public Func<string> DockStatus { get; set; } = () => "off";

    /// <summary>The Debug page's "Look for water here": a look only, nothing moves. Null = no button.</summary>
    public Action? LookForWater { get; set; }

    /// <summary>Take the water look's markers off the screen. Null = no button.</summary>
    public Action? HideWaterMarks { get; set; }

    /// <summary>The Debug page's walk test: walk to the place the last look chose. Moves the character.</summary>
    public Action? WalkToSpot { get; set; }

    /// <summary>Stop the walk test.</summary>
    public Action? StopWalk { get; set; }

    /// <summary>Write every measured payout to a sheet (CSV); the Debug page's Payouts line says where.</summary>
    public Action? ExportPayouts { get; set; }

    /// <summary>A class was picked in the sidebar: the Missions page shows it.</summary>
    public Action<uint>? SelectClass { get; set; }

    /// <summary>"Change to ..." in the sidebar: put the character on that class (Hephaestus does it).</summary>
    public Action<uint>? ChangeClass { get; set; }

    /// <summary>A Missions tab was ticked (true) or unticked as a whole: "run whatever the game offers of this kind".</summary>
    public Action<MissionCategory, bool>? SetWholeTab { get; set; }

    /// <summary>The rank at which ranked missions are reported was chosen.</summary>
    public Action<MissionRank>? SetReportGoal { get; set; }

    // [SUPERSEDED 2026-11-12 by the sidebar layout below — the first window's two tabs]
    // public override void Draw()
    // {
    //     using var tabs = ImRaii.TabBar("##SeleneTabs");
    //     if (!tabs)
    //         return;
    //     using (var tab = ImRaii.TabItem("Schedule")) { if (tab) DrawSchedule(); }
    //     using (var tab = ImRaii.TabItem("Debug")) { if (tab) DrawDebug(); }
    // }

    public override void Draw()
    {
        using var style = SeleneTheme.PushWindowStyle();

        var view = View;
        var mine = view?.Rows.FirstOrDefault(row => row.IsThisCharacter);
        var (status, accent, detail) = HeaderStatus(view, mine);
        SeleneHeader.Draw(status, accent, detail, () => _page = MainPage.Settings);

        var sidebarWidth = SeleneTheme.SidebarWidth * ImGuiHelpers.GlobalScale;
        using (ImRaii.Child("##selene_sidebar", new Vector2(sidebarWidth, -1), false))
            DrawSidebar();

        ImGui.SameLine();

        using (ImRaii.Child("##selene_content", new Vector2(-1, -1), false))
        {
            switch (_page)
            {
                case MainPage.Overview: DrawOverview(view, mine); break;
                case MainPage.Schedule: DrawSchedule(view); break;
                // [SUPERSEDED 2026-11-12 by DrawMissions — the page reads the game and keeps the picks now]
                // case MainPage.Missions:
                //     DrawNotBuilt("Missions", "Which missions a burst runs.",
                //         "Decided: crafters only. The three Master missions of the current trade, picked by hand in a list here. No re-rolling. Accepted and reported through the game's own mission windows.");
                //     break;
                case MainPage.Missions: DrawMissions(Missions); break;
                case MainPage.Spots:
                    DrawNotBuilt("Spots", "Where a character stands to craft, and where it idles on a break.",
                        "Decided: you record a handful of spots a zone with a \"save this spot\" button here. Selene picks one a session with a small random offset, never the same one twice running.");
                    break;
                case MainPage.Stops:
                    DrawNotBuilt("Stops", "What stopped a character, and when.",
                        "Decided: a tell, a /say or an emote at the character, a party invite or trade request, or an unexpected window stops everything - the item in hand is finished, nothing new is started, nothing is answered, and it never resumes by itself. A visible GM: finish the mission, go to the FC house, log out to character select, and log why. Alerts: a sound and a chat line on this box, and the Daedalus relay to the others.");
                    break;
                case MainPage.Settings: DrawSettings(view); break;
                case MainPage.Debug: DrawDebug(view); break;
            }
        }
    }

    /// <summary>
    /// The edge dock's pop-out: this character's evening at a glance, the others under it, and a way to
    /// the window. Drawn inside the dock's own panel window.
    /// </summary>
    public void DrawGlance()
    {
        using var style = SeleneTheme.PushWindowStyle();

        var view = View;
        var mine = view?.Rows.FirstOrDefault(row => row.IsThisCharacter);
        var (status, accent, _) = HeaderStatus(view, mine);
        SeleneHeader.DrawCompact(status, accent);

        if (view == null || mine == null)
        {
            SeleneTheme.Text("Log in to a character to see its evening.", SeleneTheme.TextSecondary);
        }
        else
        {
            SeleneTheme.Text(mine.Name, SeleneTheme.MoonSoft);
            ImGui.SameLine();
            SeleneTheme.Text(mine.Decision.Status, SeleneTheme.TextPrimary);
            ScheduleBar.Draw(mine.Plan, view.Now, ImGui.GetContentRegionAvail().X, 14f, hours: true);
        }

        if (view != null)
        {
            foreach (var row in view.Rows.Where(row => !row.IsThisCharacter))
            {
                SeleneTheme.Dot(PhaseColor(row.Decision.Phase));
                SeleneTheme.Text(row.Name, SeleneTheme.TextSecondary);
                ImGui.SameLine();
                SeleneTheme.Text(row.Decision.Status, SeleneTheme.TextDim);
                ScheduleBar.Draw(row.Plan, view.Now, ImGui.GetContentRegionAvail().X, 8f, hours: false);
            }
        }

        SeleneTheme.TextWrapped(NothingRuns, SeleneTheme.TextMuted);
        if (ImGui.Button(IsOpen ? "Close the window" : "Open the window", new Vector2(-1, 0)))
            IsOpen = !IsOpen;
    }

    // ---- header / sidebar ----------------------------------------------------------------------

    /// <summary>The pill names the schedule's PHASE - it does not claim that anything is being done.</summary>
    private static (string Status, Vector4 Accent, string Detail) HeaderStatus(ScheduleView? view, BoardRow? mine)
    {
        if (view == null || mine == null)
            return ("OFFLINE", SeleneTheme.TextMuted, view is { LoggedIn: true } ? "Not in the world yet" : "Log in to a character");

        var until = ScheduleBar.Clock(mine.Decision.Until);
        return mine.Decision.Phase switch
        {
            SchedulePhase.Work => ("WORK", SeleneTheme.StatusGreen,
                $"Burst {mine.Plan.BurstAt(view.Now)} of {mine.Plan.Bursts.Count} · until {until}"),
            SchedulePhase.Break => ("BREAK", SeleneTheme.Amber, $"Next burst {until}"),
            _ => ("QUIET", SeleneTheme.StatusGrey, $"Next session {DayAndClock(mine.Decision.Until)}"),
        };
    }

    private void DrawSidebar()
    {
        ImGui.Spacing();
        SeleneTheme.SectionLabel("Overview");
        Tab("Overview", FontAwesomeIcon.Home, MainPage.Overview);
        SeleneTheme.SectionLabel("Sessions");
        Tab("Schedule", FontAwesomeIcon.Moon, MainPage.Schedule);
        Tab("Missions", FontAwesomeIcon.List, MainPage.Missions);
        Tab("Spots", FontAwesomeIcon.MapMarkerAlt, MainPage.Spots);
        SeleneTheme.SectionLabel("Safety");
        Tab("Stops", FontAwesomeIcon.Flag, MainPage.Stops);
        SeleneTheme.SectionLabel("System");
        Tab("Settings", FontAwesomeIcon.Cog, MainPage.Settings);
        Tab("Debug", FontAwesomeIcon.Bug, MainPage.Debug);
        DrawClassPicker();
    }

    /// <summary>
    /// The class picker (the user, 2026-11-14: "maybe need a class picker on the left column with a select and
    /// change class"): the eleven Cosmic jobs, three to a row. A click SELECTS - the Missions page shows that
    /// class's missions, unlocks and scores, which the game keeps whatever job is on. The class the character
    /// is on is in green. "Change to ..." puts the character on the picked class, through Hephaestus.
    /// </summary>
    private void DrawClassPicker()
    {
        if (Missions is not { Classes: { Count: > 0 } classes } view || view.CurrentJobId == 0)
            return;

        SeleneTheme.SectionLabel("Class");
        var scale = ImGuiHelpers.GlobalScale;
        var gap = 4f * scale;
        var width = (ImGui.GetContentRegionAvail().X - (gap * 2f)) / 3f;
        for (var i = 0; i < classes.Count; i++)
        {
            var (jobId, name) = classes[i];
            if (i % 3 != 0)
                ImGui.SameLine(0f, gap);

            // Shown = filled in moonlight; the character's own = green letters.
            using var fill = ImRaii.PushColor(ImGuiCol.Button, SeleneTheme.Moon with { W = 0.45f }, jobId == view.ClassJobId);
            using var letters = ImRaii.PushColor(ImGuiCol.Text, SeleneTheme.StatusGreen, jobId == view.CurrentJobId);
            if (ImGui.Button($"{name}##SeleneClass{jobId}", new Vector2(width, 0f)))
            {
                SelectClass?.Invoke(jobId);
                _page = MainPage.Missions;
            }
        }

        if (!view.OnThisClass)
        {
            ImGui.BeginDisabled(view.RunActive);
            if (ImGui.Button($"Change to {view.JobName}", new Vector2(-1f, 0f)))
                ChangeClass?.Invoke(view.ClassJobId);
            ImGui.EndDisabled();
            SeleneTheme.Tooltip("Puts the character on this class: Hephaestus equips its saved gearset. Not while a mission is in progress.");
        }

        if (!string.IsNullOrEmpty(view.ClassNote))
            SeleneTheme.TextWrapped(view.ClassNote, SeleneTheme.TextMuted);
    }

    private void Tab(string label, FontAwesomeIcon icon, MainPage page)
    {
        if (SidebarTab.Draw(label, icon, _page == page))
            _page = page;
    }

    // ---- overview ------------------------------------------------------------------------------

    private static void DrawOverview(ScheduleView? view, BoardRow? mine)
    {
        SeleneTheme.PageHeader("Overview", "What this character's schedule says right now, and when that changes.");

        if (view == null)
        {
            SeleneTheme.Text("Nothing has ticked yet.", SeleneTheme.TextSecondary);
            return;
        }

        if (mine == null)
        {
            SeleneTheme.Text(
                view.LoggedIn ? "This character is not in the world yet." : "Log in to a character to see its schedule.",
                SeleneTheme.TextSecondary);
        }
        else
        {
            DrawTiles(mine, view.Now);
            SeleneTheme.VSpace(4f);
            SeleneTheme.TextWrapped(NothingRuns, SeleneTheme.TextMuted);

            SeleneTheme.SectionLabel("Tonight");
            SeleneTheme.VSpace(2f);
            DrawEvening(mine, view.Now, isMine: true);
        }

        var others = view.Rows.Where(row => !row.IsThisCharacter).ToList();
        if (others.Count == 0)
            return;

        SeleneTheme.SectionLabel("Other characters on this PC");
        SeleneTheme.VSpace(2f);
        foreach (var row in others)
            DrawEvening(row, view.Now, isMine: false);
    }

    private static void DrawTiles(BoardRow mine, DateTime now)
    {
        var gap = 8f * ImGuiHelpers.GlobalScale;
        var width = (ImGui.GetContentRegionAvail().X - (gap * 3f)) / 4f;
        var plan = mine.Plan;

        var (phase, phaseSub) = mine.Decision.Phase switch
        {
            SchedulePhase.Work => ("Work", $"burst {plan.BurstAt(now)} of {plan.Bursts.Count}"),
            SchedulePhase.Break => ("Break", "between bursts"),
            _ => ("Quiet", string.Empty),
        };
        var until = mine.Decision.Until;

        StatTile.Draw("Now", phase, phaseSub, PhaseColor(mine.Decision.Phase), width,
            "The schedule's phase. Nothing acts on it yet.");
        ImGui.SameLine(0f, gap);
        StatTile.Draw("Next change", until.Date == now.Date || until - now < TimeSpan.FromHours(12) ? ScheduleBar.Clock(until) : DayAndClock(until),
            $"in {ScheduleBar.Length(until - now)}", SeleneTheme.Moon, width);
        ImGui.SameLine(0f, gap);
        StatTile.Draw("Worked tonight", ScheduleBar.Length(plan.WorkedBy(now)), $"of {ScheduleBar.Length(plan.Total)}", SeleneTheme.Moon, width,
            "How much of this play day's bursts lies behind the clock - by the schedule, not by what was done.");
        ImGui.SameLine(0f, gap);
        StatTile.Draw("Next evening", mine.NextEvening is { } next ? DayAndClock(next) : "none", "first burst", SeleneTheme.StatusGrey, width);
    }

    /// <summary>One character's play day as a card: who, what the schedule says, the bar, and the bursts.</summary>
    private static void DrawEvening(BoardRow row, DateTime now, bool isMine)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = ImGui.GetContentRegionAvail().X;
        var plan = row.Plan;
        var origin = Card.BeginFlat();
        var right = origin.X + width - (11f * scale);

        SeleneTheme.Dot(PhaseColor(row.Decision.Phase));
        SeleneTheme.Text(row.Name, isMine ? SeleneTheme.MoonSoft : SeleneTheme.TextPrimary);
        ImGui.SameLine();
        SeleneTheme.Text(row.Decision.Status, isMine ? SeleneTheme.TextPrimary : SeleneTheme.TextDim);
        SeleneTheme.TextRight(
            isMine
                ? $"{plan.Opens.ToString("ddd d MMM", CultureInfo.InvariantCulture)} · {ScheduleBar.Clock(plan.Opens)} to {ScheduleBar.Clock(plan.Closes)}"
                : $"{plan.Bursts.Count} burst(s) · {ScheduleBar.Length(plan.Total)}",
            SeleneTheme.TextDim, right);

        ScheduleBar.Draw(plan, now, Card.InnerWidth(width), isMine ? 18f : 10f, hours: isMine);

        if (isMine)
        {
            var parts = new List<string>();
            for (var i = 0; i < plan.Bursts.Count; i++)
            {
                var burst = plan.Bursts[i];
                if (i > 0)
                    parts.Add($"break {(int)(burst.Start - plan.Bursts[i - 1].End).TotalMinutes} min");
                parts.Add($"{ScheduleBar.Clock(burst.Start)} to {ScheduleBar.Clock(burst.End)} ({ScheduleBar.Length(burst.End - burst.Start)})");
            }

            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Card.InnerWidth(width));
            SeleneTheme.Text(parts.Count == 0 ? "No burst fits these play hours." : string.Join("   ·   ", parts), SeleneTheme.TextSecondary);
            ImGui.PopTextWrapPos();
        }

        Card.EndFlat(origin, width, accent: isMine ? SeleneTheme.Moon : null);
        SeleneTheme.VSpace(2f);
    }

    // ---- schedule ------------------------------------------------------------------------------

    private static void DrawSchedule(ScheduleView? view)
    {
        SeleneTheme.PageHeader("Schedule", "Every character this PC has seen, and the play day the schedule dealt each of them.");

        if (view is not { Rows.Count: > 0 })
        {
            SeleneTheme.Text("No character yet. Log in and it appears here.", SeleneTheme.TextSecondary);
            return;
        }

        SeleneTheme.TextWrapped(Describe(view.Settings), SeleneTheme.TextSecondary);
        SeleneTheme.TextWrapped("The same for every character, and not editable yet.", SeleneTheme.TextMuted);
        SeleneTheme.VSpace(4f);

        using var table = ImRaii.Table("##SeleneSchedule", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
        if (!table)
            return;

        var scale = ImGuiHelpers.GlobalScale;
        ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthFixed, 140f * scale);
        ImGui.TableSetupColumn("The schedule says", ImGuiTableColumnFlags.WidthFixed, 190f * scale);
        ImGui.TableSetupColumn($"{ScheduleBar.Clock(view.Rows[0].Plan.Opens)} to {ScheduleBar.Clock(view.Rows[0].Plan.Closes)}", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var row in view.Rows)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            SeleneTheme.Dot(PhaseColor(row.Decision.Phase));
            SeleneTheme.Text(row.Name, row.IsThisCharacter ? SeleneTheme.MoonSoft : SeleneTheme.TextPrimary);

            ImGui.TableNextColumn();
            SeleneTheme.Text(row.Decision.Status, SeleneTheme.TextPrimary);

            ImGui.TableNextColumn();
            ScheduleBar.Draw(row.Plan, view.Now, ImGui.GetContentRegionAvail().X, 14f, hours: false);
        }
    }

    // ---- missions ------------------------------------------------------------------------------

    private void DrawMissions(MissionView? view)
    {
        // [SUPERSEDED 2026-11-14 by the tabs — the page listed the job's Master missions only]
        // SeleneTheme.PageHeader("Missions", "The Master missions of the job you are on. Tick the ones Selene may run.");
        SeleneTheme.PageHeader("Missions", "The missions of the job you are on, one kind to a tab. Tick the ones Selene may run.");
        // [SUPERSEDED 2026-11-12 by the attended run — the page could only read then]
        // SeleneTheme.TextWrapped(
        //     "Nothing is accepted, crafted or reported yet. This page reads the game and keeps your picks for when it is.",
        //     SeleneTheme.Amber);
        // [SUPERSEDED 2026-11-12 by Selene accepting the mission itself — the user accepted and opened the log then]
        // SeleneTheme.TextWrapped(
        //     "Selene does not accept missions yet. Accept a ticked mission yourself and open the Cosmic Crafting Log on its recipe, then press Run: Selene crafts through Hephaestus, has the item in hand finished before the clock runs out, and presses Report. Stay and watch - this has not been run before.",
        //     SeleneTheme.Amber);
        SeleneTheme.TextWrapped(
            "Run takes ONE mission from start to report: it accepts a ticked mission the game offers (the bonus one first), opens the Cosmic Crafting Log, crafts through Hephaestus, has the item in hand finished before the clock runs out, and presses Report. With a mission already in progress it carries on from there. Stay and watch: accepting and opening the log have not been run before.",
            SeleneTheme.Amber);
        SeleneTheme.VSpace(2f);

        if (view == null)
        {
            SeleneTheme.Text("Nothing has ticked yet.", SeleneTheme.TextSecondary);
            return;
        }

        if (view.ClassJobId == 0)
        {
            SeleneTheme.Text("Log in to a character to see its missions.", SeleneTheme.TextSecondary);
            return;
        }

        DrawMissionTiles(view);
        SeleneTheme.VSpace(4f);
        DrawRunControls(view);

        // [SUPERSEDED 2026-11-14 by the tabs — the user: "need to wire in the other missions, like ice does and
        //  have tabs to seperate them". Every Cosmic job's missions are listed now, a kind to a tab.]
        // if (!MissionInfo.IsCrafter(view.ClassJobId))
        // {
        //     SeleneTheme.TextWrapped("Crafters only for now: change to a crafting job to see its Master missions.", SeleneTheme.TextSecondary);
        //     return;
        // }
        //
        // if (view.Rows.Count == 0)
        // {
        //     SeleneTheme.TextWrapped("Neither the sheets nor the game name a Master mission for this job.", SeleneTheme.TextSecondary);
        //     return;
        // }
        //
        // SeleneTheme.SectionLabel($"Master missions · {view.JobName}");
        // SeleneTheme.VSpace(2f);
        // DrawMissionTable(view);
        //
        // // The sheets' Master flag was checked once against the game; say so if the two ever part again.
        // if (view.State.InZone && view.Rows.Any(row => row.Offered && !row.Info.IsMaster))
        // {
        //     SeleneTheme.TextWrapped(
        //         "The game offers a mission here that the sheets do not mark as Master: Selene's reading of the sheets is out of date. The row's tooltip shows what it found.",
        //         SeleneTheme.Rose);
        // }
        // else if (view.State.InZone && view.State.OfferedMaster.Count > 0 && view.Rows.Any(row => !row.Offered && row.Info.IsMaster))
        // {
        //     SeleneTheme.TextWrapped(
        //         "The sheets name a Master mission for this job that the game is not offering right now.",
        //         SeleneTheme.TextMuted);
        // }
        if (view.Rows.Count == 0)
        {
            SeleneTheme.TextWrapped("Neither the sheets nor the game name a mission for this job.", SeleneTheme.TextSecondary);
            return;
        }

        DrawUnlocks(view);

        SeleneTheme.SectionLabel($"Missions · {view.JobName}");
        SeleneTheme.VSpace(2f);

        // [SUPERSEDED 2026-11-14, the same day — the user: "need the mission seperated by moon". The kinds are
        //  tabs INSIDE a star's tab now: DrawKindTabs.]
        // using var tabs = ImRaii.TabBar("##SeleneMissionTabs");
        // if (!tabs)
        //     return;
        //
        // foreach (var category in MissionTabs)
        // {
        //     var rows = MissionBoard.In(view.Rows, category);
        //     if (rows.Count == 0)
        //         continue;
        //
        //     // "###" keeps the tab's identity while its label (the count, the mark) changes.
        //     var mark = view.State.InZone && rows.Any(row => row.Offered) ? " •" : string.Empty;
        //     using var tab = ImRaii.TabItem($"{TabName(category)} ({rows.Count}){mark}###SeleneMissionTab{category}");
        //     if (!tab)
        //         continue;
        //
        //     DrawMissionTab(view, category, rows);
        // }
        using var stars = ImRaii.TabBar("##SeleneMissionStars");
        if (!stars)
            return;

        // A tab a star, in the order they were reached; "Other" last, for missions no star is known for.
        // Arriving on a star (or logging in on one) brings its tab up once; after that the choice is the user's.
        var here = view.State.InZone ? view.State.TerritoryId : 0u;
        var arrived = here != 0 && here != _starShownFor;
        foreach (var territory in MissionStars.Stars.Select(star => star.TerritoryId).Append(0u))
        {
            var onStar = MissionBoard.On(view.Rows, territory);
            if (onStar.Count == 0)
                continue;

            var name = view.StarName?.Invoke(territory) ?? (territory == 0 ? "Other" : $"zone {territory}");
            var label = $"{name}{(territory != 0 && territory == here ? " (here)" : string.Empty)}###SeleneStar{territory}";
            using var star = ImRaii.TabItem(label, arrived && territory == here ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None);
            if (star)
                DrawKindTabs(view, onStar, territory);
        }

        _starShownFor = here;
    }

    /// <summary>
    /// What the job's missions and actions want unlocked, against the character's quest log (the user,
    /// 2026-11-14: "some mission require pre quests like unlocking collection and certain actions unlocked
    /// from class quests ... check if we have them unlocked"). What is missing is said in rose; what is all
    /// there, in one quiet line each.
    /// </summary>
    private static void DrawUnlocks(MissionView view)
    {
        SeleneTheme.SectionLabel($"Unlocks · {view.JobName}");
        SeleneTheme.VSpace(2f);

        if (view.QuestsDone is not { } done)
        {
            SeleneTheme.TextWrapped("The quest log has not been read yet.", SeleneTheme.TextMuted);
            SeleneTheme.VSpace(4f);
            return;
        }

        // The quests the missions themselves ask for (the game's own list, a mission's WKSFunction).
        var asked = view.Rows.SelectMany(row => row.Info.Needs ?? Array.Empty<QuestNeed>()).GroupBy(need => need.QuestId).ToList();
        var locked = false;
        foreach (var quest in asked.Where(quest => !done.Contains(quest.Key)))
        {
            locked = true;
            var missions = view.Rows.Count(row => row.Missing?.Any(need => need.QuestId == quest.Key) == true);
            SeleneTheme.TextWrapped($"{missions} mission(s) are LOCKED: the quest \"{quest.First().QuestName}\" is not done.", SeleneTheme.Rose);
        }

        if (!locked)
        {
            SeleneTheme.TextWrapped(
                asked.Count == 0
                    ? "None of this job's missions asks for a quest."
                    : $"Every quest this job's missions ask for is done: {string.Join(", ", asked.Select(quest => $"\"{quest.First().QuestName}\""))}.",
                SeleneTheme.TextSecondary);
        }

        // The job's actions that a quest unlocks.
        var unlocks = view.JobUnlocks ?? Array.Empty<ActionNeed>();
        var missing = unlocks.Where(unlock => !done.Contains(unlock.QuestId)).GroupBy(unlock => unlock.QuestId).ToList();
        foreach (var quest in missing)
        {
            // [SUPERSEDED 2026-11-14 — first seen in game on a Fisher: three rose lines, eleven spearfishing actions
            //  among them, for things that lock NO mission. Rose is kept for what locks a mission; this is amber.]
            // SeleneTheme.TextWrapped(
            //     $"NOT LEARNED: {string.Join(", ", quest.Select(unlock => $"{unlock.Action} (level {unlock.Level})"))} — the quest \"{quest.First().QuestName}\" is not done.",
            //     SeleneTheme.Rose);
            SeleneTheme.TextWrapped(
                $"Not learned: {string.Join(", ", quest.Select(unlock => $"{unlock.Action} (level {unlock.Level})"))} — the quest \"{quest.First().QuestName}\" is not done. No mission is locked by it.",
                SeleneTheme.Amber);
        }

        if (missing.Count == 0 && unlocks.Count > 0)
        {
            SeleneTheme.TextWrapped(
                $"Every action of this job that a quest unlocks is unlocked: {string.Join(", ", unlocks.Select(unlock => unlock.Action))}.",
                SeleneTheme.TextSecondary);
            SeleneTheme.Tooltip("By the quest log: the quest behind each action is done. The job's LEVEL is not checked here, and a specialist action also needs the specialist's soul.");
        }

        SeleneTheme.VSpace(4f);
    }

    /// <summary>The star whose tab was last brought up by itself: the one the character was on.</summary>
    private uint _starShownFor;

    /// <summary>One star's missions, a kind to a tab.</summary>
    private void DrawKindTabs(MissionView view, IReadOnlyList<MissionRow> onStar, uint territory)
    {
        using var tabs = ImRaii.TabBar($"##SeleneMissionTabs{territory}");
        if (!tabs)
            return;

        foreach (var category in MissionTabs)
        {
            var rows = MissionBoard.In(onStar, category);
            if (rows.Count == 0)
                continue;

            // "###" keeps the tab's identity while its label (the count, the mark) changes.
            var mark = view.State.InZone && rows.Any(row => row.Offered) ? " •" : string.Empty;
            using var tab = ImRaii.TabItem($"{TabName(category)} ({rows.Count}){mark}###SeleneMissionTab{territory}_{category}");
            if (!tab)
                continue;

            DrawMissionTab(view, category, rows);
        }
    }

    /// <summary>The tabs, in the order drawn: what Selene runs first, then the ranks, then the special kinds.</summary>
    private static readonly MissionCategory[] MissionTabs =
    {
        MissionCategory.Master, MissionCategory.RankA, MissionCategory.RankB, MissionCategory.RankC, MissionCategory.RankD,
        MissionCategory.Sequence, MissionCategory.Weather, MissionCategory.Timed, MissionCategory.RedAlert,
    };

    /// <summary>The game's rank number as a word; a number it has no name for (a Master mission's) is shown as it is.</summary>
    private static string RankName(int rank) => rank switch
    {
        0 => "no rank",
        1 => "bronze",
        2 => "silver",
        3 => "gold",
        _ => $"rank {rank}",
    };

    private static string TabName(MissionCategory category) => category switch
    {
        MissionCategory.Master => "Master",
        MissionCategory.RankA => "A / EX",
        MissionCategory.RankB => "B",
        MissionCategory.RankC => "C",
        MissionCategory.RankD => "D",
        MissionCategory.Sequence => "Sequence",
        MissionCategory.Weather => "Weather",
        MissionCategory.Timed => "Timed",
        _ => "Red Alert",
    };

    /// <summary>One tab: what can be said of the kind, then its missions - what the game holds first.</summary>
    private void DrawMissionTab(MissionView view, MissionCategory category, IReadOnlyList<MissionRow> rows)
    {
        // [SUPERSEDED 2026-11-14, the same day — the user: "should be all missions"; only red alerts and the
        //  gatherers' missions are not run now]
        // var runs = category == MissionCategory.Master && MissionInfo.IsCrafter(view.ClassJobId);
        // if (!runs)
        // {
        //     SeleneTheme.TextWrapped(
        //         MissionInfo.IsCrafter(view.ClassJobId)
        //             ? "Listed, and your ticks are kept - but Selene runs only Master missions so far. Nothing on this tab is accepted or run yet."
        //             : "Listed, and your ticks are kept - but Selene runs only crafting missions so far. Gathering and fishing wait on Demeter.",
        //         SeleneTheme.Amber);
        //     SeleneTheme.VSpace(2f);
        // }
        var crafter = MissionInfo.IsCrafter(view.ClassJobId);
        if (!crafter)
        {
            SeleneTheme.TextWrapped("Listed, and your ticks are kept - but gathering and fishing missions are not run yet: they wait on Demeter.", SeleneTheme.Amber);
        }
        else if (category == MissionCategory.RedAlert)
        {
            SeleneTheme.TextWrapped("Listed, and your ticks are kept - but a red alert is not run yet: it ends with a walk to a hand-in point, which Selene cannot do.", SeleneTheme.Amber);
        }
        else
        {
            if (category != MissionCategory.Master)
            {
                SeleneTheme.TextWrapped(
                    "Built, never run: these missions are reported at the rank chosen beside Run, and one of several recipes is crafted recipe by recipe. Stay and watch the first ones.",
                    SeleneTheme.Amber);
            }

            var whole = view.WholeTabs?.Contains(category) ?? false;
            if (ImGui.Checkbox($"Run whatever the game offers on this tab##whole{category}", ref whole))
                SetWholeTab?.Invoke(category, whole);
            SeleneTheme.Tooltip("Run then takes any mission of this kind the game holds, ticked or not - instead of re-rolling for a ticked one.");
        }

        SeleneTheme.VSpace(2f);

        // How far the tab is, by the game's mission log (the count a "gold all" would work down).
        if (category == MissionCategory.Master)
        {
            // No stars on a Master mission: the Best column is its high score.
        }
        else if (rows.Count > 0 && rows[0].Best != null)
        {
            var gold = rows.Count(row => row.Best == MissionBest.Gold);
            SeleneTheme.Text(
                gold == rows.Count ? $"Gold on all {rows.Count}." : $"Gold on {gold} of {rows.Count}.",
                gold == rows.Count ? SeleneTheme.Amber : SeleneTheme.TextSecondary);
        }

        // OrderBy is stable: the mission in progress, then what the game holds, each in id order.
        DrawMissionTable(view, rows.OrderByDescending(row => row.Current).ThenByDescending(row => row.Offered).ToList());

        if (category != MissionCategory.Master || !view.State.InZone)
            return;

        // The sheets' Master flag was checked once against the game; say so if the two ever part again.
        if (view.State.OfferedMaster.Any(id => view.Rows.Any(row => row.Info.Id == id && !row.Info.IsMaster)))
        {
            SeleneTheme.TextWrapped(
                "The game offers a Master mission that the sheets do not mark as one: Selene's reading of the sheets is out of date. Look for it on the other tabs.",
                SeleneTheme.Rose);
        }
        else if (view.State.OfferedMaster.Count > 0 && rows.Any(row => !row.Offered))
        {
            SeleneTheme.TextWrapped(
                "The sheets name a Master mission for this job that the game is not offering right now.",
                SeleneTheme.TextMuted);
        }
    }

    /// <summary>Run and Stop, and what the run says. A press is only a request: the plugin's tick acts on it.</summary>
    private void DrawRunControls(MissionView view)
    {
        var scale = ImGuiHelpers.GlobalScale;

        // [SUPERSEDED 2026-11-12 — Run needed a mission in progress then]
        // ImGui.BeginDisabled(view.RunActive || !view.State.InMission);
        // if (ImGui.Button("Run this mission", new Vector2(150f * scale, 0f)))
        //     _startRun();
        // ImGui.EndDisabled();
        // if (!view.RunActive && !view.State.InMission)
        //     SeleneTheme.Tooltip("Accept a mission first.");
        // [SUPERSEDED 2026-11-14 — every kind is listed and can be ticked now; Run takes Master crafting missions only]
        // var canAccept = view.Rows.Any(row => row.Picked && row.Offered);
        // [SUPERSEDED 2026-11-14, the same day — the plugin says what Run would take, now that most kinds run]
        // var canAccept = MissionInfo.IsCrafter(view.ClassJobId)
        //                 && view.Rows.Any(row => row.Picked && row.Offered && row.Info.IsMaster);
        // Another class is being looked at: nothing of it can be run from here.
        if (!view.OnThisClass)
        {
            SeleneTheme.TextWrapped(
                $"Showing {view.JobName}, which the character is not on. Use \"Change to {view.JobName}\" in the left column to run its missions.",
                SeleneTheme.Amber);
            if (view.RunActive)
                SeleneTheme.TextWrapped(view.RunStatus, SeleneTheme.StatusGreen);
            SeleneTheme.VSpace(4f);
            return;
        }

        var canAccept = view.CanAccept;
        ImGui.BeginDisabled(view.RunActive || (!view.State.InMission && !canAccept));
        if (ImGui.Button(view.State.InMission ? "Run this mission" : "Run a ticked mission", new Vector2(170f * scale, 0f)))
            _startRun();
        ImGui.EndDisabled();
        if (!view.RunActive && !view.State.InMission && !canAccept)
            SeleneTheme.Tooltip("Tick a crafting mission the game is offering first, or tick a whole tab. Red alerts and gathering or fishing missions are not run yet.");

        // The goal for every mission that is not a Master one (those are played to the clock).
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f * scale);
        using (var goal = ImRaii.Combo("##SeleneReportGoal", view.ReportGoal.ToString()))
        {
            if (goal)
            {
                foreach (var rank in new[] { MissionRank.Gold, MissionRank.Silver, MissionRank.Bronze })
                {
                    if (ImGui.Selectable(rank.ToString(), rank == view.ReportGoal))
                        SetReportGoal?.Invoke(rank);
                }
            }
        }

        SeleneTheme.Tooltip("Report a ranked mission as soon as it reads this rank. If the materials run out first, what was made is reported. Master missions do not use it.");

        ImGui.SameLine();
        ImGui.BeginDisabled(!view.RunActive);
        if (ImGui.Button("Stop", new Vector2(80f * scale, 0f)))
            _stopRun();
        ImGui.EndDisabled();
        SeleneTheme.Tooltip("Ends the run. Hephaestus finishes the item in hand; the mission is left for you to report.");

        ImGui.SameLine();
        SeleneTheme.TextWrapped(view.RunStatus, view.RunActive ? SeleneTheme.StatusGreen : SeleneTheme.TextSecondary);
        SeleneTheme.VSpace(4f);
    }

    private static void DrawMissionTiles(MissionView view)
    {
        var gap = 8f * ImGuiHelpers.GlobalScale;
        var width = (ImGui.GetContentRegionAvail().X - (gap * 3f)) / 4f;
        var state = view.State;
        var hephaestus = view.Hephaestus;

        StatTile.Draw("Job", view.JobName, state.InZone ? $"score {state.ClassScore:N0}" : null, SeleneTheme.Moon, width,
            state.InZone ? "The job's Cosmic class score." : "Not in a Cosmic Exploration zone: the game is not saying anything about missions.");
        ImGui.SameLine(0f, gap);
        StatTile.Draw("In progress",
            !state.InZone ? "Not in zone" : state.InMission ? state.Score.ToString("N0", CultureInfo.InvariantCulture) : "None",
            !state.InMission ? null
                : state.ClockSeconds is { } clock ? $"{state.Rank} · {clock / 60}:{clock % 60:00} left"
                : $"{state.Rank} · no clock",
            state.InMission ? SeleneTheme.StatusGreen : SeleneTheme.StatusGrey, width,
            state.InMission ? $"Mission {state.CurrentMissionId}: its score, rank and clock, as the game has them." : null);
        ImGui.SameLine(0f, gap);
        StatTile.Draw("Hephaestus",
            !hephaestus.Available ? "Not loaded" : hephaestus.Busy ? "Busy" : "Idle",
            hephaestus.RunHalt != "None" ? hephaestus.RunHalt : null,
            !hephaestus.Available ? SeleneTheme.Rose : hephaestus.Busy ? SeleneTheme.StatusGreen : SeleneTheme.Moon, width,
            hephaestus.Status.Length > 0 ? hephaestus.Status : null);
        ImGui.SameLine(0f, gap);
        StatTile.Draw("Gear",
            hephaestus.ReportsGear ? $"{hephaestus.GearConditionPercent}%" : "?",
            hephaestus.ReportsGear ? $"gate {hephaestus.GearGatePercent}%" : "not reported",
            hephaestus.GearFit ? SeleneTheme.StatusGreen : SeleneTheme.Rose, width,
            hephaestus.ReportsGear
                ? "The lowest condition of the gear on, and the line under which Hephaestus starts no synthesis. Selene will accept no mission under it."
                : "This Hephaestus does not report its gear gate (the fields were added on 2026-11-12). Selene will accept no mission while it cannot tell.");
    }

    private void DrawMissionTable(MissionView view, IReadOnlyList<MissionRow> rows)
    {
        // [SUPERSEDED 2026-11-14 — a sixth column: what the mission pays towards the cosmic tool]
        // using var table = ImRaii.Table("##SeleneMissions", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
        // [SUPERSEDED 2026-11-14, the same day — a seventh: the best done on it (the user: "need a column to see if
        //  they have gotten gold yet")]
        // using var table = ImRaii.Table("##SeleneMissions", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
        // [SUPERSEDED 2026-11-14, the same day — an eighth: the class points measured]
        // using var table = ImRaii.Table("##SeleneMissions", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
        using var table = ImRaii.Table("##SeleneMissions", 8, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
        if (!table)
            return;

        var scale = ImGuiHelpers.GlobalScale;
        ImGui.TableSetupColumn("Run", ImGuiTableColumnFlags.WidthFixed, 34f * scale);
        ImGui.TableSetupColumn("Mission", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Clock", ImGuiTableColumnFlags.WidthFixed, 50f * scale);
        ImGui.TableSetupColumn("Silver / gold", ImGuiTableColumnFlags.WidthFixed, 110f * scale);
        ImGui.TableSetupColumn("Best", ImGuiTableColumnFlags.WidthFixed, 56f * scale);
        ImGui.TableSetupColumn("Class pts", ImGuiTableColumnFlags.WidthFixed, 70f * scale);
        ImGui.TableSetupColumn("Tool data", ImGuiTableColumnFlags.WidthFixed, 140f * scale);
        ImGui.TableSetupColumn("On the board", ImGuiTableColumnFlags.WidthFixed, 100f * scale);
        ImGui.TableHeadersRow();

        foreach (var row in rows)
        {
            var info = row.Info;
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var picked = row.Picked;
            if (ImGui.Checkbox($"##pick{info.Id}", ref picked))
                _pickMission(info.Id, picked);

            ImGui.TableNextColumn();
            // [SUPERSEDED 2026-11-14 — a mission a quest keeps locked is drawn in rose, and its tooltip says which quest]
            // SeleneTheme.Text(info.Name, row.Picked ? SeleneTheme.TextPrimary : SeleneTheme.TextSecondary);
            // SeleneTheme.Tooltip($"Mission {info.Id} · {TabName(info.Category)} · tier {info.LevelGroup} · recipes {(info.RecipeIds.Count == 0 ? "none" : string.Join(", ", info.RecipeIds))}");
            SeleneTheme.Text(info.Name, row.Locked ? SeleneTheme.Rose : row.Picked ? SeleneTheme.TextPrimary : SeleneTheme.TextSecondary);
            SeleneTheme.Tooltip(
                $"Mission {info.Id} · {TabName(info.Category)} · tier {info.LevelGroup} · recipes {(info.RecipeIds.Count == 0 ? "none" : string.Join(", ", info.RecipeIds))}"
                + (info.Needs is { Count: > 0 } needs ? $" · needs the quest {string.Join(" and ", needs.Select(need => $"\"{need.QuestName}\""))}" : string.Empty)
                + (row.Locked ? $" — NOT DONE: {string.Join(", ", row.Missing!.Select(need => $"\"{need.QuestName}\""))}" : string.Empty));

            ImGui.TableNextColumn();
            SeleneTheme.Text(info.TimeLimitSeconds > 0 ? $"{info.TimeLimitSeconds / 60}:{info.TimeLimitSeconds % 60:00}" : "untimed", SeleneTheme.TextDim);

            ImGui.TableNextColumn();
            SeleneTheme.Text(
                $"{info.SilverScore.ToString("N0", CultureInfo.InvariantCulture)} / {info.GoldScore.ToString("N0", CultureInfo.InvariantCulture)}",
                SeleneTheme.TextDim);

            // The best done on it. A Master mission has no star: its best is the highest score the game keeps.
            ImGui.TableNextColumn();
            if (info.IsMaster)
            {
                SeleneTheme.Text(
                    row.HighScore is not { } high ? "?" : high == 0 ? "—" : high.ToString("N0", CultureInfo.InvariantCulture),
                    row.HighScore is > 0 ? SeleneTheme.Amber : SeleneTheme.TextMuted);
                SeleneTheme.Tooltip(row.HighScore == null
                    ? "The game holds a high score only for the Master missions it is offering, in a Cosmic Exploration zone."
                    : "The highest score on this Master mission, as the game keeps it. Read from a field of the game's state that has no name yet: compare it with the High Score on the game's Tool Mastery tab.");
            }
            else
            switch (row.Best)
            {
                case MissionBest.Gold: SeleneTheme.Text("gold", SeleneTheme.Amber); break;
                case MissionBest.Completed: SeleneTheme.Text("done", SeleneTheme.TextSecondary); break;
                case MissionBest.None: SeleneTheme.Text("—", SeleneTheme.TextMuted); break;
                default: SeleneTheme.Text("?", SeleneTheme.TextMuted); break;
            }

            if (!info.IsMaster)
            SeleneTheme.Tooltip(row.Best switch
            {
                MissionBest.Gold => "Completed with a gold star, by the game's mission log.",
                MissionBest.Completed => "Completed, but not yet with a gold star.",
                MissionBest.None => "Never completed.",
                _ => "The game's mission log can only be read in a Cosmic Exploration zone.",
            });

            // The class points it was MEASURED to pay (they are in none of the game's sheets): the latest at the
            // best rank seen. Blank until one has been watched to its end.
            ImGui.TableNextColumn();
            if (row.Payout is not { } payout)
            {
                SeleneTheme.Text("—", SeleneTheme.TextMuted);
                SeleneTheme.Tooltip("Not measured yet: Selene notes the class score before and after a mission, whoever runs it.");
            }
            else
            {
                var (bestRank, bestPoints) = payout.Best;
                SeleneTheme.Text(bestPoints.ToString("N0", CultureInfo.InvariantCulture), SeleneTheme.TextPrimary);
                SeleneTheme.Tooltip(
                    $"Measured {payout.Samples} time(s). "
                    + string.Join(" · ", payout.ByRank.OrderByDescending(rank => rank.Key).Select(rank =>
                        $"{RankName(rank.Key)}: {rank.Value.Last:N0}"
                        + (rank.Value.Min != rank.Value.Max ? $" (from {rank.Value.Min:N0} to {rank.Value.Max:N0})" : string.Empty)
                        + $" x{rank.Value.Count}"))
                    + (payout.PerMinute is { } perMinute
                        ? $" · about {payout.AverageSeconds / 60}:{payout.AverageSeconds % 60:00} a mission, {perMinute:N0} a minute at {RankName(bestRank)}"
                        : " · not timed yet"));
            }

            // What it pays towards the job's cosmic tool; the rest of the payout is in the tooltip.
            ImGui.TableNextColumn();
            if (info.Reward is not { } reward)
            {
                SeleneTheme.Text("?", SeleneTheme.TextMuted);
            }
            else
            {
                var data = reward.ToolDataText;
                SeleneTheme.Text(
                    data.Length > 0 ? data : reward.Tokens > 0 ? $"{reward.Tokens} tokens" : "none",
                    data.Length > 0 ? SeleneTheme.TextPrimary : SeleneTheme.TextDim);
                SeleneTheme.Tooltip(
                    $"The sheets' base figures (what a rank adds is not in them). Tool data: {(data.Length > 0 ? data : "none")} · "
                    + $"Cosmocredits {reward.CosmoCredits} · star credits {reward.PlanetCredits}"
                    + (reward.Tokens > 0 ? $" · exploration tokens {reward.Tokens}" : string.Empty));
            }

            ImGui.TableNextColumn();
            if (row.Locked)
                SeleneTheme.Text("locked", SeleneTheme.Rose);
            else if (!view.State.InZone)
                SeleneTheme.Text("not in zone", SeleneTheme.TextMuted);
            else if (row.Current)
                SeleneTheme.Text("in progress", SeleneTheme.StatusGreen);
            else if (row.Offered)
                SeleneTheme.Text(row.Bonus ? "offered · bonus" : "offered", SeleneTheme.MoonSoft);
            else
                SeleneTheme.Text("not offered", SeleneTheme.TextMuted);
        }
    }

    // ---- not built / settings / debug ----------------------------------------------------------

    private static void DrawNotBuilt(string title, string subtitle, string decided)
    {
        SeleneTheme.PageHeader(title, subtitle);
        SeleneTheme.Text("Not built yet.", SeleneTheme.Amber);
        SeleneTheme.VSpace(2f);
        SeleneTheme.TextWrapped(decided, SeleneTheme.TextSecondary);
    }

    private void DrawSettings(ScheduleView? view)
    {
        SeleneTheme.PageHeader("Settings", "How Selene shows itself. The schedule's own settings come later.");

        SeleneTheme.SectionLabel("Edge dock");
        var docked = _config.EdgeDockEnabled;
        if (ImGui.Checkbox("A Selene tab on the screen edge", ref docked))
        {
            _config.EdgeDockEnabled = docked;
            _save();
        }

        SeleneTheme.TextWrapped(
            "Point at the tab and the evening pops out beside it; click the tab to pin it open; drag it to put it on another edge. It shares the edges with Daedalus's and Charon's tabs.",
            SeleneTheme.TextMuted);

        if (ImGui.Button("Put the tab back on the left edge"))
        {
            _config.EdgeDockEdge = DockEdge.Left;
            _config.EdgeDockAlong = 0.5f;
            _save();
        }

        SeleneTheme.VSpace(6f);
        SeleneTheme.SectionLabel("Schedule");
        SeleneTheme.TextWrapped(view == null ? "Nothing has ticked yet." : Describe(view.Settings), SeleneTheme.TextSecondary);
        SeleneTheme.TextWrapped("Not editable yet: every character uses these.", SeleneTheme.TextMuted);
    }

    private void DrawDebug(ScheduleView? view)
    {
        SeleneTheme.PageHeader("Debug", "What each part is doing, or why it is idle.");

        if (view == null)
        {
            SeleneTheme.Text("Nothing has ticked yet.", SeleneTheme.TextSecondary);
            return;
        }

        foreach (var (service, status) in view.Status.Append(("Edge dock", DockStatus())))
        {
            SeleneTheme.Text(service, SeleneTheme.TextDim);
            ImGui.SameLine(110f * ImGuiHelpers.GlobalScale);
            SeleneTheme.TextWrapped(status, SeleneTheme.TextPrimary);
        }

        // A look only: the "Water" and "Shore" lines above say what it found. Nothing moves.
        if (LookForWater != null)
        {
            ImGui.Spacing();
            if (ImGui.Button("Look for water here"))
                LookForWater();
            SeleneTheme.Tooltip("Also draws what it found over the game: blue dots = water, amber dots = places at the edge, a green ring with a line = the place chosen and the way it would face.");

            if (HideWaterMarks != null)
            {
                ImGui.SameLine();
                if (ImGui.Button("Hide the markers"))
                    HideWaterMarks();
            }
        }

        if (ExportPayouts != null)
        {
            if (ImGui.Button("Export payouts as a sheet"))
                ExportPayouts();
            SeleneTheme.Tooltip("Writes every measured mission payout to payouts.csv; the \"Payouts\" line above says where.");
        }

        // The walk test. THIS MOVES THE CHARACTER - the only thing on this page that does.
        if (WalkToSpot != null && StopWalk != null)
        {
            if (ImGui.Button("Walk to the chosen spot"))
                WalkToSpot();
            SeleneTheme.Tooltip("MOVES YOUR CHARACTER: Ariadne walks it to the green ring and it turns to the water. The mesh is asked first, so a place that cannot be reached is refused before a step is taken. The \"Walk\" line above says how it went.");

            ImGui.SameLine();
            if (ImGui.Button("Stop walking"))
                StopWalk();
        }
    }

    // ---- shared --------------------------------------------------------------------------------

    private static Vector4 PhaseColor(SchedulePhase phase) => phase switch
    {
        SchedulePhase.Work => SeleneTheme.StatusGreen,
        SchedulePhase.Break => SeleneTheme.Amber,
        _ => SeleneTheme.StatusGrey,
    };

    private static string DayAndClock(DateTime time) => time.ToString("ddd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The settings in one line: "Play hours 17:00 to 01:00 · bursts 2 to 4 h · ...".</summary>
    private static string Describe(ScheduleSettings settings)
    {
        static string Minute(int minuteOfDay) => $"{(((minuteOfDay % 1440) + 1440) % 1440) / 60:00}:{(((minuteOfDay % 1440) + 1440) % 1440) % 60:00}";
        static string Range(MinuteRange range) => range.Min % 60 == 0 && range.Max % 60 == 0
            ? $"{range.Min / 60} to {range.Max / 60} h"
            : $"{range.Min} to {range.Max} min";

        return $"Play hours {Minute(settings.WindowStartMinute)} to {Minute(settings.WindowEndMinute)} · bursts {Range(settings.Burst)} · breaks {Range(settings.Break)} · daily cap {Range(settings.DailyCap)}";
    }
}
