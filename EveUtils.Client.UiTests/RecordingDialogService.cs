using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Fittings.Dtos;

namespace EveUtils.Client.UiTests;

/// <summary>
/// <see cref="IDialogService"/> double for headless UI tests. Only <see cref="PickCharacterAsync"/> is wired —
/// a test sets <see cref="OnPickCharacter"/> to choose (or cancel) and reads back <see cref="LastPrompt"/>/
/// <see cref="LastOptions"/> to assert which characters were offered. Every other dialog throws, so a test that
/// unexpectedly triggers UI fails loudly rather than silently.
/// </summary>
public sealed class RecordingDialogService : IDialogService
{
    /// <summary>Chooses the picked character id (or null to cancel). Default: cancel.</summary>
    public Func<string, IReadOnlyList<CharacterPickOption>, Task<int?>> OnPickCharacter { get; set; } =
        (_, _) => Task.FromResult<int?>(null);

    /// <summary>The prompt of the last <see cref="PickCharacterAsync"/> call, or null if never shown.</summary>
    public string? LastPrompt { get; private set; }

    /// <summary>The options of the last <see cref="PickCharacterAsync"/> call, or null if never shown.</summary>
    public IReadOnlyList<CharacterPickOption>? LastOptions { get; private set; }

    /// <summary>The text of the last <see cref="SetClipboardTextAsync"/> call, or null if never copied.</summary>
    public string? LastClipboardText { get; private set; }

    /// <summary>What <see cref="GetClipboardTextAsync"/> hands back — the "system clipboard" a test copies into.</summary>
    public string? ClipboardText { get; set; }

    public Func<Task<string?>>? OnGetClipboardText { get; set; }

    /// <summary>How often the clipboard was actually read — how a test asserts that it was left alone.</summary>
    public int ClipboardReads { get; private set; }

    /// <summary>The arguments of the last <see cref="ShowFitExportAsync"/> call, or null if never shown.</summary>
    public (string FitName, string Eft, string Dna, string EveshipUrl)? LastFitExport { get; private set; }

    /// <summary>Returns the fits the picker "selects" (or null to cancel). Default: cancel.</summary>
    public Func<FitPickerViewModel, Task<IReadOnlyList<FitReferenceInfo>?>> OnShowFitPicker { get; set; } =
        _ => Task.FromResult<IReadOnlyList<FitReferenceInfo>?>(null);

    /// <summary>Drives the composition editor (e.g. invoke its save). Default: cancel (returns false).</summary>
    public Func<CompositionEditorViewModel, Task<bool>> OnShowCompositionEditor { get; set; } =
        _ => Task.FromResult(false);

    /// <summary>The view-model of the last composition editor shown, or null.</summary>
    public CompositionEditorViewModel? LastCompositionEditor { get; private set; }

    public Task<int?> PickCharacterAsync(string prompt, IReadOnlyList<CharacterPickOption> options)
    {
        LastPrompt = prompt;
        LastOptions = options;
        return OnPickCharacter(prompt, options);
    }

    /// <summary>Drives the multi-select picker (bulk join / add). Default: cancel (null).</summary>
    public Func<string, IReadOnlyList<CharacterPickOption>, Task<IReadOnlyList<int>?>> OnPickCharacters { get; set; } =
        (_, _) => Task.FromResult<IReadOnlyList<int>?>(null);

    public Task<IReadOnlyList<int>?> PickCharactersAsync(string prompt, IReadOnlyList<CharacterPickOption> options)
    {
        LastPrompt = prompt;
        LastOptions = options;
        return OnPickCharacters(prompt, options);
    }

    public Task<IReadOnlyList<string>?> SelectScopesAsync(IReadOnlyList<EsiScopeRequirement> available,
        IReadOnlyCollection<string>? preselected = null) => throw NotUsed();
    /// <summary>Answers the ESI fit-import dialog with the ticked fitting ids (or null to cancel). Default: cancel.</summary>
    public Func<IReadOnlyList<EsiFitting>, Task<IReadOnlyList<int>?>> OnSelectFittings { get; set; } =
        _ => Task.FromResult<IReadOnlyList<int>?>(null);

