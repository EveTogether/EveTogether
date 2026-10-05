using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Esi;
using EveUtils.Client.ViewModels.Coupling;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi;

namespace EveUtils.Client.ViewModels.Setup;

/// <summary>
/// The guided setup (ET-425): per character, sign in with EVE, optionally couple that character to a server, then
/// "add another?" — and finally an overview of what this run added. Opened on a fresh install (with a welcome step),
/// from Settings › General › Setup (the same), and from <c>+ ADD CHARACTER</c> (without). Signing in and coupling go
/// through <see cref="MainWindowViewModel"/>, the same routes the rest of the app uses.
/// </summary>
public sealed partial class SetupWizardViewModel : ObservableObject, IDisposable
{
    private const string PublicDataScope = "publicData";

    private static readonly TimeSpan SignInWindow = TimeSpan.FromMinutes(3);

    private readonly MainWindowViewModel _owner;
    private readonly List<(int CharacterId, string Name, string? ServerName)> _added = [];
    private readonly IReadOnlyList<EsiScopeRequirement> _clientScopes;
    private int _existingAtStart;
    private IReadOnlyList<string> _previousScopes = [];
    private string? _previousName;
    private string? _loginLink;
    private bool _copyLinkWhenKnown;
    private CancellationTokenSource? _waitCts;
    private bool _cancelledByUser;

    public SetupWizardViewModel(MainWindowViewModel owner, SetupWizardEntry entry)
    {
        _owner = owner;
        Entry = entry;
        _clientScopes = owner.ScopeRegistry?.GetRequirements(EsiScopeTarget.Client) ?? [];
        _step = entry is SetupWizardEntry.FirstStart ? SetupWizardStep.Welcome : SetupWizardStep.Character;
        Server = new ServerCoupleViewModel(owner);
        Server.Coupled += _OnCoupled;
        Server.NotNowRequested += NotNow;
    }

    /// <summary>Raised when the wizard is done with: Done, a confirmed Skip/Cancel, or Close.</summary>
    public event Action? CloseRequested;

    public SetupWizardEntry Entry { get; }
    public bool IsFirstStart => Entry is SetupWizardEntry.FirstStart;
    public string HeaderTitle => IsFirstStart ? "FIRST-START SETUP" : "ADD CHARACTER";
    public string StopText => IsFirstStart ? "Skip setup" : "Cancel";

    public ObservableCollection<StepSegment> Stepper { get; } = [];
    public ObservableCollection<ScopeChoiceViewModel> ScopeChoices { get; } = [];
    public ObservableCollection<SetupCharacterRow> AddedRows { get; } = [];

