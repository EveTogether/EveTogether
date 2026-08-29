using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.EveSettings;
using EveUtils.Client.Platform;
using EveUtils.Shared.Messaging;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The EVE Settings Sync tool: copy one character's (or one account's) EVE client settings onto your other
/// characters (or accounts), with a full backup of the profile taken first, every time.
///
/// Three things shape this screen. Characters and accounts are two separate blocks with separate sources and
/// separate target lists, so there is no gesture that copies one onto the other. Nothing is written while an EVE
/// client is running — EVE rewrites these files when it closes, so a sync into a running client is undone at logout
/// — and the check is on the running process, not only on who is visibly in-game. And every action says what it is
/// about to do, in names, before it does it.
/// </summary>
public partial class SettingsSyncViewModel : ViewModelBase, IRefreshableModule
{
    private readonly SettingsSyncService _sync;
    private readonly SettingsBackupService _backups;
    private readonly EveSettingsNameResolver _nameResolver;
    private readonly EveSettingsPreferences _preferences;
    private readonly EveClientPresenceService? _presence;
    private readonly IDialogService? _dialogs;

    private List<EveSettingsProfile> _profiles = [];
    private EveSettingsNames _names = new(
        new Dictionary<long, string>(), new Dictionary<long, string>(), new Dictionary<long, IReadOnlyList<long>>());

    public SettingsSyncViewModel(
        SettingsSyncService sync,
        SettingsBackupService backups,
        EveSettingsNameResolver nameResolver,
        EveSettingsPreferences preferences,
        EveClientPresenceService? presence = null,
        IDialogService? dialogs = null)
    {
        _sync = sync;
        _backups = backups;
        _nameResolver = nameResolver;
        _preferences = preferences;
        _presence = presence;
        _dialogs = dialogs;
        BackupRoot = backups.RootDirectory;

        _ = LoadAsync();
    }

    // ── Where the settings are ───────────────────────────────────────────────────────────────────

    /// <summary>The EVE install directory that holds the settings_* profiles. Editable: auto-detection covers the
    /// Windows install, and everywhere else (and after a move) the user points at it themselves.</summary>
    [ObservableProperty] private string _installRoot = string.Empty;

    public ObservableCollection<string> ProfileNames { get; } = [];

    [ObservableProperty] private string? _selectedProfileName;

    /// <summary>Where the backups are kept, spelled out so the user never has to take our word for it.</summary>
    public string BackupRoot { get; }

    // ── What is in the selected profile ──────────────────────────────────────────────────────────

    public ObservableCollection<SettingsFileRowViewModel> Characters { get; } = [];

    public ObservableCollection<SettingsFileRowViewModel> Accounts { get; } = [];

    [ObservableProperty] private SettingsFileRowViewModel? _characterSource;

    [ObservableProperty] private SettingsFileRowViewModel? _accountSource;

    public ObservableCollection<SettingsBackupRowViewModel> Backups { get; } = [];

    [ObservableProperty] private SettingsBackupRowViewModel? _selectedBackup;

    // ── What the screen is telling the user ──────────────────────────────────────────────────────

    [ObservableProperty] private string _status = "Pick a profile, choose who to copy from, tick who to copy to.";

    [ObservableProperty] private bool _statusIsError;

    [ObservableProperty] private bool _isBusy;

    /// <summary>The running-client verdict, e.g. "2 EVE clients are running (Jithran, Lyra Custos)."</summary>
    [ObservableProperty] private string _clientWarning = string.Empty;

    [ObservableProperty] private bool _clientsRunning;

    public string ProfileSummary => _SelectedProfile() is { } profile
        ? $"{profile.Name} — {profile.Characters.Count} characters, {profile.Accounts.Count} accounts"
        : "No profile selected.";

    public bool HasProfile => _SelectedProfile() is not null;

    public bool HasBackups => Backups.Count > 0;

    /// <summary>What the character block would do if pressed right now — shown above the button, not after it.</summary>
    public string CharacterPlanSummary => _PlanSummary(SettingsFileKind.Character);

    public string AccountPlanSummary => _PlanSummary(SettingsFileKind.Account);

    public bool CanSyncCharacters => !ClientsRunning && !IsBusy && _TargetsOf(SettingsFileKind.Character).Count > 0;

    public bool CanSyncAccounts => !ClientsRunning && !IsBusy && _TargetsOf(SettingsFileKind.Account).Count > 0;

    public bool CanBackup => !IsBusy && HasProfile;

