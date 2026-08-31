using System;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Messaging;
using Material.Icons;

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
        ServerConnectionState.Connected      => "connected",
        ServerConnectionState.Connecting     => "connecting…",
        ServerConnectionState.Reconnecting   => "reconnecting…",
        ServerConnectionState.SessionExpired => "session expired — re-pair",
        _                                    => "disconnected"
    };

    /// <summary>Amber chip: the link is not healthy, but nothing the user has to act on — it is dropped or coming
    /// back by itself. Mutually exclusive with <see cref="HasExpired"/> so the two style variants never stack.</summary>
    public bool HasIssue => State is ServerConnectionState.Reconnecting
                                  or ServerConnectionState.Disconnected;

    /// <summary>Red chip: the pairing itself is no longer valid and only the user can fix it (re-pair). Shown as soon
    /// as the app knows — the 30s heartbeat finds an access token the server refuses and cannot silently refresh
    /// (ET-77) — rather than leaving the user to discover it on a save that comes back "Not authenticated".</summary>
    public bool HasExpired => State is ServerConnectionState.SessionExpired;

    /// <summary>The chip's icon for the character card: a cloud when healthy, a struck-through cloud when the pairing
    /// has lapsed, a warning on any other issue. These were emoji in the label text (☁️ / ⚠️), which Windows draws in
    /// full colour out of a separate font — two bright pictograms beside 9.5px grey text (ET-74).</summary>
    public MaterialIconKind ChipIcon => State switch
    {
        ServerConnectionState.SessionExpired => MaterialIconKind.CloudOffOutline,
        _ when HasIssue                      => MaterialIconKind.AlertOutline,
        _                                    => MaterialIconKind.CloudOutline
    };

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
        OnPropertyChanged(nameof(HasExpired));
        OnPropertyChanged(nameof(ChipIcon));
        OnPropertyChanged(nameof(LinkTooltip));
    }

    partial void OnDisplayNameChanged(string value)
    {
        OnPropertyChanged(nameof(LinkTooltip));
    }
}
