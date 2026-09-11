using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs.Sections;

/// <summary>The run window's BOUNTY body (ET-236).</summary>
public partial class BountyWindowSectionView : UserControl
{
    public BountyWindowSectionView() => AvaloniaXamlLoader.Load(this);
}
