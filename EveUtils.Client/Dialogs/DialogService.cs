using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.Dialogs;

/// <summary>
/// Avalonia implementation of <see cref="IDialogService"/>. The owner window is set once from
/// <c>App.OnFrameworkInitializationCompleted</c> after the main window is created. Called from
/// view-model commands, which already run on the UI thread.
/// </summary>
public sealed class DialogService : IDialogService, ISingletonService
{
    private Window? _owner;
    private readonly ModuleHostService _moduleHost = new();
    private readonly Dictionary<string, DpsOverlayWindow> _dpsOverlays = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, FleetOverlayWindow> _fleetOverlays = new();
    private ActivityWindow? _activityWindow;
    private readonly List<Window> _infoPopouts = new(); // non-modal type-info cards, shown ownerless

    public void SetOwner(Window owner)
    {
        _owner = owner;
        _moduleHost.SetOwner(owner);
    }

    /// <summary>Wires the docked module host (the main view-model): the tab sink for hosted modules.</summary>
    public void SetHost(IModuleHostDisplay host) => _moduleHost.SetHost(host);

    /// <summary>Re-render the open module set after a dock/float switch — migrates modules to the new mode (no orphans).</summary>
    public void SwitchMode() => _moduleHost.SwitchMode();

    // Opens a non-modal feature window as a module: a docked tab, or a floating window — handled by the host.
    private void Route(Window window, string title, string? moduleKey, string moduleId) =>
        _moduleHost.Open(window, title, moduleKey, moduleId);

    public void ShowDpsOverlay(DpsViewModel tracker)
    {
        if (_owner is null) return;

        // One overlay per character: re-opening focuses the existing window instead of stacking duplicates.
        if (_dpsOverlays.TryGetValue(tracker.Character, out var existing))
        {
            existing.Activate();
            return;
        }

        var overlay = new DpsOverlayWindow(tracker);
        _dpsOverlays[tracker.Character] = overlay;
        overlay.Closed += (_, _) => _dpsOverlays.Remove(tracker.Character);
        // Shown ownerless so the overlay is independent of the main window (minimizing the main window no longer
        // minimizes the overlay). The main window's close handler closes any open overlays explicitly.
        overlay.Show();
    }

    public void CloseDpsOverlay(DpsViewModel tracker)
    {
        // Keyed by name like the open path, but only closed when the window really holds THIS tracker: two screens can
        // each pop out a same-named pilot, and only the one whose member was removed should go.
        if (_dpsOverlays.TryGetValue(tracker.Character, out var overlay)
            && ReferenceEquals(overlay.DataContext, tracker))
            overlay.Close();   // fires Closed → drops itself from _dpsOverlays
    }

    public void ShowFleetOverlay(FleetOverlayViewModel viewModel)
    {
        if (_owner is null) return;

        // One overlay per fleet, exactly as there is one per character above: re-opening focuses the window that is
        // already up instead of stacking a second one reading the same rows.
        if (_fleetOverlays.TryGetValue(viewModel.FleetId, out var existing))
        {
            existing.Activate();
            return;
        }

        var overlay = new FleetOverlayWindow(viewModel);
        _fleetOverlays[viewModel.FleetId] = overlay;
        overlay.Closed += (_, _) => _fleetOverlays.Remove(viewModel.FleetId);
        overlay.Show();   // ownerless, like the DPS overlay: minimizing the main window must not take it with it
    }

    public void CloseFleetOverlay(long fleetId)
    {
        if (_fleetOverlays.TryGetValue(fleetId, out var overlay))
            overlay.Close();   // fires Closed → drops itself from _fleetOverlays
    }

    public void ShowActivityWindow(ActivityWindowViewModel viewModel,
        RunWindowOpenTrigger trigger = RunWindowOpenTrigger.LocalUser)
    {
        if (_owner is null) return;

        // Only one run is ever tracked at a time (ET-98): re-opening focuses it instead of stacking a second,
        // same rule as the DPS and fleet overlays above — and what makes ET-100's double-click AC hold. Whether
        // focus may be taken at all is RunWindowPresentation's call, never this method's (ET-105 AC-2).
        // A second call carries a newly copied signature, and the window that is already up is the one that has to
        // hear about it — dropping the incoming view model meant "start run" on a fresh signature did nothing but
        // raise the window on the previous site (Raymond, 2026-09-02).
        if (_activityWindow?.DataContext is ActivityWindowViewModel open && !ReferenceEquals(open, viewModel)
            && viewModel.SignatureName is { Length: > 0 } signature)
            open.ApplySignature(viewModel.SignatureId, viewModel.SignatureGroup, signature, viewModel.MatchedSites);

        switch (RunWindowPresentation.Decide(trigger, _activityWindow is not null))
        {
            case RunWindowActivation.LeaveAsIs:
                return;

            case RunWindowActivation.Activate when _activityWindow is { } existing:
                existing.Activate();
                return;

            case RunWindowActivation.Activate:
                _activityWindow = _Open(viewModel, showActivated: true);
                return;

            default:
                _activityWindow = _Open(viewModel, showActivated: false);
                return;
        }
    }

