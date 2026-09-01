using System;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.Views;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Docked is the default shell: <see cref="ModuleHostService"/> does not show a module's own window, it lifts
/// <c>window.Content</c> out and reparents it into a tab in the main window. Anything left on the window — its
/// <c>Styles</c>, above all — is left behind, so a screen's own styles have to live on its content root. These
/// windows each carry a style block; every one of them is checked in both shells (ET-42).
/// </summary>
public class DockedWindowStylesTests
{
    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating => false;
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    public enum Shell
    {
        OwnWindow,
        DockedTab
    }

    // The control tree a user actually sees: the window itself when floating, the reparented content when docked.
    private static Control Present(Window window, Shell shell)
    {
        Control content;
        if (shell is Shell.DockedTab)
        {
            var display = new FakeDisplay();
            var host = new ModuleHostService();
            host.SetOwner(new Window());
            host.SetHost(display);
            host.Open(window, "MODULE", "test", "module");

            // The module's own window is deliberately not the host — stand the stolen content in a plain one.
            content = (Control)Assert.Single(display.HostTabs).Content!;
            var root = new Window { Width = 900, Height = 700, Content = content };
            root.Show();
        }
        else
        {
            content = (Control)window.Content!;
            window.Show();
        }

        Dispatcher.UIThread.RunJobs();
        return content;
    }

    // A control the screen's own styles must reach, put into the content root and read back after layout.
    private static T Probe<T>(Control content, T probe) where T : Control
    {
        ((Panel)content).Children.Add(probe);
        Dispatcher.UIThread.RunJobs();
        return probe;
    }

    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public void FleetsWindow_StatePill_KeepsItsStyle(Shell shell)
    {
        var content = Present(new FleetsWindow(), shell);
        var pill = Probe(content, new Border { Classes = { "statepill" } });
        Assert.Equal(new Avalonia.CornerRadius(9), pill.CornerRadius);
    }

    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public void CompositionsWindow_TabItem_KeepsItsStyle(Shell shell)
    {
        var content = Present(new CompositionsWindow(), shell);
        var tab = Probe(content, new TabItem());
        Assert.Equal(new Avalonia.Thickness(14, 7), tab.Padding);
        Assert.Equal(0, tab.MinHeight);
    }

    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public void FitDetailWindow_PulseGauge_KeepsItsStyle(Shell shell)
    {
        // The pulse style only carries an animation, so this asserts the style is there to be matched at all rather
        // than a property it sets: an empty Styles collection on the content root is exactly the ET-42 regression.
        var content = Present(new FitDetailWindow(), shell);
        Assert.NotEmpty(content.Styles);
    }
}
