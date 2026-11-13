using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Selene.Windows.Components;

namespace Selene.Windows;

/// <summary>
/// Identity strip across the top of the main window, in HephaestusHeader's shape: the crescent mark, the
/// name, the tagline, a status pill, a detail line and the settings shortcut - moonlit where the forge has
/// its flame and embers.
/// </summary>
internal static class SeleneHeader
{
    /// <summary>Where the stars stand in the strip (fractions of its width and height) and how fast each breathes.</summary>
    private static readonly (float X, float Y, float Radius, double PeriodMs)[] Stars =
    {
        (0.38f, 0.22f, 1.2f, 3100.0),
        (0.47f, 0.66f, 1.6f, 4300.0),
        (0.56f, 0.34f, 1.1f, 2700.0),
        (0.64f, 0.76f, 1.2f, 5200.0),
        (0.71f, 0.16f, 1.6f, 3700.0),
    };

    public static void Draw(string status, Vector4 statusAccent, string detail, Action openSettings)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(ImGui.GetContentRegionAvail().X, SeleneTheme.HeaderHeight * scale);
        var origin = ImGui.GetCursorScreenPos();
        var end = origin + size;
        var dl = ImGui.GetWindowDrawList();

        var left = Vector4.Lerp(SeleneTheme.CardBg, SeleneTheme.Moon, 0.16f);
        var right = Vector4.Lerp(SeleneTheme.CardBg, SeleneTheme.MoonSoft, 0.06f);
        dl.AddRectFilledMultiColor(origin, end,
            SeleneTheme.U32(left), SeleneTheme.U32(right), SeleneTheme.U32(right), SeleneTheme.U32(left));
        dl.AddRect(origin, end, SeleneTheme.U32(SeleneTheme.WithAlpha(SeleneTheme.MoonSoft, 0.38f)),
            SeleneTheme.CardRounding * scale, ImDrawFlags.None, 1.2f * scale);

        DrawCrescent(origin + (new Vector2(40f, 44f) * scale), 18f * scale, left, dl);
        foreach (var star in Stars)
        {
            var alpha = 0.25f + (0.50f * SeleneTheme.Pulse(star.PeriodMs));
            dl.AddCircleFilled(origin + new Vector2(size.X * star.X, size.Y * star.Y), star.Radius * scale,
                SeleneTheme.U32(SeleneTheme.WithAlpha(SeleneTheme.MoonSoft, alpha)));
        }

        var textX = origin.X + (72f * scale);
        ImGui.SetWindowFontScale(1.62f);
        dl.AddText(new Vector2(textX, origin.Y + (16f * scale)), SeleneTheme.U32(SeleneTheme.TextPrimary), "SELENE");
        ImGui.SetWindowFontScale(0.78f);
        dl.AddText(new Vector2(textX, origin.Y + (50f * scale)), SeleneTheme.U32(SeleneTheme.MoonSoft), "KEEPS HER OWN HOURS");
        ImGui.SetWindowFontScale(1f);

        var pillSize = Pill.Measure(status);
        Pill.DrawAt(new Vector2(end.X - (18f * scale) - pillSize.X, origin.Y + (16f * scale)), status, statusAccent);

        ImGui.SetWindowFontScale(0.82f);
        var detailSize = ImGui.CalcTextSize(detail);
        dl.AddText(new Vector2(end.X - (18f * scale) - detailSize.X, origin.Y + (44f * scale)), SeleneTheme.U32(SeleneTheme.TextDim), detail);
        ImGui.SetWindowFontScale(1f);

        var button = 22f * scale;
        ImGui.SetCursorScreenPos(new Vector2(end.X - (18f * scale) - button, end.Y - (27f * scale)));
        bool pressed;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            pressed = ImGui.Button(FontAwesomeIcon.Cog.ToIconString() + "##selene_settings", new Vector2(button, button));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Settings");
        if (pressed)
            openSettings();

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(size);
    }

    /// <summary>The wordmark alone on a thin strip, for the dock's pop-out panel.</summary>
    public static void DrawCompact(string status, Vector4 statusAccent)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(ImGui.GetContentRegionAvail().X, 34f * scale);
        var origin = ImGui.GetCursorScreenPos();
        var end = origin + size;
        var dl = ImGui.GetWindowDrawList();

        var left = Vector4.Lerp(SeleneTheme.CardBg, SeleneTheme.Moon, 0.20f);
        var right = Vector4.Lerp(SeleneTheme.CardBg, SeleneTheme.MoonSoft, 0.08f);
        dl.AddRectFilledMultiColor(origin, end,
            SeleneTheme.U32(left), SeleneTheme.U32(right), SeleneTheme.U32(right), SeleneTheme.U32(left));
        dl.AddRect(origin, end, SeleneTheme.U32(SeleneTheme.WithAlpha(SeleneTheme.MoonSoft, 0.32f)),
            SeleneTheme.CardRounding * scale, ImDrawFlags.None, 1.0f * scale);

        DrawCrescent(origin + (new Vector2(18f, 17f) * scale), 8f * scale, left, dl);
        dl.AddText(new Vector2(origin.X + (34f * scale), origin.Y + (8f * scale)), SeleneTheme.U32(SeleneTheme.TextPrimary), "SELENE");

        var pillSize = Pill.Measure(status);
        Pill.DrawAt(new Vector2(end.X - (8f * scale) - pillSize.X, origin.Y + ((size.Y - pillSize.Y) / 2f)), status, statusAccent);

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(size);
    }

    /// <summary>The mark: a disc with a second one, in the strip's own colour, biting into it from the upper right.</summary>
    private static void DrawCrescent(Vector2 center, float radius, Vector4 behind, ImDrawListPtr dl)
    {
        dl.AddCircleFilled(center, radius, SeleneTheme.U32(SeleneTheme.MoonSoft), 32);
        dl.AddCircleFilled(center + new Vector2(radius * 0.42f, -radius * 0.22f), radius * 0.86f,
            SeleneTheme.U32(SeleneTheme.WithAlpha(behind, 1f)), 32);
    }
}