    /// <summary><c>ShowActivated</c> is Avalonia's own "put it up without taking the keyboard" — set before
    /// <c>Show()</c>, which is the only moment it is read. No platform focus call is involved.</summary>
    private ActivityWindow _Open(ActivityWindowViewModel viewModel, bool showActivated)
    {
        var window = new ActivityWindow(viewModel) { ShowActivated = showActivated };
        window.Closed += (_, _) => _activityWindow = null;
        window.Show();
        return window;
    }

    /// <summary>
    /// Every modal here is owned by the main window, and the run overlay is <c>Topmost</c> — so with a run on screen
    /// a dialog opened behind it, and the fit picker went missing. The dialog is raised instead of the overlay
    /// lowered: the owner, and with it which window the dialog blocks, stays exactly as it was, and the overlay keeps
    /// the one property it is there for. Z-order is a real windowing question, so this can only be seen on a desktop;
    /// what a test can hold is that no dialog leaves here unraised.
    /// </summary>
    private T _Over<T>(T dialog) where T : Window
    {
        if (_activityWindow is not null)
            dialog.Topmost = true;

        return dialog;
    }

    /// <summary>The activity window currently up, if any — the same one <see cref="OpenPopoutCount"/> counts.
    /// Readable so the no-focus rule (ET-105 AC-2) can be asserted on the window this service really built, rather
    /// than on a second copy of the decision that could drift away from it.</summary>
    public Window? ActivityWindow => _activityWindow;

    public bool IsActivityWindowOpen => _activityWindow is not null;

    /// <summary>Open pop-out windows independent of the main window: floating modules + DPS overlays + fleet
    /// overlays + info cards. Used by the main window's close handler to decide whether to confirm before quitting.</summary>
    public int OpenPopoutCount =>
        _dpsOverlays.Count + _fleetOverlays.Count + (_activityWindow is null ? 0 : 1)
        + _moduleHost.FloatingWindowCount + _infoPopouts.Count;

    /// <summary>Close every open pop-out window — called when the main window is closing so leftover ownerless
    /// windows don't keep the app alive.</summary>
    public void CloseAllPopouts()
    {
        foreach (var overlay in _dpsOverlays.Values.ToList())
            overlay.Close();
        foreach (var overlay in _fleetOverlays.Values.ToList())
            overlay.Close();
        _activityWindow?.Close();
        foreach (var info in _infoPopouts.ToList())
            info.Close();
        _moduleHost.CloseFloatingWindows();
    }

    /// <summary>Confirm dialog with an extra "don't ask again" opt-out checkbox. Returns whether the
    /// user confirmed and whether they ticked the opt-out.</summary>
    public async Task<(bool Confirmed, bool OptOut)> ConfirmWithOptOutAsync(string title, string message, string okText, string optOutText)
    {
        if (_owner is null) return (false, false);
        var dialog = new MessageBoxWindow(title, message, confirm: true, okText: okText, optOutText: optOutText);
        var confirmed = await _Over(dialog).ShowDialog<bool>(_owner);
        return (confirmed, dialog.OptOutChecked);
    }

    public async Task<IReadOnlyList<string>?> SelectScopesAsync(IReadOnlyList<EsiScopeRequirement> available,
        IReadOnlyCollection<string>? preselected = null)
    {
        if (_owner is null) return null;
        var dialog = new ScopeSelectionWindow(available, preselected);
        return await _Over(dialog).ShowDialog<IReadOnlyList<string>?>(_owner);
    }

    public async Task<IReadOnlyList<int>?> SelectFittingsAsync(IReadOnlyList<EsiFitting> fits)
    {
        if (_owner is null) return null;
        var dialog = new FitImportWindow(fits);
        return await _Over(dialog).ShowDialog<IReadOnlyList<int>?>(_owner);
    }

