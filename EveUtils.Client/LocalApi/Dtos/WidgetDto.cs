using System;
using EveUtils.Client.LocalApi.Widgets;

namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// A widget as the manager lists it: its config, whether it is a built-in preset (read-only, customize = copy), the
/// URL to load in OBS (carries <c>?key=</c> when an API key is set, since OBS cannot send headers) and the size in
/// pixels at its scale.
/// </summary>
public sealed record WidgetDto(WidgetConfig Config, bool IsBuiltIn, string Url, int Width, int Height)
{
    public static WidgetDto FromConfig(WidgetConfig config, string baseUrl, string? apiKey)
    {
        var (width, height) = WidgetPresets.BaseSize(config.Preset);
        var url = $"{baseUrl}/w/{config.Id}";
        if (!string.IsNullOrEmpty(apiKey)) url += $"?key={Uri.EscapeDataString(apiKey)}";
        return new WidgetDto(config, WidgetPresets.IsBuiltIn(config.Id), url,
            width * config.Scale / 100, height * config.Scale / 100);
    }
}
