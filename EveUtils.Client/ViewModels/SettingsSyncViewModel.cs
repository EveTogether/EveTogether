using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
public partial class SettingsSyncViewModel : ViewModelBase, IRefreshableModule, IDisposable
{
    private readonly SettingsSyncService _sync;
    private readonly SettingsBackupService _backups;
    private readonly SettingsPresetService _presets;
    private readonly EveSettingsNameResolver _nameResolver;
    private readonly EveSettingsPreferences _preferences;
    private readonly EveClientPresenceService? _presence;
    private readonly IEveSettingsWatch? _watch;
    private readonly IDialogService? _dialogs;
    private readonly IDisposable? _subscription;

    private List<EveSettingsProfile> _profiles = [];
    private EveSettingsNames _names = EveSettingsNames.Empty;

    public SettingsSyncViewModel(
        SettingsSyncService sync,
        SettingsBackupService backups,
        SettingsPresetService presets,
        EveSettingsNameResolver nameResolver,
        EveSettingsPreferences preferences,
        EveClientPresenceService? presence = null,
        IEveSettingsWatch? watch = null,
        IDialogService? dialogs = null)
    {
        _sync = sync;
        _backups = backups;
        _presets = presets;
        _nameResolver = nameResolver;
        _preferences = preferences;
        _presence = presence;
        _watch = watch;
        _dialogs = dialogs;
        BackupRoot = backups.RootDirectory;

        // One subscription for everything happening underneath this screen: clients opening and closing, and the
        // automatic sync writing files. Without it the screen shows whatever was true when it was opened — which is
        // how an automatic backup came to be made behind a banner insisting a client was running (ET-68).
        _subscription = watch?.Subscribe(_OnChanged);

        _ = LoadAsync();
    }

    public void Dispose() => _subscription?.Dispose();

    private void _OnChanged(EveSettingsChange change)
    {
        _ApplyClients(change.Clients);
        if (change.Kind is not (EveSettingsChangeKind.Sync or EveSettingsChangeKind.Backups))
            return;

        _LoadBackups();
        if (change.Run is { } run)
        {
            // Say it straight away rather than after a round-trip to the settings store: this line existing at all
            // is the point, and the fuller history follows below.
            AutoSyncLastRun = $"Last run {_Moment(run.AtUtc)} — {run.Summary}";
            Status = $"Automatic sync at {_Moment(run.AtUtc)}: {run.Summary}";
            StatusIsError = run.Failed;
        }
        else if (change.Kind == EveSettingsChangeKind.Sync)
        {
            // A restore in the backups window, say. Something else wrote these files; saying so beats silently
            // swapping the timestamps under the user.
            Status = "The settings on disk changed elsewhere, so this screen has been re-read.";
            StatusIsError = false;
        }

        if (change.Kind == EveSettingsChangeKind.Sync)
            _ = _RefreshAfterWriteAsync();
        else
            _NotifyGates();
    }

    private async Task _RefreshAfterWriteAsync()
    {
        await _LoadSelectedProfileAsync(announce: false);   // the write times on screen just changed
        await LoadAutoSyncAsync();
        _LoadBackups();
        _NotifyGates();
    }

    // ── Where the settings are ───────────────────────────────────────────────────────────────────

    /// <summary>The EVE install directory that holds the settings_* profiles. Editable: auto-detection covers the
    /// Windows install and a Linux one inside its Proton/Wine prefix, and when it comes up empty (or the folder
    /// moved) the user points at it themselves.</summary>
    [ObservableProperty] private string _installRoot = string.Empty;

    /// <summary>Where AUTODETECT looks. Only a test replaces it, so that both outcomes can be driven without
    /// depending on whether the machine running the test happens to have EVE installed.</summary>
    internal Func<string?> Detector { get; set; } = EveSettingsLocator.DefaultInstallRoot;

    public ObservableCollection<string> ProfileNames { get; } = [];

    [ObservableProperty] private string? _selectedProfileName;

    /// <summary>Where the backups are kept, spelled out so the user never has to take our word for it.</summary>
    public string BackupRoot { get; }

    // ── What is in the selected profile ──────────────────────────────────────────────────────────