    /// <summary>The fits the import dialog was opened with, or null if it never opened.</summary>
    public IReadOnlyList<EsiFitting>? LastFittingsOffered { get; private set; }

    public Task<IReadOnlyList<int>?> SelectFittingsAsync(IReadOnlyList<EsiFitting> fits)
    {
        LastFittingsOffered = fits;
        return OnSelectFittings(fits);
    }
    /// <summary>What the couple dialog was opened with, so a test can see whether it would have asked the user to
    /// retype something the client already knows (ET-123). Null means it has not been opened.</summary>
    public CoupleServerResult? LastCouplePrefill { get; private set; }
    public bool CoupleDialogOpened { get; private set; }

    public Task<CoupleServerResult?> CoupleServerAsync(
        Func<string, CancellationToken, Task<string?>> probeServerName, CoupleServerResult? prefill = null)
    {
        CoupleDialogOpened = true;
        LastCouplePrefill = prefill;
        return Task.FromResult<CoupleServerResult?>(null); // cancelled — the pairing round-trip is not what is under test
    }

    public Task<string?> SelectServerAsync(string prompt, IReadOnlyList<ServerPickOption> options) => throw NotUsed();
    public string? LastMessage { get; private set; }
    public Task ShowMessageAsync(string title, string message)
    {
        LastMessage = message;
        return Task.CompletedTask;
    }
    /// <summary>What the paste-a-fit dialog was pre-filled with, and how often it was opened.</summary>
    public string? ImportFitTextInitial { get; private set; }

    public int ImportFitTextCalls { get; private set; }

    /// <summary>What the dialog returns. Null (the default) stands for the user cancelling it.</summary>
    public string? ImportFitTextResult { get; set; }

    public Task<string?> ImportFitTextAsync(string? initialText = null)
    {
        ImportFitTextCalls++;
        ImportFitTextInitial = initialText;
        return Task.FromResult(ImportFitTextResult);
    }
    public Task<string?> ImportFitEsfLinkAsync() => throw NotUsed();

    /// <summary>Stub for the edit-fit-metadata dialog: set to inspect the prefilled draft and drive the result.
    /// Defaults to returning null (cancel), so a flow that unexpectedly edits doesn't silently mutate.</summary>
    public Func<FitMetadataDraft, Task<FitMetadataDraft?>>? OnEditFitMetadata { get; set; }
    public FitMetadataDraft? LastEditFitMetadataArg { get; private set; }
    public Task<FitMetadataDraft?> EditFitMetadataAsync(FitMetadataDraft current)
    {
        LastEditFitMetadataArg = current;
        return OnEditFitMetadata is null ? Task.FromResult<FitMetadataDraft?>(null) : OnEditFitMetadata(current);
    }
    public Task ShowFitExportAsync(string fitName, string eft, string dna, string eveshipUrl)
    {
        LastFitExport = (fitName, eft, dna, eveshipUrl);
        return Task.CompletedTask;
    }
    public Task SetClipboardTextAsync(string text)
    {
        LastClipboardText = text;
        return Task.CompletedTask;
    }
    public Task<string?> GetClipboardTextAsync()
    {
        ClipboardReads++;
        return OnGetClipboardText is null ? Task.FromResult(ClipboardText) : OnGetClipboardText();
    }
    /// <summary>Stub for confirm dialogs: set to drive the answer (and inspect the prompt). Defaults to throwing so a
    /// test that unexpectedly hits a confirm fails loudly.</summary>
    public Func<string, string, Task<bool>>? OnConfirm { get; set; }
    public string? LastConfirmTitle { get; private set; }
    public string? LastConfirmMessage { get; private set; }

    /// <summary>Every confirm asked for, in order — a flow that asks a second question (remove here, then kick
    /// in-game) is only pinned down by the sequence, not by the last one.</summary>
    public List<(string Title, string Message)> ConfirmPrompts { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string okText = "Delete")
    {
        LastConfirmTitle = title;
        LastConfirmMessage = message;
        ConfirmPrompts.Add((title, message));
        return OnConfirm is null ? throw NotUsed() : OnConfirm(title, message);
    }
    /// <summary>Every three-way question asked, in order. Null answers cancel, which is the row a close test needs
    /// to prove the window stays put.</summary>
    public List<(string Title, string Message, string Primary, string Secondary)> ChoicePrompts { get; } = [];

