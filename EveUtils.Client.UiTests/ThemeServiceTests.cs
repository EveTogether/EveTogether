using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using EveUtils.Client.Theming;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Faction theming: asserts the ThemeService swaps the per-faction tokens live in the application resources
/// and round-trips the persisted choice. Each test resets to Gallente so it never leaks the accent into the other
/// render suites (the App is shared across the assembly).
/// </summary>
public class ThemeServiceTests
{
    private static Color AccentColor() =>
        Application.Current!.TryGetResource("AccentBrush", null, out var v) && v is ISolidColorBrush b
            ? b.Color
            : default;

    private static Color SystemAccent() =>
        Application.Current!.TryGetResource("SystemAccentColor", null, out var v) && v is Color c ? c : default;

    [AvaloniaFact]
    public void Default_Is_Gallente_Green()
    {
        Assert.Equal(Color.Parse("#FF4EC79E"), AccentColor());
    }

    [AvaloniaFact]
    public void Apply_SwapsAccent_PerFaction_Then_ResetsToGallente()
    {
        using var instance = TestClientInstance.Create();
        var theme = instance.Services.GetRequiredService<IThemeService>();
        try
        {
            theme.Apply(FactionTheme.Amarr);
            Assert.Equal(FactionTheme.Amarr, theme.Current);
            Assert.Equal(Color.Parse("#FFE3B341"), AccentColor());
            Assert.Equal(Color.Parse("#FFE3B341"), SystemAccent());

            theme.Apply(FactionTheme.Caldari);
            Assert.Equal(Color.Parse("#FF4F9BD9"), AccentColor());

            theme.Apply(FactionTheme.Minmatar);
            Assert.Equal(Color.Parse("#FFCB4D3E"), AccentColor());
        }
        finally
        {
            theme.Apply(FactionTheme.Gallente);
            Assert.Equal(Color.Parse("#FF4EC79E"), AccentColor());
        }
    }

    [AvaloniaFact]
    public async Task InitializeAsync_Applies_PersistedFaction()
    {
        using var instance = TestClientInstance.Create();
        var settings = instance.Services.GetRequiredService<ISettingRepository>();
        await settings.UpsertAsync(ThemeService.SettingKey, "caldari");

        var theme = new ThemeService(settings);
        try
        {
            await theme.InitializeAsync();
            Assert.Equal(FactionTheme.Caldari, theme.Current);
            Assert.Equal(Color.Parse("#FF4F9BD9"), AccentColor());
        }
        finally
        {
            theme.Apply(FactionTheme.Gallente);
        }
    }

    [AvaloniaFact]
    public async Task Apply_Persists_Choice()
    {
        using var instance = TestClientInstance.Create();
        var settings = instance.Services.GetRequiredService<ISettingRepository>();
        var theme = instance.Services.GetRequiredService<IThemeService>();
        try
        {
            theme.Apply(FactionTheme.Minmatar);

            // The write is fire-and-forget; give it a moment to land, then assert it round-trips.
            string? saved = null;
            for (var i = 0; i < 50 && saved != "minmatar"; i++)
            {
                saved = (await settings.ListAsync()).FirstOrDefault(s => s.Key == ThemeService.SettingKey)?.Value;
                if (saved != "minmatar") await Task.Delay(20);
            }
            Assert.Equal("minmatar", saved);
        }
        finally
        {
            theme.Apply(FactionTheme.Gallente);
        }
    }
}