    public ObservableCollection<SettingsFileRowViewModel> Characters { get; } = [];

    public ObservableCollection<SettingsFileRowViewModel> Accounts { get; } = [];

    [ObservableProperty] private SettingsFileRowViewModel? _characterSource;

    [ObservableProperty] private SettingsFileRowViewModel? _accountSource;

    /// <summary>How many backups this screen shows. The rest live in the backups window, which has room for them.</summary>
    public const int RecentBackupCount = 2;

    /// <summary>The newest couple of snapshots — evidence that they are being taken, not a place to work with them.</summary>
    public ObservableCollection<SettingsBackupRowViewModel> RecentBackups { get; } = [];

    /// <summary>When the last backup was taken and what it covers, or that there are none yet.</summary>
    [ObservableProperty] private string _lastBackupDisplay = "No backups yet.";

    [ObservableProperty] private int _backupCount;

    // ── What the screen is telling the user ──────────────────────────────────────────────────────

    [ObservableProperty] private string _status = "Pick a profile, choose who to copy from, tick who to copy to.";

    [ObservableProperty] private bool _statusIsError;

    [ObservableProperty] private bool _isBusy;

    /// <summary>What is taking the time, while it takes it — shown beside a progress bar so a slow backup reads as
    /// work in progress rather than as a button that did nothing.</summary>
    [ObservableProperty] private string _busyMessage = string.Empty;

    /// <summary>The running-client verdict, e.g. "2 EVE clients are running (Jithran, Lyra Custos)."</summary>
    [ObservableProperty] private string _clientWarning = string.Empty;

    [ObservableProperty] private bool _clientsRunning;

    public string ProfileSummary => _SelectedProfile() is { } profile
        ? $"{profile.Name} — {profile.Characters.Count} characters, {profile.Accounts.Count} accounts"
        : "No profile selected.";

    public bool HasProfile => _SelectedProfile() is not null;

    public bool HasBackups => BackupCount > 0;

    /// <summary>What the character block would do if pressed right now — shown above the button, not after it.</summary>
    public string CharacterPlanSummary => _PlanSummary(SettingsFileKind.Character);

    public string AccountPlanSummary => _PlanSummary(SettingsFileKind.Account);

    public bool CanSyncCharacters => !ClientsRunning && !IsBusy && _TargetsOf(SettingsFileKind.Character).Count > 0;

    public bool CanSyncAccounts => !ClientsRunning && !IsBusy && _TargetsOf(SettingsFileKind.Account).Count > 0;

    public bool CanBackup => !IsBusy && HasProfile;


