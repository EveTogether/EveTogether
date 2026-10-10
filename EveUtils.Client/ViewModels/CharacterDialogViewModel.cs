using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Messaging;
using EveUtils.Shared.Modules.Esi;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The per-character settings dialog: identity + ESI status + ESI scopes (+ add-scope
/// re-auth) + the list of coupled servers (each with live status, a gear-button trust dialog and Decouple) +
/// "Couple to server" + "Remove character". Opened from the gear button on a character row; replaces the former always-on detail pane.
/// Delegates the actual operations to <see cref="MainWindowViewModel"/> (one source of truth for the
/// couple/decouple/re-auth flows) and rebuilds its own view of the data afterwards.
/// </summary>
public partial class CharacterDialogViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _owner;
    private readonly CharacterViewModel _initial;

    public int CharacterId { get; }

    /// <summary>No ESI link — surfaced from a gamelog only.</summary>
    public bool IsLocalOnly { get; }

    /// <summary>Server-coupling + ESI scopes are only offered for ESI-linked characters.</summary>
    public bool CanCouple => !IsLocalOnly;

    /// <summary>Only a signed-in character is stored on this PC; a local-only row is a gamelog name and nothing more.</summary>
    public bool CanRemove => !IsLocalOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemovalQuestion))]
    private string _name = "";

    // ── Remove character (ET-345) ── the normal case is two clicks: the button, then Remove. The confirmation stands
    // inline so it can carry the history checkbox and the open-run note without a second window.

    [ObservableProperty] private bool _isConfirmingRemoval;

    /// <summary>Runs and fittings stay unless this is ticked: they are history, partly shared with the fleet, and a
    /// saved run already keeps the pilot's name.</summary>
    [ObservableProperty] private bool _deleteRunsAndFittings;

    [ObservableProperty] private bool _removalStopsOpenRun;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemovalBlocked))]
    private string _removalBlockedReason = "";

    public bool IsRemovalBlocked => RemovalBlockedReason.Length > 0;

    public string RemovalQuestion => $"Remove {Name} from this PC?";

    public string RemovalExplanation =>
        "It is decoupled from every server and signed out at CCP, and its skills, implants, killmails, messages and " +
        "cached metrics are deleted from this PC. Signing in with it again adds it back, and a gamelog it writes later " +
        "shows it as a local-only character.";

    /// <summary>Raised once the character is gone: this dialog has nothing left to show.</summary>
    public event Action? CloseRequested;

    /// <summary>
    /// What this character actually shares, for the tooltip on the scopes block.
    /// </summary>
    /// <remarks>
    /// Read from the grant rather than from the scopes this build would ask for: where those two differ is the
    /// interesting part, and it is the question the re-authenticate button leaves unanswered.
    /// </remarks>
    [ObservableProperty] private string _scopesTooltip = "";
    [ObservableProperty] private string _esiStatus = "";
    [ObservableProperty] private string _status = "";

    /// <summary>This character's coupled servers, each with its own live state, a gear button and Decouple.</summary>
    public ObservableCollection<ServerLinkViewModel> ServerLinks { get; } = [];

    public CharacterDialogViewModel(MainWindowViewModel owner, CharacterViewModel character)
    {
        _owner = owner;
        _initial = character;
        CharacterId = character.CharacterId;
        IsLocalOnly = character.IsLocalOnly;
        if (_owner.Bus is not null)
            _owner.Bus.CharacterStateChanged += OnServerState;
    }

    /// <summary>Loads the character snapshot + its coupled-server links. Call right after construction.</summary>
    public async Task InitializeAsync()
    {
        ApplyCharacterSnapshot();
        await ReloadServerLinksAsync();
        await ReloadEveWorkbenchKeyAsync(refresh: true);
    }

    // ── EVE Workbench (optional, ET-325) ── the API key an upload uses: the pilot's own, or the one of the EVE Workbench
    // account it is an alt on (a key is stored once for the whole account). Nothing here is required.
    private global::EveUtils.Client.Runs.ResolvedEveWorkbenchKey? _key;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEveWorkbenchKeyCommand))]
    private string _eveWorkbenchKeyInput = "";

    /// <summary>Which of the three states the pilot is in: own key, alt on another key, or no key.</summary>
    [ObservableProperty] private string _eveWorkbenchKeyState = "";

    /// <summary>The EVE Workbench account's characters, or why a key was not accepted.</summary>
    [ObservableProperty] private string _eveWorkbenchKeyStatus = "";

    /// <summary>The how-to and the links, only while the pilot has no usable key.</summary>
    [ObservableProperty] private bool _showEveWorkbenchKeyHelp;

    public string EveWorkbenchHowTo => global::EveUtils.Client.Runs.EveWorkbenchLinks.HowTo;

    [RelayCommand]
    private static void OpenAbyssTrackerTokens() => global::EveUtils.Client.Runs.EveWorkbenchLinks.Open(global::EveUtils.Client.Runs.EveWorkbenchLinks.AbyssTrackerTokens);

    [RelayCommand]
    private static void OpenEveJournalTokens() => global::EveUtils.Client.Runs.EveWorkbenchLinks.Open(global::EveUtils.Client.Runs.EveWorkbenchLinks.EveJournalTokens);

    /// <summary>Set while a key was saved that does not cover this pilot: EVE Workbench would refuse its runs.</summary>
    [ObservableProperty] private string _eveWorkbenchPilotWarning = "";

    /// <summary>Only an own key can be cleared here; an alt's key belongs to its main.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearEveWorkbenchKeyCommand))]
    private bool _hasOwnEveWorkbenchKey;

    private async Task ReloadEveWorkbenchKeyAsync(bool refresh)
    {
        if (_owner.EveWorkbenchPublisher is not { } publisher)
        {
            return;
        }

        _key = await publisher.KeyForPilotAsync(CharacterId, refresh);
        HasOwnEveWorkbenchKey = _key is { IsOwn: true };
        ShowEveWorkbenchKeyHelp = _key is null or { Invalid: true };
        EveWorkbenchKeyState = _key switch
        {
            null => "No key. Runs are only uploaded when you choose to, and then a key is needed.",
            { Invalid: true } => "API key invalid — set it again.",
            { IsOwn: true } => $"Own key (main: {_key.MainName}).",
            _ => $"Uses the key of {_key.MainName} (alt). You can still enter an own key below."
        };
        EveWorkbenchKeyStatus = _key is null ? "" : "Characters on this account: " + string.Join(", ", _key.Characters.Select(c => c.Name)) + ".";
    }

    private bool CanSaveEveWorkbenchKey => !string.IsNullOrWhiteSpace(EveWorkbenchKeyInput);

    [RelayCommand(CanExecute = nameof(CanSaveEveWorkbenchKey))]
    private async Task SaveEveWorkbenchKey()
    {
        if (_owner.EveWorkbenchPublisher is not { } publisher)
        {
            return;
        }

        global::EveUtils.Client.Runs.EveWorkbenchKeyCheck check = await publisher.SaveKeyAsync(EveWorkbenchKeyInput);
        EveWorkbenchPilotWarning = "";
        if (check.Verdict == global::EveUtils.Client.Runs.EveWorkbenchKeyVerdict.Valid)
        {
            EveWorkbenchKeyInput = "";
            await ReloadEveWorkbenchKeyAsync(refresh: false);
            if (check.Characters.All(c => c.Id != CharacterId))
            {
                EveWorkbenchPilotWarning = $"This key does not cover {Name}: EVE Workbench would refuse this pilot's runs (\"Character does not belong to the authenticated account\").";
            }
        }
        else
        {
            EveWorkbenchKeyStatus = check.Verdict == global::EveUtils.Client.Runs.EveWorkbenchKeyVerdict.Invalid
                ? "EVE Workbench did not accept this key."
                : "EVE Workbench could not be reached to check the key; it was not saved.";
        }
    }

    private bool CanClearEveWorkbenchKey => HasOwnEveWorkbenchKey;

    [RelayCommand(CanExecute = nameof(CanClearEveWorkbenchKey))]
    private async Task ClearEveWorkbenchKey()
    {
        if (_owner.EveWorkbenchPublisher is not { } publisher || _key is not { IsOwn: true } key)
        {
            return;
        }

        await publisher.ClearKeyAsync(key.MainId);
        EveWorkbenchPilotWarning = "";
        await ReloadEveWorkbenchKeyAsync(refresh: false);
    }

    private void ApplyCharacterSnapshot()
    {
        // ESI characters are re-read fresh from the (rebuilt) list by id; local-only rows share id 0, so fall
        // back to the snapshot we were opened with (they have no scope/couple state to refresh anyway).
        var c = IsLocalOnly ? _initial : _owner.Characters.FirstOrDefault(x => x.CharacterId == CharacterId) ?? _initial;
        Name = c.Name;
        EsiStatus = c.EsiStatus;
        ScopesTooltip = Esi.EsiScopeSummary.Describe(c.GrantedScopes,
            _owner.ScopeRegistry?.GetRequirements(EsiScopeTarget.Client) ?? []);
    }

    private async Task ReloadServerLinksAsync()
    {
        var links = await _owner.BuildServerLinksAsync(CharacterId, DecoupleAsync, ViewTrustAsync, RecoupleAsync);
        ServerLinks.Clear();
        foreach (var link in links)
            ServerLinks.Add(link);
    }

    [RelayCommand]
    private async Task ReAuthenticate()
    {
        Status = "Opening scope selection…";
        await _owner.ReAuthenticateAsync(CharacterId);
        ApplyCharacterSnapshot(); // owner refreshed the registry → reflect any scope change
        Status = "";
    }

    [RelayCommand]
    private async Task Couple()
    {
        if (IsLocalOnly)
        {
            Status = "Sign in with ESI first — server-coupling requires an ESI link.";
            return;
        }

        var ok = await _owner.RunCoupleAsync(expectedCharacterId: CharacterId);
        if (ok)
        {
            ApplyCharacterSnapshot();
            await ReloadServerLinksAsync();
            Status = "Coupled.";
        }
        else
        {
            Status = "Coupling cancelled.";
        }
    }

    private async Task DecoupleAsync(ServerLinkViewModel link)
    {
        await _owner.DecoupleAsync(link);
        await ReloadServerLinksAsync();
    }

    /// <summary>Couple again from the link that has gone dead, which is where the user is looking when they are
    /// told to. The same flow as the "Couple to server" button below — pairing overwrites the stored session and
    /// restarts the connection, so nothing is deleted first.</summary>
    private async Task RecoupleAsync(ServerLinkViewModel link)
    {
        Status = $"Coupling {link.DisplayName} again…";
        // Hands the address over so the dialog opens filled in: this coupling already exists, it just has no session
        // on the server any more, so there is nothing here for the user to look up or retype (ET-123).
        var ok = await _owner.RunCoupleAsync(link.Address, CharacterId);
        await ReloadServerLinksAsync();
        Status = ok ? "Coupled." : "Coupling cancelled.";
    }

    private async Task ViewTrustAsync(ServerLinkViewModel link)
    {
        if (await _owner.ShowServerTrustAsync(link))
            await DecoupleAsync(link); // user pressed Decouple inside the trust dialog
    }

    [RelayCommand]
    private async Task RemoveCharacter()
    {
        var check = await _owner.CheckCharacterRemovalAsync(CharacterId);
        if (check is null)
            return;

        RemovalBlockedReason = check.BlockingFleetName is { } fleet
            ? $"{Name} commands the active fleet \"{fleet}\". Hand the fleet over or stop it first, then remove the character."
            : "";
        RemovalStopsOpenRun = check.HasOpenRun;
        IsConfirmingRemoval = !check.IsBlocked;
    }

    [RelayCommand]
    private void CancelRemoval()
    {
        IsConfirmingRemoval = false;
        DeleteRunsAndFittings = false;
    }

    [RelayCommand]
    private async Task ConfirmRemoval()
    {
        Status = $"Removing {Name}…";
        if (await _owner.RemoveCharacterAsync(CharacterId, Name, DeleteRunsAndFittings))
        {
            CloseRequested?.Invoke();
            return;
        }

        // Something changed while the confirmation stood open (a fleet went active): show why, the same way the first
        // click would have.
        Status = "";
        await RemoveCharacter();
    }

    // Only this dialog's character, and only its link to that server. The dialog shows one character's couplings, so
    // painting every link for the address wrote another character's state onto them (ET-123).
    private void OnServerState(string address, int characterId, ServerConnectionState state) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (characterId != CharacterId)
                return;
            foreach (var link in ServerLinks)
                if (string.Equals(link.Address, address, StringComparison.OrdinalIgnoreCase))
                    link.State = state;
        });

    public void Dispose()
    {
        if (_owner.Bus is not null)
            _owner.Bus.CharacterStateChanged -= OnServerState;
    }
}