    /// <summary>What to answer: true = primary, false = secondary, null = cancel. Unset answers "discard" rather
    /// than throwing: closing a window is teardown for most tests here, and a question refused would hang the close
    /// on a run made of scratch data. A test that cares what the answer does sets this.</summary>
    public Func<string, string, bool?>? OnChoose { get; set; }

    public Task<bool?> ChooseAsync(string title, string message, string primaryText, string secondaryText)
    {
        ChoicePrompts.Add((title, message, primaryText, secondaryText));
        return Task.FromResult(OnChoose is null ? false : OnChoose(title, message));
    }

    public Task ShowCharacterAsync(CharacterDialogViewModel viewModel) => throw NotUsed();
    public Task<bool> ShowServerTrustAsync(string displayName, string address, string fingerprint, string statusLabel) => throw NotUsed();
    public void ShowFleets(FleetsViewModel viewModel) => throw NotUsed();
    public void ShowMetrics(MetricsWindowViewModel viewModel) => throw NotUsed();
    public Task ShowAboutAsync(AboutViewModel viewModel) => throw NotUsed();
    public void ShowDpsOverlay(DpsViewModel tracker) => throw NotUsed();

    /// <summary>The trackers whose pop-out a screen asked to close — how a test asserts that a removed member's
    /// pop-out follows their row off the screen (ET-52). Closing what was never popped out is a no-op in the real
    /// service, so this records rather than throws.</summary>
    public List<DpsViewModel> ClosedDpsOverlays { get; } = [];

    public void CloseDpsOverlay(DpsViewModel tracker) => ClosedDpsOverlays.Add(tracker);

    /// <summary>The fleet overlays a screen asked to open (ET-72). Recorded rather than shown: a test asserts that
    /// the pop-out command reached the service and what it was handed, without a second window on screen.</summary>
    public List<FleetOverlayViewModel> ShownFleetOverlays { get; } = [];

    /// <summary>The fleets whose overlay a screen asked to close — how a test asserts the overlay follows the
    /// fleet-metrics screen it reads from. Closing what was never opened is a no-op in the real service.</summary>
    public List<long> ClosedFleetOverlays { get; } = [];

    public void ShowFleetOverlay(FleetOverlayViewModel viewModel) => ShownFleetOverlays.Add(viewModel);
    public void CloseFleetOverlay(long fleetId) => ClosedFleetOverlays.Add(fleetId);

    /// <summary>Set by a test that wants the "a window is already up" case; nothing here opens a real one.</summary>
    public bool IsActivityWindowOpen { get; set; }

    /// <summary>The activity windows a screen asked to open (ET-100). Recorded rather than shown, same as the fleet
    /// overlay above — a test asserts the toast's Start-run action reached the service without a real window.</summary>
    public List<ActivityWindowViewModel> ShownActivityWindows { get; } = [];

    /// <summary>The trigger each window was asked for under — what a test reads to prove a fleet-commander start
    /// never asked for focus (ET-105 AC-2).</summary>
    public List<RunWindowOpenTrigger> ShownActivityWindowTriggers { get; } = [];

    public void ShowActivityWindow(ActivityWindowViewModel viewModel,
        RunWindowOpenTrigger trigger = RunWindowOpenTrigger.LocalUser)
    {
        ShownActivityWindows.Add(viewModel);
        ShownActivityWindowTriggers.Add(trigger);
    }
    public void ShowSettings(string currentDirectory, string detectedDefault, bool shareLocation, bool shareBounty, bool shareCombat, bool loadTypeImages, EveUtils.Client.Theming.FactionTheme currentFaction, string sdeVersionLabel, Func<SettingsResult, Task> onApply, bool openFitDetailAfterImport = true, EveUtils.Client.Notifications.ToastPosition toastPosition = EveUtils.Client.Notifications.ToastPosition.TopRight, bool enableLocalApi = false, int localApiPort = EveUtils.Client.LocalApi.LocalApiServer.DefaultPort, string localApiStatusLabel = "", EveUtils.Client.LocalApi.ILocalApiServer? localApiServer = null, bool checkUpdatesOnStartup = true, EveUtils.Client.Clipboard.ClipboardWatchService? clipboardWatch = null, int initialCategory = 0, bool openFleetRunWindowImmediately = false) => throw NotUsed();

