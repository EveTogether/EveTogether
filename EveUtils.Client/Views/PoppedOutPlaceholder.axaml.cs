using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views;

/// <summary>A tab's stand-in while its module is popped out (ET-111); the map has its own (ET-396).</summary>
public partial class PoppedOutPlaceholder : UserControl
{
    public PoppedOutPlaceholder() => AvaloniaXamlLoader.Load(this);
}

/// <summary>What the placeholder shows and its two ways back to the module.</summary>
public sealed record PoppedOutPlaceholderModel(string TabTitle, ICommand PutBackCommand, ICommand ShowWindowCommand)
{
    public string Heading => $"{TabTitle} is open in its own window";
}
