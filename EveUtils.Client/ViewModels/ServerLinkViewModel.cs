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

    /// <summary>Couple this character to this server again, from the link itself. The only two actions here used to
    /// be decouple and view-trust, so a link the user was being asked to repair offered no way to repair it — they
    /// had to know to go and find "Couple to server" for themselves.</summary>
    public ICommand RecoupleCommand { get; }

    public string StatusLabel => State switch
    {
        ServerConnectionState.Connected           => "connected",
        ServerConnectionState.Connecting          => "connecting…",
        ServerConnectionState.Reconnecting        => "reconnecting…",
        ServerConnectionState.SessionExpired      => "sign-in refused — retrying",
        // Says what the user has to do, not what the app is doing: there is nothing left running to wait for.
        ServerConnectionState.SessionGone         => "session ended — couple again",
        ServerConnectionState.CertificateRejected => "certificate changed — check and re-pair",
        _                                         => "disconnected"
    };

    /// <summary>Whether the only way back is the user coupling again — which is also when the recouple action is
    /// worth offering. Kept off the certificate case on purpose: there the user has a fingerprint to check against
    /// the server first, and a one-click re-pair would invite them past exactly that step (ET-95).</summary>
    public bool CanRecouple => State is ServerConnectionState.SessionGone;

    /// <summary>Amber chip: the link is not healthy, but nothing the user has to act on — it is dropped or coming
    /// back by itself. Mutually exclusive with <see cref="HasExpired"/> so the two style variants never stack.</summary>
    public bool HasIssue => State is ServerConnectionState.Reconnecting
                                  or ServerConnectionState.Disconnected;

    /// <summary>Red chip: nothing this coupling is for works right now. Either the server refuses the stored session
    /// and will not renew it — the 30s heartbeat finds an access token it rejects (ET-77) — or the session is gone
    /// from the server altogether (ET-123), or the server presents a certificate the pin refuses (ET-95). Red rather
    /// than amber even though the refused case keeps retrying by itself (ET-121): reads come back EMPTY rather than
    /// failing while it lasts, and a quiet amber would let that pass for "there is nothing here". Red for the gone
    /// case for the plainer reason that it stays broken until the user does something.</summary>
    public bool HasExpired => State is ServerConnectionState.SessionExpired
                                    or ServerConnectionState.SessionGone
                                    or ServerConnectionState.CertificateRejected;

    /// <summary>The chip's icon for the character card: a cloud when healthy, a struck-through cloud when the pairing
    /// has lapsed, a warning on any other issue. These were emoji in the label text (☁️ / ⚠️), which Windows draws in
    /// full colour out of a separate font — two bright pictograms beside 9.5px grey text (ET-74).</summary>
    public MaterialIconKind ChipIcon => State switch
    {
        ServerConnectionState.SessionExpired      => MaterialIconKind.CloudOffOutline,
        // A different cloud from the struck-through one: refused-but-retrying and gone-for-good are two states the
        // user answers differently, and giving them one icon would flatten that back out.
        ServerConnectionState.SessionGone         => MaterialIconKind.CloudRemoveOutline,
        // A cert that no longer matches is a trust question, not an expiry — the shield says which of the two it is.
        ServerConnectionState.CertificateRejected => MaterialIconKind.ShieldAlertOutline,
        _ when HasIssue                           => MaterialIconKind.AlertOutline,
        _                                         => MaterialIconKind.CloudOutline
    };

    /// <summary>Tooltip shown when hovering the per-server icon: server name + live status.</summary>
    public string LinkTooltip => $"{DisplayName} — {StatusLabel}";

    public ServerLinkViewModel(
        int characterId, string address, string displayName, ServerConnectionState state,
        Func<ServerLinkViewModel, Task> onDecouple, Func<ServerLinkViewModel, Task>? onViewTrust = null,
        Func<ServerLinkViewModel, Task>? onRecouple = null)
    {
        CharacterId = characterId;
        Address = address;
        _displayName = displayName;
        _state = state;
        DecoupleCommand = new AsyncRelayCommand(() => onDecouple(this));
        ViewTrustCommand = new AsyncRelayCommand(() => onViewTrust?.Invoke(this) ?? Task.CompletedTask);
        RecoupleCommand = new AsyncRelayCommand(() => onRecouple?.Invoke(this) ?? Task.CompletedTask);
    }

    partial void OnStateChanged(ServerConnectionState value)
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(HasIssue));
        OnPropertyChanged(nameof(HasExpired));
        OnPropertyChanged(nameof(CanRecouple));
        OnPropertyChanged(nameof(ChipIcon));
        OnPropertyChanged(nameof(LinkTooltip));
    }

    partial void OnDisplayNameChanged(string value)
    {
        OnPropertyChanged(nameof(LinkTooltip));
    }
}
