using Avalonia;
using Avalonia.Controls;

namespace EveUtils.Client.Views.Shared;

/// <summary>
/// One row of a character list: hex portrait, name, a detail line and a selection mark (ET-184). Extracted out of
/// <see cref="CharacterPickerWindow"/> so a second list built from the same <c>CharacterPickRowViewModel</c> rows —
/// the SKILLS header character choice (ET-16) — renders identically instead of drifting from a copy-paste.
/// </summary>
public partial class CharacterPickRowView : UserControl
{
    /// <summary>Switches the trailing mark between a checkbox (many) and a radio dot (one). Set by the host list,
    /// which owns the selection mode.</summary>
    public static readonly StyledProperty<bool> IsMultiSelectProperty =
        AvaloniaProperty.Register<CharacterPickRowView, bool>(nameof(IsMultiSelect));

    public bool IsMultiSelect
    {
        get => GetValue(IsMultiSelectProperty);
        set => SetValue(IsMultiSelectProperty, value);
    }

    public CharacterPickRowView() => InitializeComponent();
}
