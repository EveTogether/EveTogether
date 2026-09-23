using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Messaging;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Transport;

namespace EveUtils.Client.ViewModels.Home;

public enum AttentionKind
{
    Skills,
    Esi,
    Server
}

/// <summary>One thing that needs the pilot: what, about whom, and the one action that fixes it.</summary>
public sealed partial class HomeAttentionLineViewModel(
    string key, AttentionKind kind, string subject, string text, string? actionText, Action? act, Action<HomeAttentionLineViewModel> dismiss)
    : ObservableObject
{
    /// <summary>Identifies the line and the state it reports: ✕ hides it until that state changes.</summary>
    public string Key { get; } = key;

    public AttentionKind Kind { get; } = kind;

    public string KindText { get; } = kind.ToString().ToUpperInvariant();

    public bool IsServer => Kind == AttentionKind.Server;

    public string Subject { get; } = subject;

    public string Text { get; } = text;

    public string? ActionText { get; } = actionText;

    public bool HasAction => ActionText is not null;

    [RelayCommand]
    private void Act() => act?.Invoke();

    [RelayCommand]
    private void Dismiss() => dismiss(this);
}

/// <summary>
/// The attention band (ET-324): exists only when something needs the pilot — a skill queue that stopped, an ESI sign-in
/// that has to be done again, a coupled server that cannot be reached. A scope the pilot chose not to share never lands
/// here: that is a choice, shown quietly beside the field. Everything is already live elsewhere; nothing is read here.
/// </summary>
public sealed partial class HomeAttentionViewModel : ObservableObject, IDisposable
{
    private readonly HomePilotsViewModel _pilots;
    private readonly IRemoteBusConnector? _bus;
    private readonly HomeNavigation _navigation;
    private readonly HashSet<string> _dismissed = [];
    private readonly Dictionary<string, DateTime> _unreachableSince = [];
    private readonly Dictionary<string, string> _serverNames = [];
    private readonly IServerRegistry? _servers;

    public HomeAttentionViewModel(HomePilotsViewModel pilots, IRemoteBusConnector? bus, IServerRegistry? servers,
        HomeNavigation navigation)
    {
        _pilots = pilots;
        _bus = bus;
        _servers = servers;
        _navigation = navigation;
        _pilots.QueuesChanged += Show;
        _pilots.TokensChanged += Show;
        _pilots.Rows.CollectionChanged += _OnRowsChanged;
        if (_bus is not null)
            _bus.StateChanged += _OnBusStateChanged;
        Show();
    }

    public ObservableCollection<HomeAttentionLineViewModel> Lines { get; } = [];

    [ObservableProperty] private bool _hasLines;

    /// <summary>Called whenever a source moves: a queue read, a token status, a server state. In-memory only.</summary>
    public void Show()
    {
        List<HomeAttentionLineViewModel> lines = [];
        foreach (HomePilotRowViewModel row in _pilots.Rows)
        {
            if (row.SharesQueue && row.Queue is { IsPaused: true } queue)
                lines.Add(_Line($"skills:{row.CharacterId}:{queue.SkillText}:{queue.Queued}", AttentionKind.Skills, row.Character.Name,
                    $"skill queue paused, {queue.Queued} skill{(queue.Queued == 1 ? "" : "s")} waiting ({queue.SkillText} first). "
                    + "It resumes when the character logs in.", null, null));

            if (row.Character.EsiTokenStatus == TokenStatus.NeedsReauth)
            {
                int characterId = row.CharacterId;
                lines.Add(_Line($"esi:{characterId}", AttentionKind.Esi, row.Character.Name,
                    "ESI refused the refresh token. ESI reads (location gaps, ship, skills, fleet sync) are paused until it signs in again.",
                    "RE-AUTHORIZE", () => _ = _navigation.ReAuthorize(characterId)));
            }
        }

        foreach ((string address, DateTime since) in _unreachableSince)
            lines.Add(_Line($"server:{address}", AttentionKind.Server, _serverNames.GetValueOrDefault(address, address),
                $"not reachable since {since.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}. Runs keep saving on "
                + "this PC; shared fits and fleets show their last known state.",
                "SERVER SETTINGS", () => _navigation.LaunchModule("settings")));

        List<HomeAttentionLineViewModel> shown = [.. lines.Where(line => !_dismissed.Contains(line.Key))
            .Select(line => Lines.FirstOrDefault(existing => existing.Key == line.Key) ?? line)];
        // A dismissed state that no longer holds may warn again the next time it happens.
        _dismissed.IntersectWith(lines.Select(line => line.Key));
        Lines.ReconcileTo(shown);
        HasLines = shown.Count > 0;
    }

    private HomeAttentionLineViewModel _Line(string key, AttentionKind kind, string subject, string text, string? action, Action? act) =>
        new(key, kind, subject, text, action, act, _Dismiss);

    private void _Dismiss(HomeAttentionLineViewModel line)
    {
        _dismissed.Add(line.Key);
        Show();
    }

    private void _OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Show();

    /// <summary>Only a link that was up and dropped is news: Connecting at startup, or a pairing the shell's own banners
    /// already explain (expired session, refused certificate), is not repeated here.</summary>
    private void _OnBusStateChanged(string address, ServerConnectionState state) =>
        Dispatcher.UIThread.Post(() => _ = _ShowServerAsync(address, state));

    private async Task _ShowServerAsync(string address, ServerConnectionState state)
    {
        bool isDown = state is ServerConnectionState.Reconnecting or ServerConnectionState.Disconnected;
        if (isDown)
            _unreachableSince.TryAdd(address, DateTime.UtcNow);
        else
            _unreachableSince.Remove(address);
        if (isDown && _servers is not null && !_serverNames.ContainsKey(address))
            _serverNames[address] = await Task.Run(() => _servers.DisplayNameAsync(address));
        Show();
    }

    public void Dispose()
    {
        _pilots.QueuesChanged -= Show;
        _pilots.TokensChanged -= Show;
        _pilots.Rows.CollectionChanged -= _OnRowsChanged;
        if (_bus is not null)
            _bus.StateChanged -= _OnBusStateChanged;
    }
}
