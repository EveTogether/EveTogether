using System.Text.Json.Serialization;

namespace EveUtils.Client.LocalApi.Widgets;

[JsonConverter(typeof(JsonStringEnumConverter<WidgetTheme>))]
public enum WidgetTheme
{
    [JsonStringEnumMemberName("together")] Together,
    [JsonStringEnumMemberName("minimal")] Minimal,
    [JsonStringEnumMemberName("ticker")] Ticker
}
