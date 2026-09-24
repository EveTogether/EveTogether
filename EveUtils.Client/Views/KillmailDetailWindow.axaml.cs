using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels.Killmails;

namespace EveUtils.Client.Views;

/// <summary>
/// The detail screen of one killmail (ET-333), hosted like ACTIVITY and FIT DETAIL: a docked tab when docked, its
/// own window when floating. No close button of its own — a docked tab closes by its own X, a floating window by
/// the chrome's, so this is not an <c>IHostableModuleWindow</c>.
/// </summary>
public partial class KillmailDetailWindow : ChromedWindow
{
    public KillmailDetailWindow() => AvaloniaXamlLoader.Load(this);

    public KillmailDetailWindow(KillmailDetailViewModel viewModel) : this() => DataContext = viewModel;
}