    /// <summary>
    /// Answers the update offer (true = download and install). Default: Later.
    /// </summary>
    public Func<Updates.AppRelease, bool> OnShowUpdateAvailable { get; set; } = _ => false;

    /// <summary>
    /// The offers shown, in order, as (installed version, release) — empty if the dialog never opened.
    /// </summary>
    public List<(string InstalledVersion, Updates.AppRelease Release)> UpdateOffers { get; } = new();

    public Task<bool> ShowUpdateAvailableAsync(string installedVersion, Updates.AppRelease release)
    {
        UpdateOffers.Add((installedVersion, release));
        return Task.FromResult(OnShowUpdateAvailable(release));
    }
    public Task<bool> ShowFleetSharingAsync(FleetShareViewModel viewModel) => throw NotUsed();
    public Task<FleetEditResult?> EditFleetAsync(FleetInfo? existing) => throw NotUsed();
    public Task<bool> ShowCompositionEditorAsync(CompositionEditorViewModel viewModel)
    {
        LastCompositionEditor = viewModel;
        return OnShowCompositionEditor(viewModel);
    }
    public Task<IReadOnlyList<FitReferenceInfo>?> ShowFitPickerAsync(FitPickerViewModel viewModel) => OnShowFitPicker(viewModel);

    /// <summary>Returns the fit the single picker "selects" (or null to cancel). Default: cancel.</summary>
    public Func<FitPickerViewModel, Task<FitReferenceInfo?>> OnPickFit { get; set; } =
        _ => Task.FromResult<FitReferenceInfo?>(null);

    public Task<FitReferenceInfo?> PickFitAsync(FitPickerViewModel viewModel) => OnPickFit(viewModel);
    public void ShowInbox(InboxViewModel viewModel) => throw NotUsed();
    public void ShowLogs(ClientLogViewModel viewModel) => throw NotUsed();
    public void ShowEsiMetrics(EsiMetricsViewModel viewModel) => throw NotUsed();

    /// <summary>The settings-sync tool the shell was asked to open, or null — how a test asserts the Tools menu
    /// actually reached the module without standing up the real window.</summary>
    public SettingsSyncViewModel? LastSettingsSync { get; private set; }

    public void ShowSettingsSync(SettingsSyncViewModel viewModel) => LastSettingsSync = viewModel;

    /// <summary>The backups module the tool asked to open, or null — how a test asserts that the button reaches it
    /// without standing up the real window.</summary>
    public SettingsBackupsViewModel? LastSettingsBackups { get; private set; }

    public void ShowSettingsBackups(SettingsBackupsViewModel viewModel) => LastSettingsBackups = viewModel;

    /// <summary>The appraisal tool the shell was asked to open, or null — how a test asserts the Tools menu reaches
    /// the module without standing up the real window.</summary>
    public AppraisalViewModel? LastAppraisal { get; private set; }

    public void ShowAppraisal(AppraisalViewModel viewModel) => LastAppraisal = viewModel;

    /// <summary>The manual run-start screen the shell was asked to open, or null — how a test asserts the Tools
    /// menu reaches the module without standing up the real window.</summary>
    public ManualRunStartViewModel? LastManualRunStart { get; private set; }

    public void ShowManualRunStart(ManualRunStartViewModel viewModel) => LastManualRunStart = viewModel;

    /// <summary>The activity detail the shell was asked to open, or null.</summary>
    public ActivityDetailViewModel? LastActivityDetail { get; private set; }

    public void ShowActivityDetail(ActivityDetailViewModel viewModel, Guid activitySummaryId) =>
        LastActivityDetail = viewModel;

