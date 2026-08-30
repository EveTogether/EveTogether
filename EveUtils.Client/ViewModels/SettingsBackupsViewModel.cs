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

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The backups, in a window of their own (ET-67): every snapshot ever taken, everything inside the one you select,
/// and the two things you can do with it — put it back, or throw it away.
///
/// It moved out of the sync tool because it never fitted there. Squeezed into a column beside two file lists, the
/// contents of a single backup were down to a few visible lines on a maximised screen; before that they were clipped
/// at a fixed height with no scrollbar at all, and a full backup read as if it had kept only the characters. A
/// backup you cannot read is the same as a backup you cannot trust. Here the list and the contents each get a whole
/// half of a window, and the sync tool keeps only what it needs: the last two, and a button to take one now.
///
/// Restoring is unchanged and stays deliberately slow: blocked while an EVE client runs, confirmed by name and
/// count, and snapshotted first so the restore itself can be undone.
/// </summary>
public partial class SettingsBackupsViewModel : ViewModelBase, IRefreshableModule, IDisposable
{
    private readonly SettingsBackupService _backups;
    private readonly IDialogService? _dialogs;
    private readonly IReadOnlyDictionary<long, string> _names;
    private readonly IEveSettingsWatch? _watch;
    private readonly EveSettingsPreferences? _preferences;
    private readonly IDisposable? _subscription;

    public SettingsBackupsViewModel(
        SettingsBackupService backups,
        IReadOnlyDictionary<long, string> names,
        IEveSettingsWatch? watch = null,
        EveSettingsPreferences? preferences = null,
        IDialogService? dialogs = null)
    {
        _backups = backups;
        _names = names;
        _watch = watch;
        _preferences = preferences;
        _dialogs = dialogs;
        BackupRoot = backups.RootDirectory;

        // One subscription for everything that moves under this window: a client opening or closing, a sync writing
        // files, another screen taking a backup. Released when the window closes.
        _subscription = watch?.Subscribe(_OnChanged);

        CheckClients();
        Reload();
        _ = LoadKeepAsync();
    }

    public void Dispose() => _subscription?.Dispose();

    private void _OnChanged(EveSettingsChange change)
    {
        _ApplyClients(change.Clients);
        if (change.Kind is EveSettingsChangeKind.Sync or EveSettingsChangeKind.Backups)
            Reload();
    }

    public ObservableCollection<SettingsBackupRowViewModel> Backups { get; } = [];

    [ObservableProperty] private SettingsBackupRowViewModel? _selectedBackup;

    /// <summary>Where the backups are kept, spelled out so the user never has to take our word for it.</summary>
    public string BackupRoot { get; }

    [ObservableProperty] private string _status = "Pick a backup to see what is in it.";

    [ObservableProperty] private bool _statusIsError;

    [ObservableProperty] private bool _isBusy;

    /// <summary>What is taking the time, while it takes it.</summary>
    [ObservableProperty] private string _busyMessage = string.Empty;

    [ObservableProperty] private bool _clientsRunning;

    [ObservableProperty] private string _clientWarning = string.Empty;

    public bool HasBackups => Backups.Count > 0;

    public bool HasSelection => SelectedBackup is not null;

    public bool CanRestore => !ClientsRunning && !IsBusy && SelectedBackup is { CanRestore: true };

    public bool CanDelete => !IsBusy && SelectedBackup is not null;

    /// <summary>Says what restoring the selected backup would do, before it is pressed.</summary>
    public string RestoreSummary => SelectedBackup is not { } row
        ? "Nothing selected."
        : row.CanRestore
            ? $"Restoring writes these {row.Backup.Manifest.Entries.Count} files back over {row.ProfileName}. " +
              "The profile as it is now is backed up first, so this is undoable too."
            : "This backup was written by a newer version of EVE Together and cannot be restored here.";

    public void RefreshModule() => Reload();

    /// <summary>Re-reads the backup folder from disk — new snapshots arrive while this window stands open, taken by
    /// a sync in the tool or by the automatic pass.</summary>
    [RelayCommand]
    public void Reload()
    {
        var selectedId = SelectedBackup?.Backup.Id;
        Backups.Clear();
        foreach (var backup in _backups.List())
            Backups.Add(new SettingsBackupRowViewModel(backup));

        SelectedBackup = Backups.FirstOrDefault(row => row.Backup.Id == selectedId) ?? Backups.FirstOrDefault();
        OnPropertyChanged(nameof(HasBackups));
        OnPropertyChanged(nameof(RetentionSummary));
        _Notify();
    }

    /// <summary>
    /// Re-checks for running EVE clients on demand. Restoring into a running client is undone at logout, so the same
    /// block the sync tool applies applies here — on the process, not only on who is visibly in-game. The same one
    /// path also serves every announcement, so the banner cannot be right in one place and stale in another.
    /// </summary>
    [RelayCommand]
    public void CheckClients() => _ApplyClients(_watch?.ProbeClients() ?? EveClientPresenceSnapshot.None);

    private void _ApplyClients(EveClientPresenceSnapshot clients)
    {
        ClientsRunning = clients.AnyRunning;
        ClientWarning = clients.AnyRunning
            ? $"{clients.Count} EVE client(s) running" +
              (clients.InGame.Count > 0 ? $" ({string.Join(", ", clients.InGame)})" : string.Empty) +
              ". EVE rewrites its settings when it closes, so anything restored now is overwritten again at logout. " +
              "Close every client, then check again."
            : string.Empty;
        _Notify();
    }

    // ── How many automatic backups to keep (ET-68) ───────────────────────────────────────────────

