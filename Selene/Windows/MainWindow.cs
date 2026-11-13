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
    private MainPage _page = MainPage.Overview;

    public MainWindow(SeleneConfig config, Action save)
        : base("Selene##SeleneMain")
    {
        _config = config;
        _save = save;

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

    /// <summary>The edge dock's own Status line, for the Debug page.</summary>
    public Func<string> DockStatus { get; set; } = () => "off";

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
                case MainPage.Missions:
                    DrawNotBuilt("Missions", "Which missions a burst runs.",
                        "Decided: crafters only. The three Master missions of the current trade, picked by hand in a list here. No re-rolling. Accepted and reported through the game's own mission windows.");
                    break;
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
