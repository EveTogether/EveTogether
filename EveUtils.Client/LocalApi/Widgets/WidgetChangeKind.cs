using System.Text.Json.Serialization;

namespace EveUtils.Client.LocalApi.Widgets;

[JsonConverter(typeof(JsonStringEnumConverter<WidgetChangeKind>))]
public enum WidgetChangeKind
{
    [JsonStringEnumMemberName("created")] Created,
    [JsonStringEnumMemberName("updated")] Updated,
    [JsonStringEnumMemberName("deleted")] Deleted
}
