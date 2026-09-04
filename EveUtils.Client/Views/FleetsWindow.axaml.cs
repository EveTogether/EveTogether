using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>
/// The fleet overview (ET-170): the band with one lane per own character over the fleets in three bands. Opened
/// non-modal from the main window as a module, so its content is docked as a tab or shown in this window — the
/// same content either way. Title is static (set in XAML) — no ElementName bug here.
/// </summary>
public partial class FleetsWindow : ChromedWindow
{
    public FleetsWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // The content root reports the width it is actually given — as a docked tab that is the module host's
        // column, as a window it is the window — and the view model answers with the table's state and the band's
        // density. Subscribed on the root and not on the window because the host lifts the root out of the window
        // (ET-42): a subscription here follows the content wherever it is parented.
        if (this.FindControl<DockPanel>("OverviewRoot") is { } root)
            root.GetObservable(BoundsProperty).Subscribe(new WidthObserver(root));
    }

    public FleetsWindow(FleetsViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose(); // release the fleet.changed subscription when the window closes
    }

    private sealed class WidthObserver(Control root) : IObserver<Rect>
    {
        public void OnNext(Rect bounds) => (root.DataContext as FleetsViewModel)?.ApplyWidth(bounds.Width);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
