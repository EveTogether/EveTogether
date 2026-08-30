using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Theming;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi;
using Material.Icons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The interface's icons (ET-74). Every symbol used to be a Unicode character, which meant the shipped font decided
/// what it looked like — that is how ⬒ arrived on screen with the wrong half filled. They come from Material Design
/// Icons now; these pin down the two properties that broke while they were being introduced and that no amount of
/// assertion elsewhere would have caught.
/// </summary>
public class IconSetTests
{
    /// <summary>The module rail. Ten icons that size themselves are ten different weights, which is the complaint
    /// that opened ET-73 — the size belongs to the rail, not to each button.</summary>
    [AvaloniaFact]
    public void RailIcons_ShareOneSize_AndEveryOneDrawsSomething()
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        var window = new MainWindow { DataContext = new MainWindowViewModel(), Width = 1100, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var icons = window.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.Classes.Contains("railitem"))
            .Select(b => b.GetVisualDescendants().OfType<MaterialIcon>().Single())
            .ToList();

        Assert.True(icons.Count >= 10, $"the rail should still carry its launcher icons, found {icons.Count}");
        Assert.All(icons, icon => Assert.NotNull(icon.Kind));
        Assert.All(icons, icon => Assert.Equal(icons[0].Bounds.Size, icon.Bounds.Size));

        window.Close();
    }

    /// <summary>A chip's icon has to carry the chip's state colour. It gets that by inheritance, and inheritance is
    /// exactly what the stock Fluent theme intercepts one level down — the pinned overlay tack rendered near-white on
    /// its accent square for the same reason. An amber chip with resting-green ink would look like a healthy session.
    /// </summary>
    [AvaloniaFact]
    public void AWarningChip_TintsItsIcon_NotOnlyItsLabel()
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        var vm = new MainWindowViewModel();
        vm.Characters.Add(new CharacterViewModel(new Character("Jithran", 100))
        {
            EsiTokenStatus = TokenStatus.NeedsReauth,
        });
        vm.Characters.Add(new CharacterViewModel(new Character("Lyra Custos", 200))
        {
            EsiTokenStatus = TokenStatus.Valid,
        });

        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 900 };
        window.Show();
        for (var i = 0; i < 10; i++)
            Dispatcher.UIThread.RunJobs();

        // The ESI chip specifically: a character row also carries a (hidden) implant chip, which is in the tree too.
        var chips = window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("chip") && b.DataContext is CharacterViewModel
                        && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "ESI"))
            .Select(b => (Row: (CharacterViewModel)b.DataContext!, Ink: Ink(b)))
            .ToList();

        // The amber the .warn chip is themed in, not merely "something other than the healthy chip": falling back to
        // the chip's resting accent would also differ from the healthy one, and would still be the wrong colour.
        var warned = chips.Single(c => c.Row.CharacterId == 100);
        var healthy = chips.Single(c => c.Row.CharacterId == 200);
        Assert.Equal(Color.Parse("#FFE0B341"), warned.Ink);
        Assert.NotEqual(warned.Ink, healthy.Ink);

        window.Close();
    }

    /// <summary>The colour the chip's icon actually resolves to, read off the icon rather than off the chip — a rule
    /// that never reached the icon still leaves the chip itself looking correct.</summary>
    private static Color Ink(Border chip) =>
        Assert.IsType<ISolidColorBrush>(
            chip.GetVisualDescendants().OfType<MaterialIcon>().Single().Foreground, exactMatch: false).Color;
}
