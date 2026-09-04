using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels.Runs;

namespace EveUtils.Client.Views;

/// <summary>
/// The runs screen (ET-161), hosted like the other feature modules: a docked tab when docked, its own window when
/// floating — the same <c>Content</c> either way, which is why the layout sizes off the space it is given.
///
/// Not an <c>IHostableModuleWindow</c>: the screen carries no close button of its own, so a docked tab is closed by
/// its own X and a floating one by the chrome's. The view-model is disposed on close because its lane clock is a
/// timer, and a timer that outlives its window keeps ticking for the rest of the session.
/// </summary>
public partial class RunsWindow : ChromedWindow
{
    public RunsWindow() => AvaloniaXamlLoader.Load(this);

    public RunsWindow(RunsOverviewViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }
}
