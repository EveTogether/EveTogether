using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs.Sections;

/// <summary>The run window's HOMEFRONT body (ET-230).</summary>
public partial class HomefrontWindowSectionView : UserControl
{
    public HomefrontWindowSectionView() => AvaloniaXamlLoader.Load(this);
}
