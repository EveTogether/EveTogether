using System;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Messaging;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One coupling of a character to a server: the server's display name, the live bus
/// connection state for this server, a per-link Decouple command and a per-link "view trust" command
/// (gear button → the server info/trust dialog). A character holds one of these per server it is coupled
/// to (shown inside the character dialog).
/// </summary>
public partial class ServerLinkViewModel : ObservableObject
{
    public int CharacterId { get; }
    public string Address { get; }

    [ObservableProperty] private string _displayName;
    [ObservableProperty] private ServerConnectionState _state;

    public ICommand DecoupleCommand { get; }
    public ICommand ViewTrustCommand { get; }

    public string StatusLabel => State switch
    {
        ServerConnectionState.Connected      => "☁️ connected",
        ServerConnectionState.Connecting     => "☁️ connecting…",
        ServerConnectionState.Reconnecting   => "⚠️ reconnecting…",
        ServerConnectionState.SessionExpired => "⚠️ session expired — re-pair",
        _                                    => "⚠️ disconnected"
    };

    /// <summary>True when the link is not in a healthy connected/connecting state — drives the warning badge.</summary>
    public bool HasIssue => State is ServerConnectionState.Reconnecting
                                  or ServerConnectionState.SessionExpired
                                  or ServerConnectionState.Disconnected;

    /// <summary>Compact per-server icon for the character list: synced when healthy, warning on issue.</summary>
    public string Icon => HasIssue ? "⚠️" : "☁️";

    /// <summary>Mockup-style chip text for the character card (e.g. "ET"): status glyph + the server's display name.</summary>
    public string ChipText => $"{(HasIssue ? "⚠" : "☁")} {DisplayName}";

    /// <summary>Tooltip shown when hovering the per-server icon: server name + live status.</summary>
    public string LinkTooltip => $"{DisplayName} — {StatusLabel}";

    public ServerLinkViewModel(
        int characterId, string address, string displayName, ServerConnectionState state,
        Func<ServerLinkViewModel, Task> onDecouple, Func<ServerLinkViewModel, Task>? onViewTrust = null)
    {
        CharacterId = characterId;
        Address = address;
        _displayName = displayName;
        _state = state;
        DecoupleCommand = new AsyncRelayCommand(() => onDecouple(this));
        ViewTrustCommand = new AsyncRelayCommand(() => onViewTrust?.Invoke(this) ?? Task.CompletedTask);
    }

    partial void OnStateChanged(ServerConnectionState value)
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(HasIssue));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(ChipText));
        OnPropertyChanged(nameof(LinkTooltip));
    }

    partial void OnDisplayNameChanged(string value)
    {
        OnPropertyChanged(nameof(ChipText));
        OnPropertyChanged(nameof(LinkTooltip));
    }
}
