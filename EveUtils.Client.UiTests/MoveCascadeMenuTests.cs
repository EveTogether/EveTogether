using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.ViewModels;
using Xunit;

namespace EveUtils.Client.UiTests;

public class MoveCascadeMenuTests
{
    [AvaloniaFact]
    public void NestedMoveCascade_BindsLabelsAtDepth()
    {
        using var _ = TestClientInstance.Create(); // brings the App (with the global MoveMenuItemTheme) up

        var squad1 = new MoveTargetViewModel("Squad 1", [], new RelayCommand(() => { }));
        var squad2 = new MoveTargetViewModel("Squad 2", [], new RelayCommand(() => { }));
        var wing = new MoveTargetViewModel("Wing 1", [squad1, squad2], null);
        var fleet = new MoveTargetViewModel("Fleet Commander", [], new RelayCommand(() => { }));
        var roots = new List<MoveTargetViewModel> { fleet, wing };

        Assert.True(Application.Current!.TryGetResource("MoveMenuItemTheme", null, out var themeObj));
        var theme = (ControlTheme)themeObj!;

        var root = new MenuItem { Header = "Move to…", ItemsSource = roots, ItemContainerTheme = theme };
        var menu = new Menu();
        menu.Items.Add(root);
        var window = new Window { Content = menu, Width = 320, Height = 220 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        root.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();

        // Level 1 binds the label (this already worked).
        var wingItem = (MenuItem)root.ContainerFromIndex(1)!;
        Assert.Equal("Wing 1", wingItem.Header);

        // Level 2 — the regression: was the view-model object (→ ToString), must be the label string.
        wingItem.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        var squadItem = (MenuItem)wingItem.ContainerFromIndex(0)!;
        Assert.Equal("Squad 1", squadItem.Header);

        window.Close();
    }
}
