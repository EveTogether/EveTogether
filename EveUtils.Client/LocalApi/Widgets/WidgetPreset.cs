using System.Text.Json.Serialization;

namespace EveUtils.Client.LocalApi.Widgets;

/// <summary>
/// The built-in widget presets. The JSON name is the preset's key: its fixed id and its URL (<c>/w/live-dps</c>).
/// Keys are a public contract (saved widgets and OBS scenes point at them) — never rename one.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WidgetPreset>))]
public enum WidgetPreset
{
    [JsonStringEnumMemberName("live-dps")] LiveDps,
    [JsonStringEnumMemberName("dps-graph")] DpsGraph,
    [JsonStringEnumMemberName("current-run")] CurrentRun,
    [JsonStringEnumMemberName("run-totals")] RunTotals,
    [JsonStringEnumMemberName("abyssal-totals")] AbyssalTotals,
    [JsonStringEnumMemberName("last-killmail")] LastKillmail,
    [JsonStringEnumMemberName("kill-alert")] KillAlert,
    [JsonStringEnumMemberName("fleet-dps")] FleetDps
}
