using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels.Runs;

namespace EveUtils.Client.Views;

/// <summary>
/// One saved activity, fully expanded (ET-162). Hosted like the other feature modules: a docked tab when docked,
/// its own window when floating — the same <c>Content</c> either way, which is why the layout sizes off the space
/// it is given rather than off the window.
///
/// Not an <c>IHostableModuleWindow</c>: the screen carries no close button of its own, so a docked tab is closed by
/// its own X and a floating one by the chrome's, and there is nothing left for that seam to do here.
/// </summary>
public partial class ActivityDetailWindow : ChromedWindow
{
    public ActivityDetailWindow() => AvaloniaXamlLoader.Load(this);

    public ActivityDetailWindow(ActivityDetailViewModel viewModel) : this() => DataContext = viewModel;
}
