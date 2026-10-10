using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Runs;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// "Upload to EVE Workbench" for one activity (ET-325): which account each of the user's pilots would upload to, what
/// is shared, and — when a pilot has no usable API key — the way to enter one right here. The upload is a deliberate act;
/// a run that is never uploaded never reaches EVE Workbench.
/// </summary>
public sealed partial class EveWorkbenchUploadViewModel : ViewModelBase
{
    private readonly EveWorkbenchRunAutoPublisher _publisher;
    private readonly IReadOnlyList<UploadablePilot> _pilots;
    private readonly Dictionary<long, ResolvedEveWorkbenchKey?> _keys = [];

    public EveWorkbenchUploadViewModel(EveWorkbenchRunAutoPublisher publisher, IReadOnlyList<UploadablePilot> pilots, bool combatShared)
    {
        _publisher = publisher;
        _pilots = pilots;
        SharedText = "Shared: the run's result, loot, combat and rooms. Never shared: other pilots' names, killmails or e-war."
            + (combatShared ? "" : " Combat data is left out because your DPS sharing switch is off.");
    }

    public ObservableCollection<UploadPilotLine> Pilots { get; } = [];

    public string SharedText { get; }

    public string HowTo => EveWorkbenchLinks.HowTo;

    public event Action? CloseRequested;

    [ObservableProperty] private string _notice = "";
    [ObservableProperty] private bool _needsKey;
    [ObservableProperty] private bool _canUpload;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _keyStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveKeyCommand))]
    private string _keyInput = "";

    public async Task InitializeAsync() => await ReloadAsync(refresh: true);

    private async Task ReloadAsync(bool refresh)
    {
        _keys.Clear();
        Pilots.Clear();
        foreach (UploadablePilot pilot in _pilots)
        {
            ResolvedEveWorkbenchKey? key = await _publisher.KeyForPilotAsync(pilot.CharacterId, refresh && _keys.Count == 0);
            _keys[pilot.CharacterId] = key;
            Pilots.Add(new UploadPilotLine(pilot.Name, key switch
            {
                null => "no API key",
                { Invalid: true } => $"API key invalid (account of {key.MainName})",
                { IsOwn: true } => $"own key (main: {key.MainName})",
                _ => $"uses the key of {key.MainName} (alt)"
            }));
        }

        bool anyMissing = _keys.Values.Any(key => key is null);
        bool anyInvalid = _keys.Values.Any(key => key is { Invalid: true });
        NeedsKey = anyMissing || anyInvalid;
        Notice = anyInvalid ? "Your EVE Workbench API key is invalid or was revoked."
            : anyMissing ? "An EVE Workbench API key is required to upload."
            : "";
        CanUpload = _keys.Values.Any(key => key is { Invalid: false });
    }

    private bool CanSaveKey => !string.IsNullOrWhiteSpace(KeyInput);

    [RelayCommand(CanExecute = nameof(CanSaveKey))]
    private async Task SaveKey()
    {
        EveWorkbenchKeyCheck check = await _publisher.SaveKeyAsync(KeyInput);
        if (check.Verdict == EveWorkbenchKeyVerdict.Valid)
        {
            KeyInput = "";
            KeyStatus = "Key accepted. Characters on this account: " + string.Join(", ", check.Characters.Select(c => c.Name)) + ".";
            await ReloadAsync(refresh: false);
        }
        else
        {
            KeyStatus = check.Verdict == EveWorkbenchKeyVerdict.Invalid
                ? "EVE Workbench did not accept this key."
                : "EVE Workbench could not be reached to check the key; it was not saved.";
        }
    }

    [RelayCommand]
    private async Task Upload()
    {
        Guid[] runIds = [.. _pilots.Where(pilot => _keys.GetValueOrDefault(pilot.CharacterId) is { Invalid: false }).Select(pilot => pilot.RunId)];
        await _publisher.UploadAsync(runIds);
        ResultText = runIds.Length < _pilots.Count
            ? $"{runIds.Length} run(s) are being uploaded. The others have no usable key and stay on this PC."
            : "Uploading. The run's status shows next to it.";
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private static void OpenAbyssTrackerTokens() => EveWorkbenchLinks.Open(EveWorkbenchLinks.AbyssTrackerTokens);

    [RelayCommand]
    private static void OpenEveJournalTokens() => EveWorkbenchLinks.Open(EveWorkbenchLinks.EveJournalTokens);
}

public sealed record UploadablePilot(Guid RunId, long CharacterId, string Name);

public sealed record UploadPilotLine(string Name, string KeyText);