    public async Task<int?> PickCharacterAsync(string prompt, IReadOnlyList<CharacterPickOption> options)
    {
        if (_owner is null) return null;
        var dialog = new CharacterPickerWindow(prompt, options);
        return await _Over(dialog).ShowDialog<int?>(_owner);
    }

    public async Task<IReadOnlyList<int>?> PickCharactersAsync(string prompt, IReadOnlyList<CharacterPickOption> options)
    {
        if (_owner is null) return null;
        var dialog = new CharacterPickerWindow(prompt, options, multiSelect: true);
        return await _Over(dialog).ShowDialog<IReadOnlyList<int>?>(_owner);
    }

    public async Task<CoupleServerResult?> CoupleServerAsync(
        Func<string, CancellationToken, Task<string?>> probeServerName, CoupleServerResult? prefill = null)
    {
        if (_owner is null) return null;
        var dialog = new CoupleServerWindow(probeServerName, prefill);
        return await _Over(dialog).ShowDialog<CoupleServerResult?>(_owner);
    }

    public async Task<string?> SelectServerAsync(string prompt, IReadOnlyList<ServerPickOption> options)
    {
        if (_owner is null) return null;
        var dialog = new ServerPickerWindow(prompt, options);
        return await _Over(dialog).ShowDialog<string?>(_owner);
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        if (_owner is null) return;
        var dialog = new MessageBoxWindow(title, message);
        await _Over(dialog).ShowDialog(_owner);
    }

    public async Task<string?> ImportFitTextAsync(string? initialText = null)
    {
        if (_owner is null) return null;
        return await _Over(new FitTextImportWindow(initialText)).ShowDialog<string?>(_owner);
    }

    public async Task<string?> ImportFitEsfLinkAsync()
    {
        if (_owner is null) return null;
        return await _Over(new FitEsfImportWindow()).ShowDialog<string?>(_owner);
    }

    public async Task<FitMetadataDraft?> EditFitMetadataAsync(FitMetadataDraft current)
    {
        if (_owner is null) return null;
        return await _Over(new FitMetadataDialog(current)).ShowDialog<FitMetadataDraft?>(_owner);
    }

    public async Task ShowFitExportAsync(string fitName, string eft, string dna, string eveshipUrl)
    {
        if (_owner is null) return;
        await _Over(new FitExportWindow(fitName, eft, dna, eveshipUrl)).ShowDialog(_owner);
    }

    public async Task SetClipboardTextAsync(string text)
    {
        var clipboard = _owner?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(text);
    }

    public async Task<string?> GetClipboardTextAsync()
    {
        var clipboard = _owner?.Clipboard;
        if (clipboard is null) return null;

        return await clipboard.TryGetTextAsync();
    }

    public async Task<bool> ConfirmAsync(string title, string message, string okText = "Delete")
    {
        if (_owner is null) return false;
        var dialog = new MessageBoxWindow(title, message, confirm: true, okText: okText);
        return await _Over(dialog).ShowDialog<bool>(_owner);
    }

    public async Task ShowCharacterAsync(CharacterDialogViewModel viewModel)
    {
        if (_owner is null) return;
        var dialog = new CharacterWindow(viewModel);
        await _Over(dialog).ShowDialog(_owner);
    }

    public async Task<bool> ShowServerTrustAsync(string displayName, string address, string fingerprint, string statusLabel)
    {
        if (_owner is null) return false;
        var dialog = new ServerTrustWindow(displayName, address, fingerprint, statusLabel);
        return await _Over(dialog).ShowDialog<bool>(_owner);
    }

    public void ShowFleets(FleetsViewModel viewModel) =>
        Route(new FleetsWindow(viewModel), "FLEETS", "fleet", "fleets");

    public void ShowSettings(string currentDirectory, string detectedDefault, bool shareLocation, bool shareBounty, bool shareCombat, bool loadTypeImages, Theming.FactionTheme currentFaction, string sdeVersionLabel, Func<SettingsResult, Task> onApply, bool openFitDetailAfterImport = true, Notifications.ToastPosition toastPosition = Notifications.ToastPosition.TopRight, bool enableLocalApi = false, int localApiPort = LocalApi.LocalApiServer.DefaultPort, string localApiStatusLabel = "", LocalApi.ILocalApiServer? localApiServer = null, bool checkUpdatesOnStartup = true, Clipboard.ClipboardWatchService? clipboardWatch = null, int initialCategory = 0, bool openFleetRunWindowImmediately = false)
    {
        var window = new SettingsWindow(currentDirectory, detectedDefault, shareLocation, shareBounty, shareCombat, loadTypeImages, currentFaction, sdeVersionLabel, openFitDetailAfterImport, toastPosition, enableLocalApi, localApiPort, localApiStatusLabel, localApiServer, checkUpdatesOnStartup, clipboardWatch, onApply, initialCategory, openFleetRunWindowImmediately);
        Route(window, "SETTINGS", "settings", "settings"); // docked tab in docked mode, floating window otherwise
    }

