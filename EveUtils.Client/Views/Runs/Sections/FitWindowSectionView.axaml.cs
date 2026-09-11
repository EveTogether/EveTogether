using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs.Sections;

/// <summary>The run window's FIT body (ET-236).</summary>
public partial class FitWindowSectionView : UserControl
{
    public FitWindowSectionView() => AvaloniaXamlLoader.Load(this);
}
