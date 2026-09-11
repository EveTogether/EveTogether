using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs.Sections;

/// <summary>The run window's ACTIVITY body (ET-236).</summary>
public partial class ActivityWindowSectionView : UserControl
{
    public ActivityWindowSectionView() => AvaloniaXamlLoader.Load(this);
}
