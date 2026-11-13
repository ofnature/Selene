using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Selene.Features.Schedule;

namespace Selene.Windows.Components;

/// <summary>
/// A play day as a bar: its bursts (dim = finished, green = running, moonlight = to come) and a marker
/// where the clock stands. Hovering lists the bursts.
/// </summary>
internal static class ScheduleBar
{
    /// <param name="height">The bar's height before UI scaling.</param>
    /// <param name="hours">Label the hours under the bar.</param>
    public static void Draw(DayPlan plan, DateTime now, float width, float height, bool hours)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(MathF.Max(width, 60f * scale), height * scale);
        var origin = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var seconds = Math.Max(1.0, (plan.Closes - plan.Opens).TotalSeconds);
        var rounding = 4f * scale;

        float X(DateTime time) => origin.X + (size.X * (float)Math.Clamp((time - plan.Opens).TotalSeconds / seconds, 0.0, 1.0));

        dl.AddRectFilled(origin, origin + size, SeleneTheme.U32(SeleneTheme.Track), rounding);
        foreach (var burst in plan.Bursts)
        {
            var color = now >= burst.End ? SeleneTheme.MoonDim
                : now >= burst.Start ? SeleneTheme.StatusGreen
                : SeleneTheme.Moon;
            dl.AddRectFilled(new Vector2(X(burst.Start), origin.Y), new Vector2(X(burst.End), origin.Y + size.Y), SeleneTheme.U32(color), rounding);
        }

        if (now >= plan.Opens && now <= plan.Closes)
        {
            dl.AddLine(
                new Vector2(X(now), origin.Y - (3f * scale)), new Vector2(X(now), origin.Y + size.Y + (3f * scale)),
                SeleneTheme.U32(SeleneTheme.TextPrimary), 2f * scale);
        }

        ImGui.Dummy(size);
        if (ImGui.IsItemHovered())
            DrawTooltip(plan);

        if (hours)
            DrawHours(plan, origin, size, dl);
    }

    /// <summary>"17:08 to 19:52" and the like.</summary>
    public static string Clock(DateTime time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"3 h 07", for a length of time.</summary>
    public static string Length(TimeSpan length) => $"{(int)length.TotalHours} h {length.Minutes:00}";

    private static void DrawHours(DayPlan plan, Vector2 origin, Vector2 size, ImDrawListPtr dl)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.SetWindowFontScale(0.80f);
        var color = SeleneTheme.U32(SeleneTheme.TextMuted);
        var y = origin.Y + size.Y + (3f * scale);
        var totalHours = (plan.Closes - plan.Opens).TotalHours;
        var step = totalHours > 12 ? 3 : 1;
        var first = plan.Opens.Minute == 0 && plan.Opens.Second == 0 ? plan.Opens : plan.Opens.Date.AddHours(plan.Opens.Hour + 1);

        for (var hour = first; hour <= plan.Closes; hour = hour.AddHours(step))
        {
            var label = hour.ToString("HH", CultureInfo.InvariantCulture);
            var labelWidth = ImGui.CalcTextSize(label).X;
            var x = origin.X + (size.X * (float)((hour - plan.Opens).TotalHours / totalHours)) - (labelWidth / 2f);
            dl.AddText(new Vector2(Math.Clamp(x, origin.X, origin.X + size.X - labelWidth), y), color, label);
        }

        var lineHeight = ImGui.GetTextLineHeight();
        ImGui.SetWindowFontScale(1f);
        ImGui.Dummy(new Vector2(size.X, lineHeight));
    }

    private static void DrawTooltip(DayPlan plan)
    {
        using var tip = ImRaii.Tooltip();
        SeleneTheme.Text(
            $"{plan.Opens.ToString("ddd d MMM", CultureInfo.InvariantCulture)}, play hours {Clock(plan.Opens)} to {Clock(plan.Closes)}",
            SeleneTheme.MoonSoft);
        if (plan.Bursts.Count == 0)
            SeleneTheme.Text("No burst fits these play hours.", SeleneTheme.TextDim);
        foreach (var burst in plan.Bursts)
            SeleneTheme.Text($"{Clock(burst.Start)} to {Clock(burst.End)}   {Length(burst.End - burst.Start)}", SeleneTheme.TextPrimary);
    }
}