    public bool CanRestore => !ClientsRunning && !IsBusy && SelectedBackup is { CanRestore: true };

    // ── Loading ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Re-reads the install directory, its profiles, the names and the backup list. Public so the module
    /// host and the tests can await it instead of racing the constructor's fire-and-forget load.</summary>
    public async Task LoadAsync()
    {
        try
        {
            IsBusy = true;
            if (string.IsNullOrWhiteSpace(InstallRoot))
                InstallRoot = await _preferences.LoadInstallRootAsync() ?? EveSettingsLocator.DefaultInstallRoot() ?? string.Empty;

            CheckClients();
            await ReloadProfilesAsync();
        }
        catch (Exception ex)
        {
            _Fail($"Could not read the EVE settings folder: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _NotifyGates();
        }
    }

    public void RefreshModule() => _ = LoadAsync();

    /// <summary>The user pointed at the EVE folder themselves — remember it, so auto-detection failing once does not
    /// mean picking it again every session.</summary>
    public async Task PickInstallRootAsync(string installRoot)
    {
        InstallRoot = installRoot;
        await _preferences.SaveInstallRootAsync(installRoot);
        await ReloadProfilesAsync();
    }

    /// <summary>Re-scan the install directory. Also the RELOAD button: EVE writes these files on logout, so the
    /// timestamps on screen go stale while the tool stands open.</summary>
    [RelayCommand]
    public async Task ReloadProfilesAsync()
    {
        var previous = SelectedProfileName;
        _profiles = EveSettingsLocator.LoadProfiles(InstallRoot).ToList();

        ProfileNames.Clear();
        foreach (var profile in _profiles)
            ProfileNames.Add(profile.Name);

        // Selecting a different profile starts the load through the change notification; re-selecting the same one
        // raises nothing, so that case starts it here. Either way there is exactly one load to await.
        var target = previous is not null && ProfileNames.Contains(previous) ? previous : ProfileNames.FirstOrDefault();
        if (SelectedProfileName == target)
            _profileLoad = _LoadSelectedProfileAsync();
        else
            SelectedProfileName = target;

        await _profileLoad;
        _LoadBackups();

        if (_profiles.Count == 0)
            Status = string.IsNullOrWhiteSpace(InstallRoot)
                ? "No EVE settings folder found. Use BROWSE to point at the folder that holds the settings_* directories."
                : $"No settings_* profiles found under {InstallRoot}.";
    }

    private Task _profileLoad = Task.CompletedTask;

    partial void OnSelectedProfileNameChanged(string? value) => _profileLoad = _LoadSelectedProfileAsync();

    /// <param name="announce">False after a sync or a restore: the outcome line has just said what happened, and
    /// the reload that follows must not overwrite it with "profile loaded".</param>
    private async Task _LoadSelectedProfileAsync(bool announce = true)
    {
        Characters.Clear();
        Accounts.Clear();
        CharacterSource = null;
        AccountSource = null;

        var profile = _SelectedProfile();
        if (profile is null)
        {
            _NotifyProfileChanged();
            return;
        }

        _names = await _nameResolver.ResolveAsync(profile);

        foreach (var file in profile.Characters.OrderBy(f => _names.CharacterName(f.Id), StringComparer.OrdinalIgnoreCase))
            Characters.Add(_Row(file));

        foreach (var file in profile.Accounts.OrderBy(f => _names.AccountName(f.Id), StringComparer.OrdinalIgnoreCase))
            Accounts.Add(_Row(file));

        _NotifyProfileChanged();
        if (!announce)
            return;

        Status = $"{profile.Name}: {Characters.Count} characters, {Accounts.Count} accounts. Nothing is written until you press a copy button.";
        StatusIsError = false;
    }

    private SettingsFileRowViewModel _Row(EveSettingsFile file)
    {
        var row = new SettingsFileRowViewModel(file, _names.DisplayName(file),
            file.Kind == SettingsFileKind.Account ? _names.AccountHint(file.Id) : [])
        {
            NeedsName = file.Kind == SettingsFileKind.Account && !_names.HasAccountName(file.Id)
        };
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsFileRowViewModel.IsTarget))
                _NotifyGates();
        };
        return row;
    }

    // ── Running clients ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-checks for running EVE clients. Two signals: the processes themselves (a client parked on the login
    /// screen counts — it still rewrites its files on exit) and the characters the client sweep sees in-game, which
    /// is what lets the warning name names instead of counting windows.
    /// </summary>
    [RelayCommand]
    public void CheckClients()
    {
        if (_presence is null)
        {
            ClientsRunning = false;
            ClientWarning = string.Empty;
            _NotifyGates();
            return;
        }

        var processes = _presence.RunningClientCount();
        var inGame = _presence.Current.CharacterNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();

        ClientsRunning = processes > 0 || inGame.Count > 0;
        ClientWarning = ClientsRunning
            ? _ClientWarningText(processes, inGame)
            : string.Empty;
        _NotifyGates();
    }

    private static string _ClientWarningText(int processes, IReadOnlyList<string> inGame)
    {
        var who = inGame.Count > 0 ? $" ({string.Join(", ", inGame)})" : string.Empty;
        var count = Math.Max(processes, inGame.Count);
        var clients = count == 1 ? "1 EVE client is" : $"{count} EVE clients are";
        return $"{clients} running{who}. EVE writes its settings when it closes, so anything copied now is " +
               "overwritten again at logout. Close every client, then check again.";
    }

    // ── Target selection ─────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void SelectAllCharacters() => _SetAllTargets(Characters, true);

    [RelayCommand]
    private void ClearCharacters() => _SetAllTargets(Characters, false);

    [RelayCommand]
    private void SelectAllAccounts() => _SetAllTargets(Accounts, true);

    [RelayCommand]
    private void ClearAccounts() => _SetAllTargets(Accounts, false);

    private void _SetAllTargets(IEnumerable<SettingsFileRowViewModel> rows, bool ticked)
    {
        foreach (var row in rows.Where(row => row.CanBeTarget))
            row.IsTarget = ticked;
        _NotifyGates();
    }

    partial void OnCharacterSourceChanged(SettingsFileRowViewModel? value) => _MarkSource(Characters, value);

    partial void OnAccountSourceChanged(SettingsFileRowViewModel? value) => _MarkSource(Accounts, value);

    private void _MarkSource(IEnumerable<SettingsFileRowViewModel> rows, SettingsFileRowViewModel? source)
    {
        foreach (var row in rows)
            row.IsSource = ReferenceEquals(row, source);
        _NotifyGates();
    }

    // ── Naming an account ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gives an account the name the user knows it by. EVE exposes no account name anywhere — not in the settings
    /// folder, not over ESI — so this is the only way an account row can read as anything other than a number. The
    /// name is remembered per account id and survives restarts.
    /// </summary>
    [RelayCommand]
    private async Task NameAccountAsync(SettingsFileRowViewModel? row)
    {
        if (row is null || row.Kind != SettingsFileKind.Account || _dialogs is null)
            return;

        var hint = row.HasHint
            ? $"EVE gives accounts no name. Last saved together with {string.Join(", ", row.AccountHint)}."
            : "EVE gives accounts no name, so pick one you will recognise.";
        var current = row.NeedsName ? string.Empty : row.DisplayName;

        var chosen = await _dialogs.PromptTextAsync("Name this account", hint, current);
        if (string.IsNullOrWhiteSpace(chosen))
            return;

        await _preferences.SaveAccountNameAsync(row.Id, chosen);
        _names.SetAccountName(row.Id, chosen.Trim());
        row.DisplayName = chosen.Trim();
        row.NeedsName = false;
        _NotifyGates();
    }

    // ── Syncing ──────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private Task SyncCharactersAsync() => _SyncAsync(SettingsFileKind.Character);

    [RelayCommand]
    private Task SyncAccountsAsync() => _SyncAsync(SettingsFileKind.Account);

    private async Task _SyncAsync(SettingsFileKind kind)
    {
        var plan = BuildPlan(kind);
        if (plan is null)
        {
            _Fail(kind == SettingsFileKind.Character
                ? "Choose a character to copy from and tick at least one character to copy to."
                : "Choose an account to copy from and tick at least one account to copy to.");
            return;
        }

        CheckClients();
        if (ClientsRunning)
        {
            _Fail(ClientWarning);
            return;
        }

        if (_dialogs is not null)
        {
            var confirmed = await _dialogs.ConfirmAsync(
                "Overwrite these settings?",
                $"{plan.SourceName}'s {_KindWord(kind)} settings will overwrite {plan.FileCount} " +
                $"{_KindWord(kind, plan.FileCount)} in {plan.Profile.Name}:\n\n" +
                $"{string.Join("\n", plan.TargetNames.Select(name => "  • " + name))}\n\n" +
                $"The whole profile — {_ProfileContents()} — is backed up first, to {_backups.RootDirectory}.",
                okText: "Copy");
            if (!confirmed)
                return;
        }

        try
        {
            IsBusy = true;
            var names = _names.AsLookup();
            var outcome = await Task.Run(() => _sync.Apply(plan, names));
            _Report(outcome, plan);
        }
        finally
        {
            IsBusy = false;
        }

        await _LoadSelectedProfileAsync(announce: false);   // the timestamps on screen just changed
        _LoadBackups();
        _NotifyGates();
    }

    /// <summary>
    /// The intended copy for one block, or null when it is not complete. Kept public and free of side effects: it is
    /// what the preview line reads, what the confirmation shows, and what an automatic sync (ET-60) would build.
    /// </summary>
    public SettingsSyncPlan? BuildPlan(SettingsFileKind kind)
    {
        var profile = _SelectedProfile();
        var source = kind == SettingsFileKind.Character ? CharacterSource : AccountSource;
        if (profile is null || source is null)
            return null;

        var targets = _TargetsOf(kind);
        if (targets.Count == 0)
            return null;

        return new SettingsSyncPlan(
            profile, InstallRoot, source.File, source.DisplayName,
            targets.Select(row => row.File).ToList(),
            targets.Select(row => row.DisplayName).ToList());
    }

    private void _Report(Result<SettingsSyncOutcome> outcome, SettingsSyncPlan plan)
    {
        if (!outcome.IsSuccess || outcome.Value is null)
        {
            _Fail(string.Join(" ", outcome.Messages.Select(message => message.Text)));
            return;
        }

        var value = outcome.Value;
        var copied = $"Copied {plan.SourceName}'s {_KindWord(plan.Kind)} settings to {value.Copied.Count} " +
                     $"{_KindWord(plan.Kind, value.Copied.Count)}: {string.Join(", ", value.Copied)}.";
        var failed = value.Failed.Count == 0
            ? string.Empty
            : $" Not copied: {string.Join("; ", value.Failed)}.";

        Status = copied + failed +
                 $" Backed up {plan.Profile.Name} ({value.Backup.Manifest.ContentsSummary}) to {value.Backup.DirectoryPath}.";
        StatusIsError = value.Failed.Count > 0;
    }

    // ── Backups ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Snapshots the profile on demand — the same snapshot a sync takes, without a sync.</summary>
    [RelayCommand]
    private async Task BackupNowAsync()
    {
        var profile = _SelectedProfile();
        if (profile is null)
            return;

        try
        {
            IsBusy = true;
            var names = _names.AsLookup();
            var result = await Task.Run(() =>
                _backups.Create(profile, InstallRoot, names, BackupReason.Manual, "backed up on request"));

            if (result.IsSuccess && result.Value is not null)
            {
                Status = $"Backed up {profile.Name}: {result.Value.Manifest.ContentsSummary} " +
                         $"({result.Value.Manifest.Entries.Count} files) to {result.Value.DirectoryPath}.";
                StatusIsError = false;
            }
            else
            {
                _Fail(string.Join(" ", result.Messages.Select(message => message.Text)));
            }
        }
        finally
        {
            IsBusy = false;
        }

        _LoadBackups();
        _NotifyGates();
    }

    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        var row = SelectedBackup;
        if (row is null || !row.CanRestore)
            return;

        CheckClients();
        if (ClientsRunning)
        {
            _Fail(ClientWarning);
            return;
        }

        if (_dialogs is not null)
        {
            var confirmed = await _dialogs.ConfirmAsync(
                "Restore this backup?",
                $"The backup of {row.TakenAtDisplay} — {row.Backup.Manifest.ContentsSummary}, " +
                $"{row.Backup.Manifest.Entries.Count} files — will overwrite {row.ProfileName}. The profile's " +
                "current state is backed up first, so this is undoable too.",
                okText: "Restore");
            if (!confirmed)
                return;
        }

        try
        {
            IsBusy = true;
            var names = _names.AsLookup();
            var result = await Task.Run(() => _backups.Restore(row.Backup, names));

            if (result.IsSuccess && result.Value is not null)
            {
                var failed = result.Value.Failed.Count == 0
                    ? string.Empty
                    : $" Not restored: {string.Join("; ", result.Value.Failed)}.";
                Status = $"Restored {result.Value.Restored.Count} files into {row.ProfileName}.{failed} " +
                         $"The state from before this restore is in {result.Value.SafetyBackupDirectory}.";
                StatusIsError = result.Value.Failed.Count > 0;
            }
            else
            {
                _Fail(string.Join(" ", result.Messages.Select(message => message.Text)));
            }
        }
        finally
        {
            IsBusy = false;
        }

        await _LoadSelectedProfileAsync(announce: false);
        _LoadBackups();
        _NotifyGates();
    }

    [RelayCommand]
    private async Task DeleteBackupAsync()
    {
        var row = SelectedBackup;
        if (row is null)
            return;

        if (_dialogs is not null &&
            !await _dialogs.ConfirmAsync("Delete this backup?",
                $"The backup of {row.ProfileName} taken on {row.TakenAtDisplay} is removed from disk. This cannot be undone."))
            return;

        var result = _backups.Delete(row.Backup);
        if (!result.IsSuccess)
            _Fail(string.Join(" ", result.Messages.Select(message => message.Text)));
        else
            Status = $"Deleted the backup of {row.ProfileName} taken on {row.TakenAtDisplay}.";

        _LoadBackups();
        _NotifyGates();
    }

    private void _LoadBackups()
    {
        var selectedId = SelectedBackup?.Backup.Id;
        Backups.Clear();
        foreach (var backup in _backups.List())
            Backups.Add(new SettingsBackupRowViewModel(backup));

        SelectedBackup = Backups.FirstOrDefault(row => row.Backup.Id == selectedId) ?? Backups.FirstOrDefault();
        OnPropertyChanged(nameof(HasBackups));
    }

    // ── Small shared pieces ──────────────────────────────────────────────────────────────────────

    private EveSettingsProfile? _SelectedProfile() =>
        _profiles.FirstOrDefault(profile => profile.Name == SelectedProfileName);

    private List<SettingsFileRowViewModel> _TargetsOf(SettingsFileKind kind)
    {
        var rows = kind == SettingsFileKind.Character ? Characters : Accounts;
        var source = kind == SettingsFileKind.Character ? CharacterSource : AccountSource;
        return source is null
            ? []
            : rows.Where(row => row.IsTarget && !ReferenceEquals(row, source)).ToList();
    }

    private string _PlanSummary(SettingsFileKind kind)
    {
        var source = kind == SettingsFileKind.Character ? CharacterSource : AccountSource;
        if (source is null)
            return $"Choose the {_KindWord(kind)} to copy from.";

        var targets = _TargetsOf(kind);
        if (targets.Count == 0)
            return $"Tick the {_KindWord(kind, 2)} that should take {source.DisplayName}'s settings.";

        return $"{source.DisplayName} → {string.Join(", ", targets.Select(row => row.DisplayName))} " +
               $"({targets.Count} files). The whole profile — {_ProfileContents()} — is backed up first.";
    }

    // What a backup of the selected profile would cover, in both kinds — the answer to "did it back up my
    // account data too?", given before the question is asked.
    private string _ProfileContents() => _SelectedProfile() is { } profile
        ? $"{profile.Characters.Count} {_KindWord(SettingsFileKind.Character, profile.Characters.Count)} " +
          $"and {profile.Accounts.Count} {_KindWord(SettingsFileKind.Account, profile.Accounts.Count)}"
        : "every character and account in it";

    private static string _KindWord(SettingsFileKind kind, int count = 1) => kind switch
    {
        SettingsFileKind.Character => count == 1 ? "character" : "characters",
        _ => count == 1 ? "account" : "accounts"
    };

    private void _Fail(string message)
    {
        Status = message;
        StatusIsError = true;
    }

    private void _NotifyProfileChanged()
    {
        OnPropertyChanged(nameof(ProfileSummary));
        OnPropertyChanged(nameof(HasProfile));
        _NotifyGates();
    }

    private void _NotifyGates()
    {
        OnPropertyChanged(nameof(CharacterPlanSummary));
        OnPropertyChanged(nameof(AccountPlanSummary));
        OnPropertyChanged(nameof(CanSyncCharacters));
        OnPropertyChanged(nameof(CanSyncAccounts));
        OnPropertyChanged(nameof(CanBackup));
        OnPropertyChanged(nameof(CanRestore));
    }

    partial void OnIsBusyChanged(bool value) => _NotifyGates();

    partial void OnClientsRunningChanged(bool value) => _NotifyGates();

    partial void OnSelectedBackupChanged(SettingsBackupRowViewModel? value) => OnPropertyChanged(nameof(CanRestore));
}
