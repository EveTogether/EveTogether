using System.Text.Json.Serialization;

namespace EveUtils.Client.LocalApi.Widgets;

[JsonConverter(typeof(JsonStringEnumConverter<WidgetBackground>))]
public enum WidgetBackground
{
    [JsonStringEnumMemberName("transparent")] Transparent,
    [JsonStringEnumMemberName("panel")] Panel
}
