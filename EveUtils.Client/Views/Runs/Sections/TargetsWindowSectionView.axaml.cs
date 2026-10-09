using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs.Sections;

/// <summary>The run window's TARGETS body (ET-369).</summary>
public partial class TargetsWindowSectionView : UserControl
{
    public TargetsWindowSectionView() => AvaloniaXamlLoader.Load(this);
}
