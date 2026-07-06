using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;

namespace EveUtils.Client.Views;

/// <summary>
/// Couple-server dialog: server address + optional label. Returns null on cancel. Shows the
/// server's own name live: an unauthenticated, accept-any-cert probe runs on open and (debounced)
/// on every address change — display-only; real trust is established via TOFU at pairing.
/// </summary>
public partial class CoupleServerWindow : ChromedWindow
{
    private readonly Func<string, CancellationToken, Task<string?>>? _probeServerName;
    private readonly DispatcherTimer? _debounce;
    private CancellationTokenSource? _probeCts;

    public CoupleServerWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public CoupleServerWindow(Func<string, CancellationToken, Task<string?>> probeServerName) : this()
    {
        _probeServerName = probeServerName;

        this.FindControl<TextBox>("AddressBox")!.TextChanged += OnAddressChanged;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _debounce.Tick += (_, _) => { _debounce!.Stop(); _ = ProbeAsync(); };

        Opened += (_, _) => _ = ProbeAsync();      // initial probe with the default address
        Closed += (_, _) => { _debounce?.Stop(); _probeCts?.Cancel(); };
    }

    private void OnAddressChanged(object? sender, TextChangedEventArgs e)
    {
        SetServerNameText("checking…");
        _debounce?.Stop();
        _debounce?.Start(); // restart the window — only the last keystroke probes
    }

    private async Task ProbeAsync()
    {
        if (_probeServerName is null) return;
        var address = this.FindControl<TextBox>("AddressBox")?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(address)) { SetServerNameText(""); return; }

        _probeCts?.Cancel();
        _probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = _probeCts.Token;
        SetServerNameText("checking…");
        try
        {
            var name = await _probeServerName(address, ct);
            if (ct.IsCancellationRequested) return; // a newer probe superseded this one
            SetServerNameText(string.IsNullOrWhiteSpace(name) ? "(server not reachable)" : $"Server: {name}");
        }
        catch (OperationCanceledException)
        {
            // superseded or timed out — leave whatever the newer probe sets
        }
        catch (Exception)
        {
            SetServerNameText("(server not reachable)");
        }
    }

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
