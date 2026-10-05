using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Pairing;

namespace EveUtils.Client.Views;

/// <summary>
/// Couple-server dialog: server address + optional label. Returns null on cancel. Shows the
/// server's own name live: an unauthenticated, accept-any-cert probe runs on open and (debounced)
/// on every address change — display-only; real trust is established via TOFU at pairing.
/// </summary>
public partial class CoupleServerWindow : ChromedWindow
{
    private readonly DebouncedServerProbe<string>? _probe;

    public CoupleServerWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public CoupleServerWindow(Func<string, CancellationToken, Task<string?>> probeServerName, CoupleServerResult? prefill = null)
        : this()
    {
        _probe = new DebouncedServerProbe<string>(probeServerName,
            onChecking: () => SetServerNameText("checking…"),
            onCleared: () => SetServerNameText(""),
            onResult: (_, name) => SetServerNameText(string.IsNullOrWhiteSpace(name) ? "(server not reachable)" : $"Server: {name}"));

        // Restoring a coupling the client already knows: fill in what it knows rather than asking for it again, so
        // the only steps left are connect and sign in (ET-123).
        if (prefill is not null)
        {
            this.FindControl<TextBox>("AddressBox")!.Text = prefill.Address;
            this.FindControl<TextBox>("LabelBox")!.Text = prefill.Label ?? "";
            var hint = this.FindControl<TextBlock>("IntroBlock");
            if (hint is not null)
                hint.Text = "Filled in from the existing coupling — just sign in again as this character.";
        }

        this.FindControl<TextBox>("AddressBox")!.TextChanged += OnAddressChanged;

        Opened += (_, _) => _ = _probe.ProbeNowAsync(this.FindControl<TextBox>("AddressBox")?.Text);   // initial probe with the default address
        Closed += (_, _) => _probe.Dispose();
    }

    private void OnAddressChanged(object? sender, TextChangedEventArgs e) =>
        _probe?.AddressChanged(this.FindControl<TextBox>("AddressBox")?.Text);

    private void SetServerNameText(string text)
    {
        var block = this.FindControl<TextBlock>("ServerNameBlock");
        if (block is not null) block.Text = text;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var address = this.FindControl<TextBox>("AddressBox")?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(address))
            return; // no address — keep the dialog open instead of returning an empty result

        var label = this.FindControl<TextBox>("LabelBox")?.Text?.Trim();
        Close(new CoupleServerResult(address, string.IsNullOrWhiteSpace(label) ? null : label));
    }
}
