using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels.Killmails;

namespace EveUtils.Client.Views;

/// <summary>
/// The KILLMAILS overview (ET-332), hosted like RUNS: a docked tab when docked, its own window when floating.
/// No close button of its own, like <c>RunsWindow</c> — a docked tab closes by its own X, a floating window by the
/// chrome's, so this is not an <c>IHostableModuleWindow</c>.
/// </summary>
public partial class KillmailsWindow : ChromedWindow
{
    public KillmailsWindow() => AvaloniaXamlLoader.Load(this);

    public KillmailsWindow(KillmailsOverviewViewModel viewModel) : this() => DataContext = viewModel;
}
