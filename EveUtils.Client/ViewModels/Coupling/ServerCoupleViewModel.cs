using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Pairing;
using EveUtils.Grpc;
using EveUtils.Shared.Modules.Esi;

namespace EveUtils.Client.ViewModels.Coupling;

/// <summary>
/// Coupling one character to a server, as the setup wizard's server step and the character screen's "Couple to server"
/// both show it (ET-428): pick a known server or type an address, see whether it answers, then couple — with the
/// progress, a Cancel that really stops it, and an error you can retry, all in the window itself. The pairing itself
/// runs through <see cref="MainWindowViewModel.CoupleCharacterAsync"/>.
/// </summary>
public sealed partial class ServerCoupleViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan PairingWindow = TimeSpan.FromMinutes(5);   // the server forgets a pairing after ~5 min

    private readonly MainWindowViewModel _owner;
    private readonly DebouncedServerProbe<ServerScopesResponse> _probe;
    private IReadOnlyList<string> _requiredServerScopes = [];
    private string? _probedServerName;
    private string? _loginLink;
    private CancellationTokenSource? _pairingCts;
    private bool _cancelledByUser;
    private bool _probeUnreachable;
    private bool _explicitTest;
    private bool _unreachableWhileCoupling;
    private bool _labelEditedByUser;
    private bool _fillingLabel;

    public ServerCoupleViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        _probe = new DebouncedServerProbe<ServerScopesResponse>(owner.ProbeServerAsync,
            _OnProbeChecking, _OnProbeCleared, _OnProbeResult);
    }

    /// <summary>Raised once the character is coupled, with the name the server is shown under.</summary>
    public event Action<string>? Coupled;

    /// <summary>"Not now": no server for this character. What follows is the host's to decide.</summary>
    public event Action? NotNowRequested;

    public ObservableCollection<KnownServerOption> KnownServers { get; } = [];
    public ObservableCollection<ScopeChoiceViewModel> ServerScopeChoices { get; } = [];

    public int CharacterId { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(CoupleButtonText), nameof(PairingWaitTitle), nameof(PickAgainText),
        nameof(PairingCancelledText))]
    private string _characterName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecking), nameof(IsUnreachable), nameof(IsPairing), nameof(IsCoupled),
        nameof(IsPairingCancelled), nameof(IsOtherCharacter), nameof(IsPairingFailed), nameof(IsServerReady),
        nameof(ShowServerForm), nameof(ShowLabelAndScopes), nameof(ShowNotNow), nameof(ShowCoupleButton),
        nameof(ShowProbeOk), nameof(ShowProbeBad), nameof(CoupleButtonText))]
    private ServerCoupleState _coupleState;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNewServerFields), nameof(IsServerReady), nameof(ShowLabelAndScopes),
        nameof(ShowCoupleButton), nameof(CoupleButtonText))]
    private bool _isAnotherServer;

    [ObservableProperty] private string _serverAddress = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CoupleButtonText))] private string _serverLabel = "";
    [ObservableProperty] private string _probeText = "";
    [ObservableProperty] private string _pairingError = "";
    [ObservableProperty] private string _otherCharacterText = "";
    [ObservableProperty] private string _coupledTitle = "";
    [ObservableProperty] private string _coupledCertificateText = "";
    [ObservableProperty] private string _fingerprint = "";
    [ObservableProperty] private bool _showFingerprint;

    /// <summary>Which of the pairing's steps is under way, for the waiting box: 1 contacting, 2 browser, 3 waiting.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContacting), nameof(IsContacted), nameof(IsBrowserOpened))]
    private int _pairingPhase;

    public bool IsContacting => PairingPhase <= 1;
    public bool IsContacted => PairingPhase > 1;
    public bool IsBrowserOpened => PairingPhase > 2;

    public bool HasKnownServers => KnownServers.Count > 0;
    public bool ShowNewServerFields => !HasKnownServers || IsAnotherServer;
    public bool IsChecking => CoupleState is ServerCoupleState.Checking;
    public bool IsUnreachable => CoupleState is ServerCoupleState.Unreachable;
    public bool IsPairing => CoupleState is ServerCoupleState.Pairing;
    public bool IsCoupled => CoupleState is ServerCoupleState.Coupled;
    public bool IsPairingCancelled => CoupleState is ServerCoupleState.PairingCancelled;
    public bool IsOtherCharacter => CoupleState is ServerCoupleState.OtherCharacter;
    public bool IsPairingFailed => CoupleState is ServerCoupleState.PairingFailed;
    public bool ShowServerForm => !IsPairing && !IsCoupled;
    public bool ShowProbeOk => _probedServerName is not null && CoupleState is ServerCoupleState.Reachable
        or ServerCoupleState.PairingCancelled or ServerCoupleState.OtherCharacter or ServerCoupleState.PairingFailed;
    public bool ShowProbeBad => _probeUnreachable && !IsChecking && ShowNewServerFields;

    /// <summary>A server is picked and answers (or is one this PC already knows): coupling can start.</summary>
    public bool IsServerReady => CoupleState is ServerCoupleState.Reachable or ServerCoupleState.PairingCancelled
        or ServerCoupleState.OtherCharacter or ServerCoupleState.PairingFailed
        || (CoupleState is ServerCoupleState.Idle or ServerCoupleState.Unreachable && _SelectedKnown() is not null);

    public bool ShowLabelAndScopes => IsServerReady && ShowNewServerFields;
    public bool ShowNotNow => !IsPairing && !IsCoupled;
    public bool ShowCoupleButton => ShowNotNow && IsServerReady;
    public bool HasServerScopes => ServerScopeChoices.Count > 0;

    public string Title => $"Couple {CharacterName} to a server?";
    public string CoupleButtonText => $"Couple {CharacterName} to {_TargetDisplayName()} →";
    public string PairingWaitTitle => $"Coupling {CharacterName} to {_TargetDisplayName()}…";
    public string ContactingText => $"Contacting {_TargetAddress()}…";
    public string PickAgainText => $"On the EVE page, pick {CharacterName} again";
    public string UnreachableText =>
        $"No answer from {_TargetAddress()} within {ServerPairingService.ContactTimeout.TotalSeconds:0} seconds.";
    public string UnreachableRetryText => _unreachableWhileCoupling ? "Try again" : "Test again";
    public string PairingCancelledText =>
        $"The EVE login for {_TargetDisplayName()} was not authorized. {CharacterName} is added to this PC; only the server coupling is missing.";

    /// <summary>
    /// Starts the step for one character. <paramref name="restoreAddress"/> is a coupling this PC already has, coupled
    /// again after the server dropped its session (ET-123): it opens on that server, so connecting and signing in are
    /// the only steps left.
    /// </summary>
    public async Task StartAsync(int characterId, string characterName, string? restoreAddress = null)
    {
        CharacterId = characterId;
        CharacterName = characterName;

        KnownServers.Clear();
        foreach (var server in await _owner.ListKnownServersAsync())
            KnownServers.Add(new KnownServerOption(server, _OnKnownServerSelected));
        OnPropertyChanged(nameof(HasKnownServers));

        ServerAddress = "";
        _SetLabel("");
        _labelEditedByUser = false;
        IsAnotherServer = false;
        _ResetServerChoice();
        ShowFingerprint = false;

        var restored = restoreAddress is null
            ? null
            : KnownServers.FirstOrDefault(option => string.Equals(option.Server.Address, restoreAddress, StringComparison.OrdinalIgnoreCase));
        if (restored is not null)
            restored.IsSelected = true;
        else if (KnownServers.Count > 0)
            KnownServers[0].IsSelected = true;
        _RaiseServerTexts();
    }

    partial void OnServerLabelChanged(string value)
    {
        if (!_fillingLabel) _labelEditedByUser = true;
    }

    partial void OnServerAddressChanged(string value)
    {
        // A new address is untested, whatever the previous one answered.
        if (CoupleState is ServerCoupleState.Pairing) return;
        _ResetServerChoice();
        _probe.AddressChanged(value);
    }

    partial void OnIsAnotherServerChanged(bool value)
    {
        if (!value) return;
        foreach (var option in KnownServers) option.IsSelected = false;
        _ResetServerChoice();
    }

    private void _OnKnownServerSelected(KnownServerOption selected)
    {
        foreach (var option in KnownServers.Where(option => option != selected)) option.IsSelected = false;
        IsAnotherServer = false;
        _ResetServerChoice();
        _ = _LoadKnownServerScopesAsync(selected.Server.Address);
    }

    private void _ResetServerChoice()
    {
        _probedServerName = null;
        _probeUnreachable = false;
        _requiredServerScopes = [];
        ServerScopeChoices.Clear();
        OnPropertyChanged(nameof(HasServerScopes));
        ProbeText = "";
        CoupleState = ServerCoupleState.Idle;
        _RaiseServerTexts();
    }

    /// <summary>A known server couples in one click, so its optional scopes are fetched quietly in the background; when it
    /// does not answer, the coupling itself says so.</summary>
    private async Task _LoadKnownServerScopesAsync(string address)
    {
        using var probeCts = new CancellationTokenSource(ServerPairingService.ContactTimeout);
        var response = await _owner.ProbeServerAsync(address, probeCts.Token);
        if (response is null || _SelectedKnown()?.Server.Address != address) return;
        _ApplyServerScopes(response);
    }

    /// <summary>The address is probed as it is typed; this asks again now, and a server that still does not answer
    /// gets the full explanation rather than the one-line hint.</summary>
    [RelayCommand]
    private Task TestConnection()
    {
        _explicitTest = true;
        _unreachableWhileCoupling = false;
        return _probe.ProbeNowAsync(_TargetAddress());
    }

    /// <summary>"Try again" after an unreachable server: repeats what failed — the coupling, or the test.</summary>
    [RelayCommand]
    private Task RetryUnreachable() => _unreachableWhileCoupling ? Couple() : TestConnection();

    private void _OnProbeChecking()
    {
        if (CoupleState is ServerCoupleState.Pairing) return;
        ProbeText = "checking…";
        CoupleState = ServerCoupleState.Checking;
    }

    private void _OnProbeCleared()
    {
        _explicitTest = false;
        _ResetServerChoice();
    }

    private void _OnProbeResult(string address, ServerScopesResponse? response)
    {
        var explicitTest = _explicitTest;
        _explicitTest = false;
        if (CoupleState is ServerCoupleState.Pairing || address != _TargetAddress()) return;

        if (response is null)
        {
            _probeUnreachable = true;
            ProbeText = "● server not reachable";
            CoupleState = explicitTest ? ServerCoupleState.Unreachable : ServerCoupleState.Idle;
            _RaiseServerTexts();
            return;
        }

        _probedServerName = string.IsNullOrWhiteSpace(response.ServerName) ? null : response.ServerName;
        ProbeText = $"● Server: {_probedServerName ?? address} · reachable";
        _ApplyServerScopes(response);

        // The server's own name goes in as the label's value, but never over what the user typed there themselves.
        if (!_labelEditedByUser && _probedServerName is not null && _SelectedKnown() is null)
            _SetLabel(_probedServerName);

        CoupleState = ServerCoupleState.Reachable;
        _RaiseServerTexts();
    }

    private void _ApplyServerScopes(ServerScopesResponse response)
    {
        _requiredServerScopes = response.RequiredScopes;
        ServerScopeChoices.Clear();
        foreach (var optional in response.OptionalScopes)
            ServerScopeChoices.Add(new ScopeChoiceViewModel(
                new EsiScopeRequirement(optional.Scope, EsiScopeTarget.Server, optional.Feature, optional.Reason), isSelected: false));
        OnPropertyChanged(nameof(HasServerScopes));
    }

    [RelayCommand]
    private async Task Couple()
    {
        var address = _TargetAddress();
        if (string.IsNullOrWhiteSpace(address)) return;
        var displayName = _TargetDisplayName();
        var isNewServer = _owner.GetServerFingerprint(address) is null;
        // An empty label, or the server's own name left as it was, is no label of the user's: the name stays the server's.
        var typedLabel = ServerLabel.Trim();
        string? label = _SelectedKnown() is null && typedLabel.Length > 0 && typedLabel != _probedServerName ? typedLabel : null;

        _pairingCts?.Dispose();
        _pairingCts = new CancellationTokenSource(PairingWindow);
        _cancelledByUser = false;
        _loginLink = null;
        var stateBefore = _SelectedKnown() is not null ? ServerCoupleState.Idle : ServerCoupleState.Reachable;
        PairingPhase = 1;
        OnPropertyChanged(nameof(ContactingText));
        CoupleState = ServerCoupleState.Pairing;

        try
        {
            IReadOnlyList<string> scopes = [.. (_requiredServerScopes.Count > 0 ? _requiredServerScopes : ["publicData"])
                .Concat(ServerScopeChoices.Where(choice => choice.IsSelected).Select(choice => choice.Scope))];
            var result = await _owner.CoupleCharacterAsync(address, label, scopes, CharacterId, _OnLoginLink, _pairingCts.Token);

            var serverName = label ?? (_SelectedKnown() is not null ? displayName : _probedServerName ?? result.ServerName);
            var affiliation = string.IsNullOrEmpty(result.AllianceName)
                ? result.CorporationName
                : $"{result.CorporationName} · {result.AllianceName}";
            CoupledTitle = string.IsNullOrWhiteSpace(affiliation)
                ? $"Connected to {serverName} as {result.CharacterName}"
                : $"Connected to {serverName} as {result.CharacterName} ({affiliation})";
            CoupledCertificateText = isNewServer
                ? "The server's certificate is now pinned. If it ever changes, EVE Together will refuse it and ask you."
                : "Same certificate as for your other characters on this server.";
            Fingerprint = _owner.GetServerFingerprint(address) ?? "";
            ShowFingerprint = isNewServer && Fingerprint.Length > 0;
            CoupleState = ServerCoupleState.Coupled;
            Coupled?.Invoke(serverName);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user: back to where coupling can start again, address and all. Past the pairing window:
            // the EVE login never came back, which reads the same as a declined one.
            CoupleState = _cancelledByUser ? stateBefore : ServerCoupleState.PairingCancelled;
        }
        catch (ServerUnreachableException)
        {
            _unreachableWhileCoupling = true;
            _probeUnreachable = true;
            ProbeText = "● server not reachable";
            CoupleState = ServerCoupleState.Unreachable;
            _RaiseServerTexts();
        }
        catch (PairingFailedException ex) when (ex.Failure is PairingFailure.Cancelled)
        {
            CoupleState = ServerCoupleState.PairingCancelled;
        }
        catch (PairingFailedException ex) when (ex.Failure is PairingFailure.OtherCharacter)
        {
            OtherCharacterText =
                $"On the EVE page you picked {ex.SignedInCharacterName}, but this step couples {CharacterName}. " +
                $"Nothing was coupled. Pick {CharacterName} on the EVE page.";
            CoupleState = ServerCoupleState.OtherCharacter;
        }
        catch (Exception ex)
        {
            PairingError = ex.Message;
            CoupleState = ServerCoupleState.PairingFailed;
        }
    }

    [RelayCommand]
    private void NotNow() => NotNowRequested?.Invoke();

    /// <summary>Stops a pairing under way — the call to the server included — and leaves the address to change.</summary>
    [RelayCommand]
    public void CancelPairing()
    {
        _cancelledByUser = true;
        _pairingCts?.Cancel();
    }

    [RelayCommand]
    private async Task CopyLoginLink()
    {
        if (_loginLink is not null) await _owner.CopyToClipboardAsync(_loginLink);
    }

    private void _OnLoginLink(string url)
    {
        _loginLink = url;
        PairingPhase = 3;
    }

    public void Dispose()
    {
        CancelPairing();
        _pairingCts?.Dispose();
        _probe.Dispose();
    }

    private void _SetLabel(string label)
    {
        _fillingLabel = true;
        ServerLabel = label;
        _fillingLabel = false;
    }

    private KnownServerOption? _SelectedKnown() =>
        IsAnotherServer ? null : KnownServers.FirstOrDefault(option => option.IsSelected);

    private string _TargetAddress() => _SelectedKnown()?.Server.Address ?? ServerAddress.Trim();

    private string _TargetDisplayName()
    {
        if (_SelectedKnown() is { } known) return known.Server.DisplayName;
        if (!string.IsNullOrWhiteSpace(ServerLabel)) return ServerLabel.Trim();
        return _probedServerName ?? _TargetAddress();
    }

    private void _RaiseServerTexts()
    {
        OnPropertyChanged(nameof(CoupleButtonText));
        OnPropertyChanged(nameof(PairingWaitTitle));
        OnPropertyChanged(nameof(ContactingText));
        OnPropertyChanged(nameof(UnreachableText));
        OnPropertyChanged(nameof(UnreachableRetryText));
        OnPropertyChanged(nameof(PairingCancelledText));
        OnPropertyChanged(nameof(ShowNewServerFields));
        OnPropertyChanged(nameof(IsServerReady));
        OnPropertyChanged(nameof(ShowLabelAndScopes));
        OnPropertyChanged(nameof(ShowCoupleButton));
        OnPropertyChanged(nameof(ShowProbeOk));
        OnPropertyChanged(nameof(ShowProbeBad));
    }
}
