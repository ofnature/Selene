using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Selene.Windows;

/// <summary>
/// Selene's palette and shared helpers, in HephaestusTheme's shape (the layout the user approved on
/// 2026-11-12 from docs/mockup-selene-main.html). The identity accent is MOONLIGHT where Hephaestus has
/// forge gold; mint/green = good, amber = waiting, rose = a problem keep the suite's meanings.
/// </summary>
internal static class SeleneTheme
{
    // ---- layout constants (HephaestusTheme's) ----
    public const float SidebarWidth = 160f;
    public const float HeaderHeight = 88f;
    public const float StatTileHeight = 58f;

    // ---- identity: moonlight ----
    public static readonly Vector4 Moon = new(0.616f, 0.733f, 0.918f, 1.00f);
    public static readonly Vector4 MoonSoft = new(0.788f, 0.855f, 0.961f, 1.00f);
    public static readonly Vector4 MoonDim = new(0.290f, 0.369f, 0.502f, 1.00f);

    // ---- status accents ----
    public static readonly Vector4 StatusGreen = new(0.20f, 0.75f, 0.35f, 1.00f);
    public static readonly Vector4 StatusGrey = new(0.45f, 0.45f, 0.50f, 1.00f);
    public static readonly Vector4 Amber = new(0.92f, 0.74f, 0.34f, 1.00f);
    public static readonly Vector4 Rose = new(0.93f, 0.42f, 0.50f, 1.00f);

    // ---- surfaces ----
    public static readonly Vector4 CardBg = new(0.070f, 0.090f, 0.105f, 0.85f);
    public static readonly Vector4 CardBgSoft = new(0.085f, 0.105f, 0.120f, 0.55f);
    public static readonly Vector4 CardBgHover = new(0.100f, 0.125f, 0.145f, 0.95f);
    public static readonly Vector4 BorderDim = new(0.22f, 0.26f, 0.30f, 1.00f);
    public static readonly Vector4 Track = new(0.105f, 0.130f, 0.150f, 1.00f);

    // ---- text ----
    public static readonly Vector4 TextPrimary = new(0.95f, 0.93f, 0.89f, 1.00f);
    public static readonly Vector4 TextSecondary = new(0.78f, 0.80f, 0.84f, 1.00f);
    public static readonly Vector4 TextDim = new(0.55f, 0.58f, 0.62f, 1.00f);
    public static readonly Vector4 TextMuted = new(0.40f, 0.42f, 0.46f, 1.00f);

    public const float CardRounding = 7f;
    public const float FrameRounding = 5f;
    public const float WindowRounding = 7f;

    public static uint U32(Vector4 color) => ImGui.ColorConvertFloat4ToU32(color);

    public static Vector4 WithAlpha(Vector4 c, float a) => c with { W = a };

    /// <summary>0..1 sine pulse on the given period, for the stars in the header.</summary>
    public static float Pulse(double periodMs)
    {
        var t = (Environment.TickCount64 % periodMs) / periodMs;
        return (float)((Math.Sin(t * Math.PI * 2.0) + 1.0) * 0.5);
    }

    public static void VSpace(float pixels)
        => ImGui.Dummy(new Vector2(0, pixels * ImGuiHelpers.GlobalScale));

    /// <summary>The dim uppercase label over a section, as in Hephaestus.</summary>
    public static void SectionLabel(string label)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, TextDim))
            ImGui.TextUnformatted(label.ToUpperInvariant());
    }

    public static void Text(string text, Vector4 color)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextUnformatted(text);
    }

    /// <summary>Text that wraps at the content edge, for sentences rather than labels.</summary>
    public static void TextWrapped(string text, Vector4 color)
    {
        ImGui.PushTextWrapPos(0f);
        Text(text, color);
        ImGui.PopTextWrapPos();
    }

    /// <summary>
    /// Text ending at <paramref name="rightEdge"/> (a screen X), level with the item drawn last. Painted
    /// straight onto the draw list: it takes no room, so it works inside a card's groups, where a
    /// SameLine offset is measured from the group and not from the window.
    /// </summary>
    public static void TextRight(string text, Vector4 color, float rightEdge)
    {
        var position = new Vector2(rightEdge - ImGui.CalcTextSize(text).X, ImGui.GetItemRectMin().Y);
        ImGui.GetWindowDrawList().AddText(position, U32(color), text);
    }

    /// <summary>A small filled circle at the start of a line, then the same line continues.</summary>
    public static void Dot(Vector4 color)
    {
        var line = ImGui.GetTextLineHeight();
        var origin = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddCircleFilled(origin + new Vector2(line * 0.35f, line * 0.55f), line * 0.22f, U32(color));
        ImGui.Dummy(new Vector2(line * 0.7f, line));
        ImGui.SameLine();
    }

    public static void Tooltip(string text, float wrapWidth = 300f)
    {
        if (!ImGui.IsItemHovered())
            return;

        using var tip = ImRaii.Tooltip();
        ImGui.PushTextWrapPos(wrapWidth * ImGuiHelpers.GlobalScale);
        Text(text, TextSecondary);
        ImGui.PopTextWrapPos();
    }

    public static IDisposable PushWindowStyle()
    {
        var p = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, FrameRounding);
        p.Push(ImGuiStyleVar.WindowRounding, WindowRounding);
        p.Push(ImGuiStyleVar.ChildRounding, CardRounding);
        p.Push(ImGuiStyleVar.ItemSpacing, new Vector2(8, 7) * ImGuiHelpers.GlobalScale);
        return p;
    }

    /// <summary>The large page title, its muted one-line subtitle, and a rule (Hephaestus's PageHeader).</summary>
    public static void PageHeader(string title, string subtitle)
    {
        ImGui.SetWindowFontScale(1.45f);
        Text(title, TextPrimary);
        ImGui.SetWindowFontScale(1f);
        TextWrapped(subtitle, TextMuted);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    // [SUPERSEDED 2026-11-12 by SectionLabel / PageHeader — the first window's gold header and palette]
    // public static readonly Vector4 AccentGold = new(0.85f, 0.65f, 0.20f, 1.00f);
    // public static readonly Vector4 AccentDim = new(0.55f, 0.42f, 0.13f, 1.00f);
    // public static readonly Vector4 StatusYellow = new(0.85f, 0.75f, 0.10f, 1.00f);
    // public static readonly Vector4 BgRow = new(0.15f, 0.15f, 0.18f, 0.60f);
    // public static void SectionHeader(string label) { ... gold label with a hairline to the right edge ... }
}
