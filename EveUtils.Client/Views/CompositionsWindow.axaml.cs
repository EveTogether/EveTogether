using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>The Fleet Compositions library window. A <see cref="ChromedWindow"/> like the other feature
/// modules, so the shell hosts it docked or floating.</summary>
public partial class CompositionsWindow : ChromedWindow
{
    public CompositionsWindow() => AvaloniaXamlLoader.Load(this);

    public CompositionsWindow(CompositionsViewModel viewModel) : this() => DataContext = viewModel;
}