    public async Task<bool> ShowFleetSharingAsync(FleetShareViewModel viewModel)
    {
        if (_owner is null) return false;
        return await _Over(new FleetShareWindow(viewModel)).ShowDialog<bool>(_owner);
    }

    public void ShowMetrics(MetricsWindowViewModel viewModel) =>
        Route(new MetricsWindow(viewModel), "METRICS", null, "metrics");

    public async Task ShowAboutAsync(AboutViewModel viewModel)
    {
        if (_owner is null) return;
        await _Over(new AboutWindow(viewModel)).ShowDialog(_owner);
    }

    public async Task<bool> ShowUpdateAvailableAsync(string installedVersion, Updates.AppRelease release)
    {
        if (_owner is null) return false;
        return await _Over(new UpdateAvailableWindow(installedVersion, release)).ShowDialog<bool>(_owner);
    }

    public async Task<Fleet.FleetEditResult?> EditFleetAsync(Fleet.FleetInfo? existing)
    {
        if (_owner is null) return null;
        var dialog = existing is null ? new FleetEditWindow() : new FleetEditWindow(existing);
        return await _Over(dialog).ShowDialog<Fleet.FleetEditResult?>(_owner);
    }

    // The composition editor opens as a hosted module (docked tab when docked, floating window when floating) rather
    // than a modal dialog, so it sits alongside the library like the other feature modules. The Task still resolves
    // with whether the composition was saved (so the library reloads) — completed when the editor closes by any path.
    public Task<bool> ShowCompositionEditorAsync(CompositionEditorViewModel viewModel)
    {
        if (_owner is null) return Task.FromResult(false);

        var tcs = new TaskCompletionSource<bool>();
        // Save/Cancel raise CloseRequested with the saved flag — resolve straight from it (independent of how the
        // host then closes the window). Closing the tab/window by its X never raises it, so resolve false there too.
        viewModel.CloseRequested += result => tcs.TrySetResult(result);
        var window = new CompositionEditorWindow(viewModel);
        window.Closed += (_, _) => tcs.TrySetResult(false);

        var title = string.IsNullOrWhiteSpace(viewModel.Name) ? viewModel.Title : viewModel.Name;
        Route(window, title, "compositions", viewModel.ModuleId);
        return tcs.Task;
    }

    public async Task<IReadOnlyList<Fleet.FitReferenceInfo>?> ShowFitPickerAsync(FitPickerViewModel viewModel)
    {
        if (_owner is null) return null;
        return await _Over(new FitPickerWindow(viewModel)).ShowDialog<IReadOnlyList<Fleet.FitReferenceInfo>?>(_owner);
    }

    public async Task<Fleet.FitReferenceInfo?> PickFitAsync(FitPickerViewModel viewModel)
    {
        if (_owner is null) return null;
        return await _Over(new FitPickerWindow(viewModel)).ShowDialog<Fleet.FitReferenceInfo?>(_owner);
    }

    public void ShowInbox(InboxViewModel viewModel)
    {
        _ = viewModel.OnOpenedAsync();   // mark shown messages read so the unread badge clears
        Route(new InboxWindow(viewModel), "INBOX", "inbox", "inbox");
    }

    public void ShowLogs(ClientLogViewModel viewModel) =>
        Route(new LogsWindow(viewModel), "APP LOGS", "logs", "app-logs");

    public void ShowEsiMetrics(EsiMetricsViewModel viewModel) =>
        Route(new EsiMetricsWindow(viewModel), "ESI METRICS", "esi", "esi-metrics");

    public void ShowSettingsSync(SettingsSyncViewModel viewModel) =>
        Route(new SettingsSyncWindow(viewModel), "EVE SETTINGS SYNC", "tools", "settings-sync");

    public void ShowSettingsBackups(SettingsBackupsViewModel viewModel) =>
        Route(new SettingsBackupsWindow(viewModel), "SETTINGS BACKUPS", "tools", "settings-backups");

    public void ShowAppraisal(AppraisalViewModel viewModel) =>
        Route(new AppraisalWindow(viewModel), "APPRAISAL", "tools", "appraisal");