    // ── Step ──────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsCharacterStep), nameof(IsServerStep), nameof(IsAnotherStep),
        nameof(IsDoneStep), nameof(ShowRoundBadge), nameof(RoundText), nameof(ShowStopButton))]
    private SetupWizardStep _step;

    public bool IsWelcome => Step is SetupWizardStep.Welcome;
    public bool IsCharacterStep => Step is SetupWizardStep.Character;
    public bool IsServerStep => Step is SetupWizardStep.Server;
    public bool IsAnotherStep => Step is SetupWizardStep.Another;
    public bool IsDoneStep => Step is SetupWizardStep.Done;
    public bool ShowStopButton => !IsDoneStep;
    public bool ShowRoundBadge => IsCharacterStep || IsServerStep;
    public string RoundText => $"CHARACTER {_added.Count + (IsCharacterStep ? 1 : 0)}";

    partial void OnStepChanged(SetupWizardStep value)
    {
        _RebuildStepper();
        _RebuildAddedRows();
        _RaiseCharacterTexts();
        OnPropertyChanged(nameof(AnotherIntro));
        OnPropertyChanged(nameof(AddedLabel));
        OnPropertyChanged(nameof(DoneTitle));
        OnPropertyChanged(nameof(DoneIntro));
        OnPropertyChanged(nameof(HasAddedRows));
        OnPropertyChanged(nameof(ShowCharacterBack));
    }

    public void Initialize()
    {
        _existingAtStart = _owner.Characters.Count(character => character.CharacterId > 0);

        // "Same access as" starts from the newest character already on this PC — from the second round on it is the
        // one this run added just before.
        if (_owner.Characters.LastOrDefault(character => character.CharacterId > 0) is { } newest)
        {
            _previousName = newest.Name;
            _previousScopes = newest.GrantedScopes;
        }

        _RebuildStepper();
        _RebuildScopeChoices();
        _RaiseCharacterTexts();
    }

    // ── Welcome ───────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void GetStarted() => _EnterCharacterStep();

    // ── Character ─────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignInWaiting), nameof(IsSignInCancelled), nameof(IsSignInNoAnswer),
        nameof(IsSignInFailed), nameof(ShowSignInButton), nameof(ShowCharacterForm))]
    private CharacterSignInState _signInState;

    [ObservableProperty] private string _signInError = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSameAccess), nameof(ShowScopeList))]
    private bool _showFullAccess;

    public bool IsSignInWaiting => SignInState is CharacterSignInState.Waiting;
    public bool IsSignInCancelled => SignInState is CharacterSignInState.Cancelled;
    public bool IsSignInNoAnswer => SignInState is CharacterSignInState.NoAnswer;
    public bool IsSignInFailed => SignInState is CharacterSignInState.Failed;
    public bool ShowSignInButton => IsCharacterStep && !IsSignInWaiting;
    public bool ShowCharacterForm => !IsSignInWaiting;
    public bool ShowSameAccess => _previousName is not null && !ShowFullAccess;
    public bool ShowScopeList => !ShowSameAccess;

    /// <summary>"Stop here, show overview" once something was added; on <c>+ ADD CHARACTER</c> with nothing added yet,
    /// "Close" — the first start has no such way out of an error, Skip setup is that.</summary>
    public bool ShowStopHere => _added.Count > 0;
    public bool ShowErrorClose => _added.Count == 0 && !IsFirstStart;

    public bool ShowCharacterBack => IsCharacterStep && (_added.Count > 0 || IsFirstStart);

    public string CharacterTitle =>
        _added.Count == 0 && IsFirstStart ? "Add your first character" : "Add a character";

    private bool HasCharactersOnPc => _existingAtStart + _added.Count > 0;

    public string CharacterIntro =>
        "EVE Together reads the character's data from EVE Online (ESI). Choose what it may read; you sign in on the next " +
        "screen, in your browser." +
        (HasCharactersOnPc ? " A character on another EVE account? Log out at the top of the EVE page first; it remembers the last account." : "");

    public string SameAccessTitle => $"Same access as {_previousName}";
    public string SameAccessSummary => _DescribeAccess(_previousScopes);

    public string SignInCancelledText =>
        "The EVE login page reported that the access was not authorized (access_denied). Nothing was saved" +
        (_added.Count > 0 ? "; the characters you added before stay." : ".");

    public string WaitingAccountStep => HasCharactersOnPc
        ? "Other EVE account? Log out at the top of the EVE page first"
        : "Log in and choose a character";

    [RelayCommand]
    private void CharacterBack()
    {
        if (_added.Count > 0) Step = SetupWizardStep.Another;
        else Step = SetupWizardStep.Welcome;
    }

    [RelayCommand]
    private void ChangeAccess()
    {
        ShowFullAccess = true;
        SignInState = CharacterSignInState.Idle;
    }

    [RelayCommand]
    private void SelectAllScopes()
    {
        foreach (var choice in ScopeChoices.Where(choice => choice.IsEditable)) choice.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNoScopes()
    {
        foreach (var choice in ScopeChoices.Where(choice => choice.IsEditable)) choice.IsSelected = false;
    }

    [RelayCommand]
    private Task SignIn() => _SignInAsync(copyLink: false);

    /// <summary>After "no answer" the old link is dead with its listener, so this starts a fresh sign-in and copies
    /// that one.</summary>
    [RelayCommand]
    private Task SignInAndCopyLink() => _SignInAsync(copyLink: true);

    private async Task _SignInAsync(bool copyLink)
    {
        _waitCts?.Dispose();
        _waitCts = new CancellationTokenSource(SignInWindow);
        _cancelledByUser = false;
        _copyLinkWhenKnown = copyLink;
        _loginLink = null;
        SignInState = CharacterSignInState.Waiting;

        try
        {
            IReadOnlyList<string> scopes = ShowSameAccess
                ? _previousScopes
                : [.. ScopeChoices.Where(choice => choice.IsSelected).Select(choice => choice.Scope)];
            EsiIdentity identity = await _owner.SignInCharacterAsync(scopes, _OnLoginLink, _waitCts.Token);

            _added.Add((identity.CharacterId, identity.CharacterName, null));
            _previousName = identity.CharacterName;
            _previousScopes = identity.GrantedScopes;
            SignInState = CharacterSignInState.Idle;
            await _EnterServerStepAsync();
        }
        catch (OperationCanceledException)
        {
            SignInState = _cancelledByUser ? CharacterSignInState.Idle : CharacterSignInState.NoAnswer;
        }
        catch (EsiSignInDeniedException)
        {
            SignInState = CharacterSignInState.Cancelled;
        }
        catch (Exception ex)
        {
            SignInError = ex.Message;
            SignInState = CharacterSignInState.Failed;
        }
    }

    [RelayCommand]
    private void CancelWaiting()
    {
        _cancelledByUser = true;
        _waitCts?.Cancel();
    }

    [RelayCommand]
    private async Task CopyLoginLink()
    {
        if (_loginLink is not null) await _owner.CopyToClipboardAsync(_loginLink);
    }

    private void _OnLoginLink(string url)
    {
        _loginLink = url;
        if (_copyLinkWhenKnown) _ = _owner.CopyToClipboardAsync(url);
    }

    // ── Server ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The server step: the same coupling as the character screen's "Couple to server" (ET-428).</summary>
    public ServerCoupleViewModel Server { get; }

    [ObservableProperty] private SetupCharacterRow? _currentRow;

    [RelayCommand]
    private void NotNow() => Step = SetupWizardStep.Another;

    private async Task _EnterServerStepAsync()
    {
        var (characterId, name, _) = _added[^1];
        await Server.StartAsync(characterId, name);
        _RebuildCurrentRow();
        Step = SetupWizardStep.Server;
    }

    private void _OnCoupled(string serverName)
    {
        var (characterId, name, _) = _added[^1];
        _added[^1] = (characterId, name, serverName);
        _RebuildCurrentRow();
    }

    private void _RebuildCurrentRow() =>
        CurrentRow = _added.Count == 0 ? null : _RowFor(_added[^1].CharacterId, _added[^1].Name, _added[^1].ServerName, "ADDED");

    // ── Another? / Done ───────────────────────────────────────────────────────────────────────────

    public bool HasAddedRows => AddedRows.Count > 0;
    public string AddedLabel => $"ADDED {(IsFirstStart ? "SO FAR" : "NOW")} · {_added.Count}";

    public string AnotherIntro
    {
        get
        {
            if (_added.Count == 0) return "";
            var (_, name, server) = _added[^1];
            var what = server is null ? "added" : $"added and coupled to {server}";
            return $"{name} is {what}. Add an alt now, or finish and see what you set up.";
        }
    }

    public string DoneTitle => IsFirstStart ? "You're set" : "Done";

    public string DoneIntro
    {
        get
        {
            var coupled = _added.Where(added => added.ServerName is not null).ToList();
            var servers = coupled.Select(added => added.ServerName).Distinct().ToList();
            var coupledPart = coupled.Count == 0 ? ""
                : $", {coupled.Count} coupled to {(servers.Count == 1 ? servers[0] : "a server")}";
            var others = _existingAtStart switch
            {
                0 => "",
                1 => " Your other character is unchanged.",
                _ => $" Your other {_existingAtStart} characters are unchanged."
            };
            return $"{_added.Count} character{(_added.Count == 1 ? "" : "s")} added{coupledPart}.{others}";
        }
    }

    [RelayCommand]
    private void AddAnother() => _EnterCharacterStep();

    [RelayCommand]
    private void Finish() => Step = SetupWizardStep.Done;

    [RelayCommand]
    private void CloseWizard() => CloseRequested?.Invoke();

    // ── Skip / Cancel ─────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isStopDialogOpen;

    public string StopDialogTitle => IsFirstStart ? "Skip the setup?" : "Stop adding characters?";

    public string StopDialogText
    {
        get
        {
            var stays = _added.Count == 0 ? ""
                : "What you added stays: " +
                  string.Join(", ", _added.Select(added => added.ServerName is null ? added.Name : $"{added.Name} ({added.ServerName})")) + ". ";
            return stays + (IsFirstStart
                ? "You can open the setup again from Settings › General, or add a character with + ADD CHARACTER."
                : "Add more any time with + ADD CHARACTER.");
        }
    }

    [RelayCommand]
    private void RequestStop()
    {
        if (_added.Count == 0 && !IsFirstStart)
        {
            _Leave();
            return;
        }
        OnPropertyChanged(nameof(StopDialogText));
        IsStopDialogOpen = true;
    }

    [RelayCommand]
    private void StopDialogBack() => IsStopDialogOpen = false;

    [RelayCommand]
    private void StopDialogShowOverview()
    {
        IsStopDialogOpen = false;
        _CancelWaiting();
        Step = SetupWizardStep.Done;
    }

    [RelayCommand]
    private void StopDialogConfirm() => _Leave();

    private void _Leave()
    {
        _CancelWaiting();
        CloseRequested?.Invoke();
    }

    private void _CancelWaiting()
    {
        _cancelledByUser = true;
        _waitCts?.Cancel();
        Server.CancelPairing();
    }

    public void Dispose()
    {
        _CancelWaiting();
        _waitCts?.Dispose();
        Server.Coupled -= _OnCoupled;
        Server.NotNowRequested -= NotNow;
        Server.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private void _EnterCharacterStep()
    {
        SignInState = CharacterSignInState.Idle;
        ShowFullAccess = false;
        _RebuildScopeChoices();
        Step = SetupWizardStep.Character;
    }

    private void _RebuildScopeChoices()
    {
        ScopeChoices.Clear();
        foreach (var requirement in _clientScopes.DistinctBy(requirement => requirement.Scope, StringComparer.OrdinalIgnoreCase))
        {
            var isFixed = requirement.Scope == PublicDataScope;
            var isSelected = _previousName is null
                ? !requirement.OptIn
                : _previousScopes.Contains(requirement.Scope, StringComparer.OrdinalIgnoreCase);
            ScopeChoices.Add(new ScopeChoiceViewModel(requirement, isSelected, isFixed));
        }
    }

    private void _RaiseCharacterTexts()
    {
        OnPropertyChanged(nameof(CharacterTitle));
        OnPropertyChanged(nameof(CharacterIntro));
        OnPropertyChanged(nameof(SameAccessTitle));
        OnPropertyChanged(nameof(SameAccessSummary));
        OnPropertyChanged(nameof(ShowSameAccess));
        OnPropertyChanged(nameof(ShowScopeList));
        OnPropertyChanged(nameof(SignInCancelledText));
        OnPropertyChanged(nameof(WaitingAccountStep));
        OnPropertyChanged(nameof(ShowStopHere));
        OnPropertyChanged(nameof(ShowErrorClose));
        OnPropertyChanged(nameof(ShowSignInButton));
        OnPropertyChanged(nameof(ShowCharacterBack));
    }

    private void _RebuildAddedRows()
    {
        AddedRows.Clear();
        foreach (var (characterId, name, serverName) in _added)
            AddedRows.Add(_RowFor(characterId, name, serverName));
    }

    // The list is rebuilt whenever the registry changes, so the row is looked up fresh rather than held on to.
    private SetupCharacterRow _RowFor(int characterId, string name, string? serverName, string? chipText = null)
    {
        var character = _owner.Characters.FirstOrDefault(candidate => candidate.CharacterId == characterId)
                        ?? new CharacterViewModel(new Character(name, characterId, []));
        return new SetupCharacterRow(character, serverName, chipText);
    }

    private void _RebuildStepper()
    {
        SetupWizardStep[] steps = IsFirstStart
            ? [SetupWizardStep.Welcome, SetupWizardStep.Character, SetupWizardStep.Server, SetupWizardStep.Another, SetupWizardStep.Done]
            : [SetupWizardStep.Character, SetupWizardStep.Server, SetupWizardStep.Another, SetupWizardStep.Done];
        var current = Array.IndexOf(steps, Step);

        Stepper.Clear();
        for (var index = 0; index < steps.Length; index++)
        {
            var state = index < current ? StepSegmentState.Done
                : index == current ? StepSegmentState.Current
                : StepSegmentState.Upcoming;
            var isLoop = steps[index] is SetupWizardStep.Character or SetupWizardStep.Server or SetupWizardStep.Another;
            Stepper.Add(new StepSegment(_StepLabel(steps[index]), state, steps[index] is SetupWizardStep.Server, isLoop));
        }
    }

    private static string _StepLabel(SetupWizardStep step) => step switch
    {
        SetupWizardStep.Welcome => "WELCOME",
        SetupWizardStep.Character => "CHARACTER",
        SetupWizardStep.Server => "SERVER",
        SetupWizardStep.Another => "ANOTHER?",
        _ => "DONE"
    };

    /// <summary>"Identity, Fittings, Skills. Fleet and Location stay off." — per feature, in the order the modules
    /// declare them.</summary>
    private string _DescribeAccess(IReadOnlyList<string> granted)
    {
        var features = _clientScopes
            .GroupBy(requirement => requirement.Feature)
            .Select(group => (Feature: group.Key, Granted: group.Any(requirement => granted.Contains(requirement.Scope, StringComparer.OrdinalIgnoreCase))))
            .ToList();
        var on = features.Where(feature => feature.Granted).Select(feature => feature.Feature).ToList();
        var off = features.Where(feature => !feature.Granted).Select(feature => feature.Feature).ToList();

        var text = on.Count == 0 ? "Identity only." : string.Join(", ", on) + ".";
        return off.Count switch
        {
            0 => text,
            1 => $"{text} {off[0]} stays off.",
            _ => $"{text} {string.Join(", ", off.Take(off.Count - 1))} and {off[^1]} stay off."
        };
    }
}
