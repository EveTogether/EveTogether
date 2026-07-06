using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.FitBrowser;
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
    public Task<IReadOnlyList<int>?> SelectFittingsAsync(IReadOnlyList<EsiFitting> fits) => throw NotUsed();
    public Task<CoupleServerResult?> CoupleServerAsync(Func<string, CancellationToken, Task<string?>> probeServerName) => throw NotUsed();
    public Task<string?> SelectServerAsync(string prompt, IReadOnlyList<ServerPickOption> options) => throw NotUsed();
    public string? LastMessage { get; private set; }
    public Task ShowMessageAsync(string title, string message)
    {
        LastMessage = message;
        return Task.CompletedTask;
    }
    public Task<string?> ImportFitTextAsync() => throw NotUsed();
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
    /// <summary>Stub for confirm dialogs: set to drive the answer (and inspect the prompt). Defaults to throwing so a
    /// test that unexpectedly hits a confirm fails loudly.</summary>
    public Func<string, string, Task<bool>>? OnConfirm { get; set; }
    public string? LastConfirmTitle { get; private set; }
    public string? LastConfirmMessage { get; private set; }
    public Task<bool> ConfirmAsync(string title, string message, string okText = "Delete")
    {
        LastConfirmTitle = title;
        LastConfirmMessage = message;
        return OnConfirm is null ? throw NotUsed() : OnConfirm(title, message);
    }
    public Task ShowCharacterAsync(CharacterDialogViewModel viewModel) => throw NotUsed();
    public Task<bool> ShowServerTrustAsync(string displayName, string address, string fingerprint, string statusLabel) => throw NotUsed();
    public void ShowFleets(FleetsViewModel viewModel) => throw NotUsed();
    public void ShowMetrics(MetricsWindowViewModel viewModel) => throw NotUsed();
    public Task ShowAboutAsync(AboutViewModel viewModel) => throw NotUsed();
    public void ShowDpsOverlay(DpsViewModel tracker) => throw NotUsed();
    public void ShowSettings(string currentDirectory, string detectedDefault, bool shareLocation, bool shareBounty, bool shareCombat, bool loadTypeImages, EveUtils.Client.Theming.FactionTheme currentFaction, string sdeVersionLabel, Func<SettingsResult, Task> onApply, bool openFitDetailAfterImport = true, EveUtils.Client.Notifications.ToastPosition toastPosition = EveUtils.Client.Notifications.ToastPosition.TopRight, bool enableLocalApi = false, int localApiPort = EveUtils.Client.LocalApi.LocalApiServer.DefaultPort, string localApiStatusLabel = "", EveUtils.Client.LocalApi.ILocalApiServer? localApiServer = null) => throw NotUsed();
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
    public void ShowFitBrowser(FitBrowserViewModel viewModel) => throw NotUsed();
    public void ShowCompositions(CompositionsViewModel viewModel) => throw NotUsed();
    public FitDetailWindowViewModel? LastFitDetail { get; private set; }
    public void ShowFitDetail(FitDetailWindowViewModel viewModel) => LastFitDetail = viewModel;
    public void ShowTypeInfo(TypeInfoWindowViewModel viewModel) => throw NotUsed();
    public Task<FleetInviteResult?> PickFleetInviteAsync(string fleetName, IReadOnlyList<CharacterPickOption> options) => throw NotUsed();
    public Task<int?> AddExternalMemberAsync(IExternalCharacterLookup lookup) => throw NotUsed();
    /// <summary>Answers a text prompt (e.g. the add-wing/add-squad name). Default: cancel (null).</summary>
    public Func<string, string, string?, Task<string?>> OnPromptText { get; set; } =
        (_, _, _) => Task.FromResult<string?>(null);

    public Task<string?> PromptTextAsync(string title, string header, string? defaultValue = null) =>
        OnPromptText(title, header, defaultValue);
    public Task<bool> ConfirmStartFleetAsync(string fleetName, int unlinkedCount) => throw NotUsed();
    public void ShowRoster(FleetRosterViewModel viewModel) => throw NotUsed();
    public void ShowFleetMetrics(FleetMetricsViewModel viewModel) => throw NotUsed();
    public Task ShowSdeUpdateAsync(SdeProgressViewModel viewModel) => throw NotUsed();
    public void SwitchMode() { }

    private static NotSupportedException NotUsed() =>
        new("RecordingDialogService: this dialog is not expected in the fleet request-to-join picker test.");
}
