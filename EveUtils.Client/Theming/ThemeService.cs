using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using EveUtils.Shared.Modules.Settings.Repositories;

namespace EveUtils.Client.Theming;

/// <summary>
/// Runtime faction theming: swaps the per-faction <c>ResourceDictionary</c> merged into the application
/// resources so the whole surface re-tints live — every <c>DynamicResource</c> consumer (accent ramp, borders,
/// glows, header/window gradients, hex brand, rail) updates without a reload. The choice persists via the Settings
/// module (key <c>SettingKey</c>); default = <see cref="FactionTheme.Gallente"/> (the original look).
/// </summary>
public interface IThemeService
{
    /// <summary>The faction theme currently applied.</summary>
    FactionTheme Current { get; }

    /// <summary>Applies a faction theme live and persists the choice.</summary>
    void Apply(FactionTheme faction);

    /// <summary>Loads the persisted faction (if any) and applies it. Call once at startup.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised on the UI thread whenever the applied faction changes.</summary>
    event Action<FactionTheme>? Changed;
}

public sealed class ThemeService(ISettingRepository settings) : IThemeService
{
    public const string SettingKey = "ui.faction";

    public FactionTheme Current { get; private set; } = FactionTheme.Gallente;

    public event Action<FactionTheme>? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var saved = (await settings.ListAsync(cancellationToken))
            .FirstOrDefault(s => s.Key == SettingKey)?.Value;
        if (TryParse(saved, out var faction) && faction != Current)
            ApplyCore(faction);
    }

    public void Apply(FactionTheme faction)
    {
        if (faction == Current) return;
        ApplyCore(faction);
        _ = settings.UpsertAsync(SettingKey, faction.ToString().ToLowerInvariant());
    }

    private void ApplyCore(FactionTheme faction)
    {
        var app = Application.Current;
        if (app is null) return;

        var include = new ResourceInclude((Uri?)null)
        {
            Source = new Uri($"avares://EveUtils.Client/Themes/Factions/{faction}.axaml")
        };

        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = include;
        else merged.Add(include);

        Current = faction;

        if (Dispatcher.UIThread.CheckAccess()) Changed?.Invoke(faction);
        else Dispatcher.UIThread.Post(() => Changed?.Invoke(faction));
    }

    public static bool TryParse(string? value, out FactionTheme faction) =>
        Enum.TryParse(value, ignoreCase: true, out faction);
}