    public async Task ShowPresetExportAsync(PresetExportViewModel viewModel)
    {
        if (_owner is null) return;
        await _Over(new PresetExportWindow(viewModel)).ShowDialog(_owner);
    }

    public async Task<bool> ShowPresetImportAsync(PresetImportViewModel viewModel)
    {
        if (_owner is null) return false;
        await _Over(new PresetImportWindow(viewModel)).ShowDialog(_owner);
        return viewModel.Applied;   // the window's own buttons never decide this — what was written does
    }

    public void ShowFitBrowser(FitBrowserViewModel viewModel) =>
        // One fit-browser module for the whole app (not per-entity, unlike roster/metrics): re-opening re-selects
        // it and refreshes instead of silently handing back the library as it stood at first open (ET-48, same
        // pattern as ET-46).
        Route(new FitBrowserWindow(viewModel), "FIT BROWSER", "fits", "fit-browser");

    public void ShowCompositions(CompositionsViewModel viewModel) =>
        // Same fix as the fit browser above: one compositions module for the whole app, refreshed on re-open
        // instead of re-selecting a stale one (ET-48).
        Route(new CompositionsWindow(viewModel), "COMPOSITIONS", "compositions", "compositions");

    public void ShowFitDetail(FitDetailWindowViewModel viewModel) =>
        Route(new FitDetailWindow(viewModel), string.IsNullOrWhiteSpace(viewModel.Name) ? "FIT DETAIL" : viewModel.Name,
            "fits", viewModel.ModuleId);

    public void ShowTypeInfo(TypeInfoWindowViewModel viewModel)
    {
        if (_owner is null) return;
        // Non-modal info card, shown ownerless so it survives a main-window minimize; tracked so the close
        // handler can tear it down with the rest of the pop-outs.
        var window = new TypeInfoWindow(viewModel);
        _infoPopouts.Add(window);
        window.Closed += (_, _) => _infoPopouts.Remove(window);
        window.Show();
    }

    public async Task<FleetInviteResult?> PickFleetInviteAsync(string fleetName, IReadOnlyList<CharacterPickOption> options)
    {
        if (_owner is null) return null;
        var dialog = new FleetInviteWindow(fleetName, options);
        return await _Over(dialog).ShowDialog<FleetInviteResult?>(_owner);
    }

    public async Task<int?> AddExternalMemberAsync(Fleet.IExternalCharacterLookup lookup)
    {
        if (_owner is null) return null;
        var dialog = new AddExternalMemberWindow(lookup);
        return await _Over(dialog).ShowDialog<int?>(_owner);
    }

    public async Task<string?> PromptTextAsync(string title, string header, string? defaultValue = null)
    {
        if (_owner is null) return null;
        var dialog = new TextPromptWindow(title, header, defaultValue);
        return await _Over(dialog).ShowDialog<string?>(_owner);
    }

    public async Task<bool> ConfirmStartFleetAsync(string fleetName, int unlinkedCount)
    {
        if (_owner is null) return false;
        var dialog = new StartFleetEsiPromptWindow(fleetName, unlinkedCount);
        return await _Over(dialog).ShowDialog<bool>(_owner);
    }

    public void ShowRoster(FleetRosterViewModel viewModel) =>
        // One roster module per fleet (de-duped on the fleet id): MANAGE on a second fleet opens its own window
        // instead of re-selecting the first fleet's roster, which used to stay bound to the original fleet.
        Route(new FleetRosterWindow(viewModel), $"FLEET ROSTER · {viewModel.FleetName}",
            "fleet", $"fleet-roster:{viewModel.FleetId}");

    public void ShowFleetMetrics(FleetMetricsViewModel viewModel) =>
        // One metrics module per fleet, same as the roster above: the title alone used to identify it, so METRICS on
        // a second fleet re-selected the first fleet's screen. Re-opening the SAME fleet's metrics re-selects its
        // module and refreshes its roster — the host calls IRefreshableModule — so a member who joined while the
        // screen stood open is not missing from the totals and the WITH FC badge (ET-46).
        Route(new FleetMetricsWindow(viewModel), $"FLEET METRICS · {viewModel.FleetName}",
            "fleet", $"fleet-metrics:{viewModel.FleetId}");

    public async Task ShowSdeUpdateAsync(SdeProgressViewModel viewModel)
    {
        if (_owner is null) return;
        // Modal: blocks interaction while the static-data store is (re)built; the window closes itself on success.
        await _Over(new SdeProgressWindow(viewModel)).ShowDialog(_owner);
    }
}
