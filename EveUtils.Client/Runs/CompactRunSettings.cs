using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Settings.Dtos;

namespace EveUtils.Client.Runs;

/// <summary>
/// The three settings behind the compact run window (ET-478): which compact version opens, whether every new run
/// window opens compact, and the state the pilot left the last one in. The geometry (position, opacity, pin) stays in
/// the activity window's one <c>ui.activity-window</c> entry, shared by every state.
/// </summary>
public static class CompactRunSettings
{
    public const string StyleKey = "ui.activity-window.compact-style";
    public const string OpenCompactKey = "ui.activity-window.open-compact";
    public const string CompactKey = "ui.activity-window.compact";

    public static CompactRunStyle ReadStyle(IReadOnlyList<SettingDto>? settings) =>
        Enum.TryParse(_ValueOf(settings, StyleKey), ignoreCase: true, out CompactRunStyle style)
        && Enum.IsDefined(style)
            ? style
            : CompactRunStyle.Card;

    public static bool ReadOpenCompact(IReadOnlyList<SettingDto>? settings) => _ValueOf(settings, OpenCompactKey) == "true";

    public static bool ReadCompact(IReadOnlyList<SettingDto>? settings) => _ValueOf(settings, CompactKey) == "true";

    /// <summary>Whether a window being opened now starts compact: the pilot asked for every run to, or left the last
    /// one that way.</summary>
    public static bool StartsCompact(IReadOnlyList<SettingDto>? settings) =>
        ReadOpenCompact(settings) || ReadCompact(settings);

    private static string? _ValueOf(IReadOnlyList<SettingDto>? settings, string key) =>
        settings?.FirstOrDefault(setting => setting.Key == key)?.Value;
}
