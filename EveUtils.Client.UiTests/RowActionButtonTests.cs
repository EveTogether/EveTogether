using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Messaging;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Location;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Buttons that share the end of a list row (ET-82). An icon button and a text button take their height from what
/// they happen to contain — a 14px icon against ~12px of 9pt text — so without a shared rule they never line up by
/// themselves, which is what the coupled-servers row showed.
/// </summary>
public class RowActionButtonTests
{
    [AvaloniaFact]
    public async Task ACoupledServerRow_GivesItsGearAndDecoupleButtonsTheSameHeight()
    {
        using var instance = TestClientInstance.Create();
        var owner = new MainWindowViewModel(instance.Services);
        var dialog = new CharacterDialogViewModel(owner,
            new CharacterViewModel(new Character("RaymondKrah", 90250177,
                [LocationScopeCatalog.ReadLocation])));
        await dialog.InitializeAsync();

        dialog.ServerLinks.Add(new ServerLinkViewModel(dialog.CharacterId, "https://eve.local", "eve.local",
            ServerConnectionState.Connected, _ => Task.CompletedTask));

        var window = new CharacterWindow(dialog);
        window.Show();
        for (var i = 0; i < 10; i++)
            Dispatcher.UIThread.RunJobs();

        var buttons = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.DataContext is ServerLinkViewModel)
            .ToList();

        Assert.Equal(2, buttons.Count);
        Assert.All(buttons, button => Assert.Equal(buttons[0].Bounds.Height, button.Bounds.Height));

        // The height comes from the row's rule rather than from either button's content, so it is above both.
        Assert.True(buttons[0].Bounds.Height >= 24,
            $"the row's buttons should carry the shared height, measured {buttons[0].Bounds.Height}");

        // A shared height only reads as one row while the content still sits in the middle of it: a button grown
        // past what it contains would otherwise pin its icon or its label to the top edge.
        Assert.All(buttons, button =>
        {
            Visual content = button.GetVisualDescendants().OfType<ContentPresenter>().First();
            double slack = button.Bounds.Height - content.Bounds.Height;
            Assert.Equal(slack / 2, content.Bounds.Y, 1);
        });

        window.Close();
    }
}