    /// <summary>The runs screen the shell was asked to open, or null — how a test asserts the RUNS rail reaches the
    /// module without standing up the real window.</summary>
    public RunsOverviewViewModel? LastRuns { get; private set; }

    public void ShowRuns(RunsOverviewViewModel viewModel) => LastRuns = viewModel;

    /// <summary>The save-a-preset dialog the tool asked for, or null — and a hook to drive it (pick a path and
    /// export) without a window.</summary>
    public PresetExportViewModel? LastPresetExport { get; private set; }

    public Func<PresetExportViewModel, Task> OnShowPresetExport { get; set; } = _ => Task.CompletedTask;

    public Task ShowPresetExportAsync(PresetExportViewModel viewModel)
    {
        LastPresetExport = viewModel;
        return OnShowPresetExport(viewModel);
    }

    /// <summary>The read-a-preset dialog the tool asked for, or null. The hook stands in for the user: open a file,
    /// change a line, apply — then the tool behind it sees whatever really happened.</summary>
    public PresetImportViewModel? LastPresetImport { get; private set; }

    public Func<PresetImportViewModel, Task> OnShowPresetImport { get; set; } = _ => Task.CompletedTask;

    public async Task<bool> ShowPresetImportAsync(PresetImportViewModel viewModel)
    {
        LastPresetImport = viewModel;
        await OnShowPresetImport(viewModel);
        return viewModel.Applied;
    }
    /// <summary>The view-model of the last fit browser shown, or null.</summary>
    public FitBrowserViewModel? LastFitBrowser { get; private set; }
    public void ShowFitBrowser(FitBrowserViewModel viewModel) => LastFitBrowser = viewModel;
    public void ShowCompositions(CompositionsViewModel viewModel) => throw NotUsed();
    public FitDetailWindowViewModel? LastFitDetail { get; private set; }
    public void ShowFitDetail(FitDetailWindowViewModel viewModel) => LastFitDetail = viewModel;
    public void ShowTypeInfo(TypeInfoWindowViewModel viewModel) => throw NotUsed();
    public Task<FleetInviteResult?> PickFleetInviteAsync(string fleetName, IReadOnlyList<CharacterPickOption> options) => throw NotUsed();
    /// <summary>Answers the add-external-pilot search dialog with a character id (or null to cancel). Default: cancel.</summary>
    public Func<IExternalCharacterLookup, Task<int?>> OnAddExternalMember { get; set; } =
        _ => Task.FromResult<int?>(null);

    public Task<int?> AddExternalMemberAsync(IExternalCharacterLookup lookup) => OnAddExternalMember(lookup);
    /// <summary>Answers a text prompt (e.g. the add-wing/add-squad name). Default: cancel (null).</summary>
    public Func<string, string, string?, Task<string?>> OnPromptText { get; set; } =
        (_, _, _) => Task.FromResult<string?>(null);

    public Task<string?> PromptTextAsync(string title, string header, string? defaultValue = null) =>
        OnPromptText(title, header, defaultValue);
    public Task<bool> ConfirmStartFleetAsync(string fleetName, int unlinkedCount) => throw NotUsed();

    /// <summary>What the stop dialog answers with, and what it was asked. A test sets the exit it wants taken and
    /// reads the prompt back to check the fleet's state was described to the FC, without a window opening.</summary>
    public StopFleetChoice FleetExit { get; set; } = StopFleetChoice.Cancel;

    public StopFleetPrompt? FleetExitPrompt { get; private set; }

    public Task<StopFleetChoice> PickFleetExitAsync(StopFleetPrompt prompt)
    {
        FleetExitPrompt = prompt;
        return Task.FromResult(FleetExit);
    }
    public void ShowRoster(FleetRosterViewModel viewModel) => throw NotUsed();
    public void ShowFleetMetrics(FleetMetricsViewModel viewModel) => throw NotUsed();
    public Task ShowSdeUpdateAsync(SdeProgressViewModel viewModel) => throw NotUsed();
    public void SwitchMode() { }

    private static NotSupportedException NotUsed() =>
        new("RecordingDialogService: this dialog is not expected in the fleet request-to-join picker test.");
}
