using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Fleet;

namespace EveUtils.Client.Views;

/// <summary>
/// Add-external-member dialog: a character-id field that, on field-leave, runs a best-effort public-ESI
/// lookup and shows a name/corp/alliance preview before the owner confirms. Returns the verified character id on
/// confirm, null on cancel. Confirm is only enabled once a lookup resolves an existing character.
/// </summary>
public partial class AddExternalMemberWindow : ChromedWindow
{
    private readonly IExternalCharacterLookup _lookup;
    private int _verifiedCharacterId;

    public AddExternalMemberWindow()
    {
        AvaloniaXamlLoader.Load(this);
        // Parameterless ctor is the XAML previewer's; the real one supplies the lookup.
        _lookup = null!;
    }

    public AddExternalMemberWindow(IExternalCharacterLookup lookup) : this()
    {
        _lookup = lookup;
    }

    private async void OnIdLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_lookup is null)
            return;

        var name = this.FindControl<TextBlock>("PreviewName")!;
        var affiliation = this.FindControl<TextBlock>("PreviewAffiliation")!;
        var confirm = this.FindControl<Button>("ConfirmButton")!;
        _verifiedCharacterId = 0;
        confirm.IsEnabled = false;

        var raw = this.FindControl<TextBox>("CharacterIdBox")?.Text?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            name.Text = "Enter an id to preview.";
            affiliation.Text = "";
            return;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            name.Text = "Not a valid character id.";
            affiliation.Text = "";
            return;
        }

        name.Text = "Looking up…";
        affiliation.Text = "";

        var info = await _lookup.LookupAsync(id);
        if (!info.Exists)
        {
            name.Text = "Character not found (or public ESI unreachable).";
            affiliation.Text = "";
            return;
        }

        name.Text = info.Name;
        affiliation.Text = string.Join("  •  ", new[] { info.Corp, info.Alliance }
            .Where(s => !string.IsNullOrEmpty(s)));
        _verifiedCharacterId = id;
        confirm.IsEnabled = true;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (_verifiedCharacterId > 0)
            Close((int?)_verifiedCharacterId);
    }
}
