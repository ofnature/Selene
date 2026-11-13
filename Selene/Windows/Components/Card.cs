using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Selene.Windows.Components;

/// <summary>
/// A card-shaped background behind content of unknown height (Hephaestus's flat card): call
/// <see cref="BeginFlat"/>, draw, then <see cref="EndFlat"/>.
/// </summary>
internal static class Card
{
    public static Vector2 BeginFlat()
    {
        var dl = ImGui.GetWindowDrawList();
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.BeginGroup();
        ImGui.SetCursorScreenPos(origin + (new Vector2(11f, 9f) * ImGuiHelpers.GlobalScale));
        ImGui.BeginGroup();
        return origin;
    }

    /// <param name="accent">A tick down the card's left side, for the one that matters.</param>
    public static void EndFlat(Vector2 origin, float width, Vector4? accent = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.EndGroup();
        var end = new Vector2(origin.X + width, ImGui.GetItemRectMax().Y + (9f * scale));
        var dl = ImGui.GetWindowDrawList();
        dl.ChannelsSetCurrent(0);
        var rounding = SeleneTheme.CardRounding * scale;
        dl.AddRectFilled(origin, end, SeleneTheme.U32(SeleneTheme.CardBgSoft), rounding);
        dl.AddRect(origin, end, SeleneTheme.U32(SeleneTheme.WithAlpha(SeleneTheme.BorderDim, 0.55f)), rounding);
        if (accent is { } a)
            dl.AddRectFilled(origin, new Vector2(origin.X + (3f * scale), end.Y), SeleneTheme.U32(SeleneTheme.WithAlpha(a, 0.85f)), 2f);
        dl.ChannelsMerge();
        ImGui.SetCursorScreenPos(new Vector2(origin.X, end.Y));
        ImGui.Dummy(new Vector2(width, 0f));
        ImGui.EndGroup();
    }

    /// <summary>The width content may take inside a flat card of <paramref name="cardWidth"/>.</summary>
    public static float InnerWidth(float cardWidth) => cardWidth - (22f * ImGuiHelpers.GlobalScale);
}