    /// <summary>
    /// The retention the automatic sync applies, set here beside the list where its effect is visible. A year of
    /// running otherwise leaves a list nobody can manage.
    /// </summary>
    [ObservableProperty] private int _keepAutomatic = AutoSettingsSyncService.KeepAutomaticBackups;

    /// <summary>What the number means right now, in this list: how many are subject to it, how many are not, and
    /// what lowering it would cost.</summary>
    public string RetentionSummary
    {
        get
        {
            var automatic = Backups.Count(row => row.Backup.Manifest.Reason == BackupReason.BeforeAutoSync);
            var others = Backups.Count - automatic;
            var over = Math.Max(0, automatic - Math.Max(1, KeepAutomatic));
            return $"{automatic} automatic, {others} kept for other reasons — only the automatic ones are ever " +
                   "deleted, and never the newest." +
                   (over > 0 ? $" At {KeepAutomatic}, the {over} oldest automatic will go." : string.Empty);
        }
    }

    private bool _applyingKeep;

    private async Task LoadKeepAsync()
    {
        if (_preferences is null)
            return;

        _applyingKeep = true;
        KeepAutomatic = await _preferences.LoadAutoSyncKeepAsync(AutoSettingsSyncService.KeepAutomaticBackups);
        _applyingKeep = false;
        OnPropertyChanged(nameof(RetentionSummary));
    }

    partial void OnKeepAutomaticChanged(int oldValue, int newValue)
    {
        OnPropertyChanged(nameof(RetentionSummary));
        if (!_applyingKeep)
            _ = _SaveKeepAsync(oldValue, newValue);
    }

    /// <summary>
    /// Stores the new number, and when it means throwing snapshots away, says so first and names them. Lowering a
    /// setting must never quietly take a row of backups with it — so a refusal puts the number back rather than
    /// leaving it standing for the automatic pass to act on later, out of sight.
    /// </summary>
    private async Task _SaveKeepAsync(int previous, int keep)
    {
        if (_preferences is null || keep < 1)
            return;

        var doomed = Backups
            .Where(row => row.Backup.Manifest.Reason == BackupReason.BeforeAutoSync)
            .Skip(keep)
            .ToList();

        if (doomed.Count > 0 && _dialogs is not null)
        {
            var confirmed = await _dialogs.ConfirmAsync(
                "Delete the older automatic backups?",
                $"Keeping {keep} means {doomed.Count} automatic backup(s) go now:\n\n" +
                string.Join("\n", doomed.Take(8).Select(row => $"  • {row.TakenAtDisplay} — {row.ContentsDisplay}")) +
                (doomed.Count > 8 ? $"\n  • …and {doomed.Count - 8} more" : string.Empty) +
                "\n\nBackups you made by hand, and those taken before a copy or a restore you asked for, are not touched.",
                okText: "Delete them");
            if (!confirmed)
            {
                _applyingKeep = true;
                KeepAutomatic = previous;
                _applyingKeep = false;
                Status = "Kept the previous number — nothing was deleted.";
                StatusIsError = false;
                OnPropertyChanged(nameof(RetentionSummary));
                return;
            }
        }

        await _preferences.SaveAutoSyncKeepAsync(keep);

        if (doomed.Count > 0)
        {
            var deleted = _backups.Prune(keep, BackupReason.BeforeAutoSync);
            Status = $"Keeping the newest {keep} automatic backups; {deleted.Count} older one(s) deleted.";
            StatusIsError = false;
            Reload();
            _watch?.Announce(new EveSettingsChange(EveSettingsChangeKind.Backups,
                _watch.ProbeClients()));
        }
        else
        {
            Status = $"Keeping the newest {keep} automatic backups.";
            StatusIsError = false;
        }
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
            // A whole profile takes long enough that a silent button looks broken, and a second click on a restore
            // is the last thing anybody wants.
            BusyMessage = $"Restoring {row.Backup.Manifest.Entries.Count} files into {row.ProfileName} — backing up " +
                          "what is there first…";
            IsBusy = true;
            var result = await Task.Run(() => _backups.Restore(row.Backup, _names));

            if (result.IsSuccess && result.Value is not null)
            {
                var failed = result.Value.Failed.Count == 0
                    ? string.Empty
                    : $" Not restored: {string.Join("; ", result.Value.Failed)}.";
                Status = $"Restored {result.Value.Restored.Count} files into {row.ProfileName}.{failed} " +
                         $"The state from before this restore is in {result.Value.SafetyBackupDirectory}.";
                StatusIsError = result.Value.Failed.Count > 0;
                // Files were written and a safety snapshot taken: every open screen re-reads, including the sync
                // tool whose timestamps this just made untrue.
                _watch?.Announce(new EveSettingsChange(EveSettingsChangeKind.Sync, _watch.ProbeClients()));
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

        Reload();
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
        {
            _Fail(string.Join(" ", result.Messages.Select(message => message.Text)));
        }
        else
        {
            Status = $"Deleted the backup of {row.ProfileName} taken on {row.TakenAtDisplay}.";
            _watch?.Announce(new EveSettingsChange(EveSettingsChangeKind.Backups, _watch.ProbeClients()));
        }

        Reload();
    }

    private void _Fail(string message)
    {
        Status = message;
        StatusIsError = true;
    }

    private void _Notify()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(RestoreSummary));
    }

    partial void OnSelectedBackupChanged(SettingsBackupRowViewModel? value) => _Notify();

    partial void OnIsBusyChanged(bool value) => _Notify();

    partial void OnClientsRunningChanged(bool value) => _Notify();
}
