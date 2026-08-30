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
public partial class SettingsBackupsViewModel : ViewModelBase, IRefreshableModule
{
    private readonly SettingsBackupService _backups;
    private readonly EveClientPresenceService? _presence;
    private readonly IDialogService? _dialogs;
    private readonly IReadOnlyDictionary<long, string> _names;

    public SettingsBackupsViewModel(
        SettingsBackupService backups,
        IReadOnlyDictionary<long, string> names,
        EveClientPresenceService? presence = null,
        IDialogService? dialogs = null)
    {
        _backups = backups;
        _names = names;
        _presence = presence;
        _dialogs = dialogs;
        BackupRoot = backups.RootDirectory;

        CheckClients();
        Reload();
    }

    /// <summary>Raised after a restore put files back, so the sync tool behind this re-reads the profile it is
    /// showing instead of standing there with write times that are no longer true.</summary>
    public event Action? ProfileChanged;

    public ObservableCollection<SettingsBackupRowViewModel> Backups { get; } = [];

    [ObservableProperty] private SettingsBackupRowViewModel? _selectedBackup;

    /// <summary>Where the backups are kept, spelled out so the user never has to take our word for it.</summary>
    public string BackupRoot { get; }

    [ObservableProperty] private string _status = "Pick a backup to see what is in it.";

    [ObservableProperty] private bool _statusIsError;

    [ObservableProperty] private bool _isBusy;

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
        _Notify();
    }

    /// <summary>
    /// Re-checks for running EVE clients. Restoring into a running client is undone at logout, so the same block the
    /// sync tool applies applies here — on the process, not only on who is visibly in-game.
    /// </summary>
    [RelayCommand]
    public void CheckClients()
    {
        if (_presence is null)
        {
            ClientsRunning = false;
            ClientWarning = string.Empty;
            _Notify();
            return;
        }

        var processes = _presence.RunningClientCount();
        var inGame = _presence.Current.CharacterNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        ClientsRunning = processes > 0 || inGame.Count > 0;
        ClientWarning = ClientsRunning
            ? $"{Math.Max(processes, inGame.Count)} EVE client(s) running" +
              (inGame.Count > 0 ? $" ({string.Join(", ", inGame)})" : string.Empty) +
              ". EVE rewrites its settings when it closes, so anything restored now is overwritten again at logout. " +
              "Close every client, then check again."
            : string.Empty;
        _Notify();
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
            var result = await Task.Run(() => _backups.Restore(row.Backup, _names));

            if (result.IsSuccess && result.Value is not null)
            {
                var failed = result.Value.Failed.Count == 0
                    ? string.Empty
                    : $" Not restored: {string.Join("; ", result.Value.Failed)}.";
                Status = $"Restored {result.Value.Restored.Count} files into {row.ProfileName}.{failed} " +
                         $"The state from before this restore is in {result.Value.SafetyBackupDirectory}.";
                StatusIsError = result.Value.Failed.Count > 0;
                ProfileChanged?.Invoke();
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
            _Fail(string.Join(" ", result.Messages.Select(message => message.Text)));
        else
            Status = $"Deleted the backup of {row.ProfileName} taken on {row.TakenAtDisplay}.";

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
