using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace Selene.Windows;

/// <summary>What the last look for water found, as points in the world.</summary>
/// <param name="Water">The looks that read as water, at the water's surface.</param>
/// <param name="Stands">Every place at the water's edge that was found.</param>
public sealed record ShoreMarks(
    Vector3 Chosen, float Facing, IReadOnlyList<Vector3> Alternates, IReadOnlyList<Vector3> Stands, IReadOnlyList<Vector3> Water);

/// <summary>
/// Draws the last look for water over the game, so one screenshot shows whether Selene's reading of the
/// water and its choice of place are right (asked for after the first look, 2026-11-14: the Debug lines
/// alone could not show it). A DRAWING ONLY: blue dots = water, amber dots = places at the edge, a moonlit
/// ring = an alternate, a green ring with a line = the place chosen and the way it would face.
/// Nothing is targeted, marked in the game or moved.
/// </summary>
public sealed class ShoreOverlay
{
    private static readonly Vector4 WaterBlue = new(0.35f, 0.65f, 1f, 0.75f);

    private readonly IGameGui _gameGui;

    public ShoreOverlay(IGameGui gameGui)
    {
        _gameGui = gameGui;
    }

    /// <summary>Null = nothing is drawn.</summary>
    public ShoreMarks? Marks { get; set; }

    public void Draw()
    {
        if (Marks is not { } marks)
            return;

        try
        {
            var canvas = ImGui.GetBackgroundDrawList();
            var water = ImGui.GetColorU32(WaterBlue);
            var edge = ImGui.GetColorU32(SeleneTheme.Amber);
            var alternate = ImGui.GetColorU32(SeleneTheme.Moon);
            var chosen = ImGui.GetColorU32(SeleneTheme.StatusGreen);

            foreach (var point in marks.Water)
            {
                if (_gameGui.WorldToScreen(point, out var at))
                    canvas.AddCircleFilled(at, 2.5f, water);
            }

            foreach (var point in marks.Stands)
            {
                if (_gameGui.WorldToScreen(point, out var at))
                    canvas.AddCircleFilled(at, 3.5f, edge);
            }

            foreach (var point in marks.Alternates)
            {
                if (_gameGui.WorldToScreen(point, out var at))
                    canvas.AddCircle(at, 9f, alternate, 24, 2f);
            }

            if (_gameGui.WorldToScreen(marks.Chosen, out var here))
            {
                canvas.AddCircle(here, 12f, chosen, 24, 3f);

                // The way it would face: two yalms along the game's rotation (forward = sin, 0, cos).
                var ahead = marks.Chosen + new Vector3(MathF.Sin(marks.Facing) * 2f, 0f, MathF.Cos(marks.Facing) * 2f);
                if (_gameGui.WorldToScreen(ahead, out var there))
                    canvas.AddLine(here, there, chosen, 3f);
            }
        }
        catch
        {
            // A drawing aid: never worth breaking the frame over.
            Marks = null;
        }
    }
}