    // ── Loading ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Re-reads the install directory, its profiles, the names and the backup list. Public so the module
    /// host and the tests can await it instead of racing the constructor's fire-and-forget load.</summary>
    public async Task LoadAsync()
    {
        try
        {
            IsBusy = true;
            if (string.IsNullOrWhiteSpace(InstallRoot))
                InstallRoot = await _preferences.LoadInstallRootAsync() ?? Detector() ?? string.Empty;

            CheckClients();
            await ReloadProfilesAsync();
            await LoadAutoSyncAsync();
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

    /// <summary>
    /// The AUTODETECT button (ET-76). Runs the search again and fills the field in with what it finds — on Linux
    /// that means walking the Steam libraries and their Proton prefixes, which is worth a button because a game
    /// installed or moved after the tool was first opened would otherwise never be noticed.
    ///
    /// Finding nothing changes nothing: the path already in the field stays, and the line underneath points at
    /// BROWSE… — auto-detection is the shortcut here, not the only way in.
    /// </summary>
    [RelayCommand]
    public async Task DetectInstallRootAsync()
    {
        // Off the UI thread: this stats its way through every Steam library on the machine, and one of them can be
        // a disk that has to spin up first.
        var detected = await Task.Run(Detector);
        if (string.IsNullOrWhiteSpace(detected))
        {
            _Fail("No EVE folder found automatically. Use BROWSE… to point at the folder that holds the settings_* directories.");
            return;
        }

        await PickInstallRootAsync(detected);
        Status = $"Found EVE's settings at {detected}. Nothing is written until you press a copy button.";
        StatusIsError = false;
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
                ? "No EVE settings folder found. Press AUTODETECT, or use BROWSE… to point at the folder that holds the settings_* directories."
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

        // Every profile, not just this one: which characters sit on which account is often only visible in another
        // profile, and an account belongs to a character whichever folder made that plain (ET-64).
        _names = await _nameResolver.ResolveAsync(profile, _profiles);

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
        var isAccount = file.Kind == SettingsFileKind.Account;
        var row = new SettingsFileRowViewModel(file, _names.DisplayName(file),
            isAccount ? _names.AccountCharacters(file.Id) : [],
            isAccount ? _names.LinkOrigin(file.Id) : null)
        {
            NeedsName = isAccount && !_names.HasAccountName(file.Id)
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
    public void CheckClients() => _ApplyClients(_watch?.ProbeClients() ?? _ProbeWithoutWatch());

    // The banner is set here and nowhere else, whether the answer came from the button or from an announcement —
    // two ways of writing it is how one of them ends up stale.
    private void _ApplyClients(EveClientPresenceSnapshot clients)
    {
        ClientsRunning = clients.AnyRunning;
        ClientWarning = clients.AnyRunning ? _ClientWarningText(clients) : string.Empty;
        _NotifyGates();
    }

    // Only for a tool built without the watch (a test, or a host that wired neither).
    private EveClientPresenceSnapshot _ProbeWithoutWatch() => _presence is null
        ? EveClientPresenceSnapshot.None
        : new EveClientPresenceSnapshot(_presence.RunningClientCount(),
            _presence.Current.CharacterNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList());

    private static string _ClientWarningText(EveClientPresenceSnapshot clients)
    {
        var who = clients.InGame.Count > 0 ? $" ({string.Join(", ", clients.InGame)})" : string.Empty;
        var running = clients.Count == 1 ? "1 EVE client is" : $"{clients.Count} EVE clients are";
        return $"{running} running{who}. EVE writes its settings when it closes, so anything copied now is " +
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
            ? $"EVE gives accounts no name. This one holds {string.Join(", ", row.AccountCharacters)}."
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

    /// <summary>
    /// Says which characters are on an account (ET-64). The tool works this out itself where EVE's write times allow
    /// it, but a machine where every client is closed at once leaves nothing to work with — and the user knows the
    /// answer anyway. What they state here outranks anything inferred and is never rewritten by a later guess.
    /// </summary>
    [RelayCommand]
    private async Task LinkAccountCharactersAsync(SettingsFileRowViewModel? row)
    {
        if (row is null || row.Kind != SettingsFileKind.Account || _dialogs is null)
            return;

        // Every character in any profile plus every one we have a name for — not only this profile's, since a pilot
        // can be missing here and still be on this account, and one whose name never resolved still has to be
        // pickable rather than quietly absent.
        var known = _profiles.SelectMany(profile => profile.Characters).Select(file => file.Id)
            .Concat(_names.CharacterNames.Keys)
            .Distinct()
            .Where(id => id is > 0 and <= int.MaxValue)
            .Select(id => (Id: id, Name: _names.CharacterName(id)))
            .OrderBy(pair => pair.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (known.Count == 0)
        {
            _Fail("No characters are known yet, so there is nothing to put on this account.");
            return;
        }

        var owned = _names.Link(row.Id)?.CharacterIds ?? [];
        var options = known.Select(pair => new CharacterPickOption((int)pair.Id, pair.Name,
            owned.Contains(pair.Id) ? "on this account" : string.Empty, true)).ToList();

        var chosen = await _dialogs.PickCharactersAsync(
            $"Which characters are on {(row.NeedsName ? "this account" : row.DisplayName)} ({row.Id})?", options);
        if (chosen is null)
            return;

        var ids = chosen.Select(id => (long)id).ToList();
        _names.SetAccountCharacters(row.Id, ids, DateTimeOffset.UtcNow);
        await _preferences.SaveAccountLinksAsync(_names.AllLinks);   // every account, not only this profile's

        row.AccountCharacters = ids.Select(_names.CharacterName).ToList();
        row.LinkOrigin = ids.Count > 0 ? AccountLinkOrigin.UserSet : null;
        Status = ids.Count == 0
            ? $"Cleared the characters on account {row.Id}."
            : $"Account {(row.NeedsName ? row.Id.ToString(CultureInfo.InvariantCulture) : row.DisplayName)} holds {string.Join(", ", row.AccountCharacters)}.";
        StatusIsError = false;
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

    // ── Keeping it in step by itself (ET-60) ─────────────────────────────────────────────────────

    /// <summary>Off unless the user turned it on, and it cannot be turned on before there is a rule to run.</summary>
    [ObservableProperty] private bool _autoSyncEnabled;

    /// <summary>What is remembered, in names — the same choice as the blocks above, only kept.</summary>
    [ObservableProperty] private string _autoSyncRuleSummary =
        "Nothing set yet — REMEMBER THIS keeps whatever you pick on the left.";

    [ObservableProperty] private bool _hasAutoSyncRule;

    [ObservableProperty] private string _autoSyncLastRun = "It has not run yet.";

    /// <summary>The recent runs, for the tooltip: an unattended tool that touches files has to leave a record.</summary>
    [ObservableProperty] private string _autoSyncHistory = string.Empty;

    /// <summary>
    /// What switching this on actually means, said as a sentence rather than as a label on a switch. It has to
    /// name three things the user cannot otherwise know: that it is a sync of the selection on the left, that it
    /// only happens while EVE Together itself keeps running (which is why closing your clients is the trigger at
    /// all), and how often it looks — an interval nobody can guess is an interval nobody can trust.
    /// </summary>
    public static string AutoSyncExplanation { get; } =
        $"While EVE Together keeps running, it checks every {_Seconds(AutoSettingsSyncService.PollInterval)} whether " +
        "every EVE client is closed — and then repeats the copy below on its own.";

    /// <summary>The rest of the rules, on the tooltip: true and worth knowing, but not worth a paragraph in a
    /// column this narrow.</summary>
    public static string AutoSyncDetail { get; } =
        AutoSyncExplanation + $" It waits until the clients have been closed for " +
        $"{_Seconds(AutoSettingsSyncService.SettleDelay)}, so EVE has finished writing its files; it does nothing " +
        "while the files already match; and it stops part-way if you start a client.";

    private static string _Seconds(TimeSpan span) => $"{(int)span.TotalSeconds} seconds";

    private AutoSyncSettings _autoSync = AutoSyncSettings.None;
    private bool _applyingAutoSync;

    /// <summary>There has to be something selected to remember: the automatic rule is exactly what the two blocks
    /// are showing, never a second, invisible selection.</summary>
    public bool CanRememberAutoSync =>
        !IsBusy && (BuildPlan(SettingsFileKind.Character) is not null || BuildPlan(SettingsFileKind.Account) is not null);

    public async Task LoadAutoSyncAsync()
    {
        _autoSync = await _preferences.LoadAutoSyncAsync();
        _applyingAutoSync = true;
        AutoSyncEnabled = _autoSync.Enabled && _autoSync.IsConfigured;
        _applyingAutoSync = false;

        HasAutoSyncRule = _autoSync.IsConfigured;
        AutoSyncRuleSummary = _DescribeAutoSync(_autoSync);

        var history = await _preferences.LoadAutoSyncHistoryAsync();
        AutoSyncLastRun = history.Count == 0
            ? "It has not run yet."
            : $"Last run {_Moment(history[0].AtUtc)} — {history[0].Summary}";
        AutoSyncHistory = history.Count == 0
            ? "No automatic sync has run yet."
            : string.Join("\n", history.Take(10).Select(run => $"{_Moment(run.AtUtc)} — {run.Summary}"));
        _NotifyGates();
    }

    /// <summary>Stores what the two blocks are showing as the standing instruction, and switches it on. One button:
    /// there is no second place to configure this, so there is nothing to keep in step with what is on screen.</summary>
    [RelayCommand]
    private async Task RememberAutoSyncAsync()
    {
        var profile = _SelectedProfile();
        if (profile is null)
            return;

        var characters = BuildPlan(SettingsFileKind.Character);
        var accounts = BuildPlan(SettingsFileKind.Account);
        if (characters is null && accounts is null)
        {
            _Fail("Pick a source and tick at least one target first — that is what gets remembered.");
            return;
        }

        _autoSync = new AutoSyncSettings
        {
            Enabled = true,
            InstallRoot = InstallRoot,
            ProfileName = profile.Name,
            CharacterSourceId = characters?.Source.Id,
            CharacterTargetIds = characters?.Targets.Select(file => file.Id).ToList() ?? [],
            AccountSourceId = accounts?.Source.Id,
            AccountTargetIds = accounts?.Targets.Select(file => file.Id).ToList() ?? []
        };
        await _preferences.SaveAutoSyncAsync(_autoSync);

        _applyingAutoSync = true;
        AutoSyncEnabled = true;
        _applyingAutoSync = false;
        HasAutoSyncRule = true;
        AutoSyncRuleSummary = _DescribeAutoSync(_autoSync);
        Status = $"Remembered: {AutoSyncRuleSummary} It runs by itself once every EVE client is closed, backs the " +
                 $"whole profile up first, and does nothing while the files already match.";
        StatusIsError = false;
        _NotifyGates();
    }

    /// <summary>Throws the standing instruction away — off is off, not "off but still remembered".</summary>
    [RelayCommand]
    private async Task ForgetAutoSyncAsync()
    {
        _autoSync = AutoSyncSettings.None;
        await _preferences.SaveAutoSyncAsync(_autoSync);

        _applyingAutoSync = true;
        AutoSyncEnabled = false;
        _applyingAutoSync = false;
        HasAutoSyncRule = false;
        AutoSyncRuleSummary = "Nothing set yet — REMEMBER THIS keeps whatever you pick on the left.";
        Status = "The automatic sync is off and its rule is forgotten.";
        StatusIsError = false;
        _NotifyGates();
    }

    partial void OnAutoSyncEnabledChanged(bool value)
    {
        if (_applyingAutoSync)
            return;

        if (value && !_autoSync.IsConfigured)
        {
            _applyingAutoSync = true;
            AutoSyncEnabled = false;
            _applyingAutoSync = false;
            _Fail("There is nothing to run yet — pick a source and targets, then press REMEMBER THIS.");
            return;
        }

        _autoSync = _autoSync with { Enabled = value };
        _ = _preferences.SaveAutoSyncAsync(_autoSync);
        Status = value
            ? "Automatic sync is on. It waits until every EVE client is closed."
            : "Automatic sync is off.";
        StatusIsError = false;
    }

    private string _DescribeAutoSync(AutoSyncSettings settings)
    {
        if (!settings.IsConfigured)
            return "Nothing set yet — REMEMBER THIS keeps whatever you pick on the left.";

        var parts = new List<string>();
        if (settings.HasCharacterRule)
            parts.Add($"{_NameOf(settings.CharacterSourceId!.Value, SettingsFileKind.Character)} → " +
                      _Targets(settings.CharacterTargetIds, SettingsFileKind.Character));
        if (settings.HasAccountRule)
            parts.Add($"{_NameOf(settings.AccountSourceId!.Value, SettingsFileKind.Account)} → " +
                      _Targets(settings.AccountTargetIds, SettingsFileKind.Account));

        return $"{string.Join(" · ", parts)} in {settings.ProfileName}.";
    }

    // Names while there are few enough to read; a count beyond that. What the rule does has to be legible at a
    // glance — it is the thing that will happen without anyone watching.
    private string _Targets(IReadOnlyList<long> ids, SettingsFileKind kind) => ids.Count <= 3
        ? string.Join(", ", ids.Select(id => _NameOf(id, kind)))
        : $"{ids.Count} {_KindWord(kind, ids.Count)}";

    // Names when the remembered profile is the one on screen; the id otherwise — better a number than a name
    // borrowed from a different profile's file of the same id.
    private string _NameOf(long id, SettingsFileKind kind)
    {
        var rows = kind == SettingsFileKind.Character ? Characters : Accounts;
        return rows.FirstOrDefault(row => row.Id == id)?.DisplayName ?? id.ToString(CultureInfo.InvariantCulture);
    }

    private static string _Moment(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    // ── Presets: carrying settings to another machine (ET-61) ────────────────────────────────────

    /// <summary>Saves a preset — the user picks what goes in, names it, and writes it to one file.</summary>
    [RelayCommand]
    private async Task ExportPresetAsync()
    {
        var profile = _SelectedProfile();
        if (profile is null || _dialogs is null)
        {
            _Fail("Pick a profile first — a preset is made from what is in one.");
            return;
        }

        await _dialogs.ShowPresetExportAsync(new PresetExportViewModel(_presets, profile, InstallRoot, _names));
    }

    /// <summary>Reads a preset in. The dialog shows what it would do, line by line, before anything is written.</summary>
    [RelayCommand]
    private async Task ImportPresetAsync()
    {
        var profile = _SelectedProfile();
        if (profile is null || _dialogs is null)
        {
            _Fail("Pick the profile to import into first. If EVE has never run here, start it once so it creates one.");
            return;
        }

        var applied = await _dialogs.ShowPresetImportAsync(
            new PresetImportViewModel(_presets, profile, InstallRoot, _names, _presence));
        if (!applied)
            return;

        await ReloadProfilesAsync();   // new files, new write times — and the account links may have grown
        Status = $"Imported a preset into {profile.Name}. Use the two blocks above to copy it onto your other " +
                 "characters and accounts.";
        StatusIsError = false;
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
            // A whole profile is a noticeable wait, and a button that shows nothing looks like a button that did
            // nothing — which is how you get a second click.
            BusyMessage = $"Backing up {profile.Name} — {profile.Characters.Count + profile.Accounts.Count} files…";
            IsBusy = true;
            var names = _names.AsLookup();
            var result = await Task.Run(() =>
                _backups.Create(profile, InstallRoot, names, BackupReason.Manual, "backed up on request"));

            if (result.IsSuccess && result.Value is not null)
            {
                Status = $"Backed up {profile.Name}: {result.Value.Manifest.ContentsSummary} " +
                         $"({result.Value.Manifest.Entries.Count} files) to {result.Value.DirectoryPath}.";
                StatusIsError = false;
                _watch?.Announce(new EveSettingsChange(EveSettingsChangeKind.Backups, _watch.ProbeClients()));
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

    /// <summary>
    /// Opens the backups in their own window (ET-67). Reading a backup and putting one back needs room this screen
    /// does not have: crammed in beside the two file lists, a single backup's contents were a few lines on a
    /// maximised window. Here only the last two and "back up now" stay behind.
    /// </summary>
    [RelayCommand]
    private void OpenBackups()
    {
        if (_dialogs is null)
            return;

        // A restore over there changes the profile this screen is showing, and a backup taken here shows up in that
        // list — both travel over the same announcement every screen already listens to, so neither window needs a
        // hand-off to the other.
        _dialogs.ShowSettingsBackups(
            new SettingsBackupsViewModel(_backups, _names.AsLookup(), _watch, _preferences, _dialogs));
    }

    // Only the newest two: the rest lives in the backups window. Enough to see that snapshots are being taken and
    // when the last one was, which is what this screen is for.
    private void _LoadBackups()
    {
        RecentBackups.Clear();
        var all = _backups.List();
        foreach (var backup in all.Take(RecentBackupCount))
            RecentBackups.Add(new SettingsBackupRowViewModel(backup));

        BackupCount = all.Count;
        LastBackupDisplay = all.Count == 0
            ? "No backups yet — one is taken automatically before every copy."
            : $"Last backup {_Moment(all[0].Manifest.CreatedAtUtc)} · {all[0].Manifest.ContentsSummary}" +
              (all.Count == 1 ? " · 1 kept" : $" · {all.Count} kept");
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
        OnPropertyChanged(nameof(CanRememberAutoSync));
    }

    partial void OnIsBusyChanged(bool value) => _NotifyGates();

    partial void OnClientsRunningChanged(bool value) => _NotifyGates();
}
