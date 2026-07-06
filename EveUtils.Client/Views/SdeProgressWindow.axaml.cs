using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>
/// Modal SDE-update popup. Bound to a <see cref="SdeProgressViewModel"/> that the importer reports into; the
/// window closes itself when the VM signals a successful (or already-up-to-date) finish, and stays open showing
/// the error + a Close button on failure.
/// </summary>
public partial class SdeProgressWindow : ChromedWindow
{
    private readonly SdeProgressViewModel? _viewModel;

    public SdeProgressWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public SdeProgressWindow(SdeProgressViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // The import may have finished before the window became visible (a missed CloseRequested); never
        // auto-close on an error, which the user dismisses via the Close button.
        if (_viewModel is { IsFinished: true, IsError: false })
            Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.CloseRequested -= OnCloseRequested;
        base.OnClosed(e);
    }

    private void OnCloseRequested()
    {
        if (IsVisible)
            Close();
    }
}
