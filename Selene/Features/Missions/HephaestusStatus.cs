using System.Text.Json;

namespace Selene.Features.Missions;

/// <summary>
/// What Hephaestus says of itself, from <c>Hephaestus.GetQueueStatusJson</c> (extend-only on its side; read
/// leniently on ours - a field an older Hephaestus does not send keeps its default here).
/// </summary>
/// <param name="Available">Hephaestus is loaded and answered.</param>
/// <param name="Busy"><c>Hephaestus.IsBusy</c>: it has the character.</param>
/// <param name="CanResume">It stands halted with work left.</param>
/// <param name="Status">Its own sentence.</param>
/// <param name="RunHalt">Why its craft run halted: "None", "GearGate", "OutOfMaterials", ...</param>
/// <param name="GearConditionPercent">The lowest condition of the gear on; -1 = this Hephaestus does not say.</param>
/// <param name="GearGatePercent">Under this it starts no synthesis; -1 = this Hephaestus does not say.</param>
public sealed record HephaestusStatus(
    bool Available, bool Busy, bool CanResume, string Status, string RunHalt, int GearConditionPercent, int GearGatePercent)
{
    public static readonly HephaestusStatus Absent = new(false, false, false, "Hephaestus is not loaded.", "None", -1, -1);

    /// <summary>Hephaestus reports its gear gate (it gained those fields on 2026-11-12).</summary>
    public bool ReportsGear => GearConditionPercent >= 0 && GearGatePercent >= 0;

    /// <summary>
    /// Hephaestus would start a synthesis on this gear - ITS definition of "too worn", so the two plugins
    /// cannot disagree. False when it does not say: a mission is not to be accepted on a guess.
    /// </summary>
    public bool GearFit => ReportsGear && GearConditionPercent >= GearGatePercent;

    /// <summary>Never throws: unreadable JSON is a loaded Hephaestus that said nothing useful.</summary>
    public static HephaestusStatus Parse(string? json, bool busy)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new HephaestusStatus(true, busy, false, string.Empty, "None", -1, -1);

            return new HephaestusStatus(
                true,
                busy,
                root.TryGetProperty("CanResume", out var canResume) && canResume.ValueKind == JsonValueKind.True,
                root.TryGetProperty("Status", out var status) && status.ValueKind == JsonValueKind.String ? status.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("RunHalt", out var halt) && halt.ValueKind == JsonValueKind.String ? halt.GetString() ?? "None" : "None",
                Number(root, "GearConditionPercent"),
                Number(root, "GearGatePercent"));
        }
        catch (JsonException)
        {
            return new HephaestusStatus(true, busy, false, "Hephaestus's status could not be read.", "None", -1, -1);
        }
    }

    private static int Number(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : -1;
}
