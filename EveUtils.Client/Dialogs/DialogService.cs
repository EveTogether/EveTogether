using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using EveUtils.Client.Notifications;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.DependencyInjection;
using Material.Icons;

namespace EveUtils.Client.Dialogs;

/// <summary>
/// Avalonia implementation of <see cref="IDialogService"/>. The owner window is set once from
/// <c>App.OnFrameworkInitializationCompleted</c> after the main window is created. Called from
/// view-model commands, which already run on the UI thread.
/// </summary>
public sealed class DialogService : IDialogService, ISingletonService
{
    // Optional so the eight tests that build this service by hand keep working: without it a fault still reaches
    // stderr, it just has no window to say so in.
    private readonly IToastService? _toasts;

    public DialogService(IToastService? toasts = null) => _toasts = toasts;

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
    // The icon is the tab's, and is chosen per SCREEN rather than per rail group (ET-171): one fleet can put three
    // tabs in the strip and they all read "FLEET…", so a symbol shared by the whole group would separate nothing.
    // Where a screen has a rail entry the rail's own icon is reused, so the strip and the rail agree.
    private void Route(Window window, string title, string? moduleKey, string moduleId, MaterialIconKind icon) =>
        _moduleHost.Open(window, title, moduleKey, moduleId, icon);

    /// <summary>
    /// Watches a load this service starts but does not await, so a fault is seen instead of vanishing into an
    /// unobserved task — the same rule <c>Program.RunResilient</c> holds for background services, and the trap
    /// ET-158 fell into when <c>_StartOnArrivalAsync</c> ran outside its <c>try</c>. A locked database is the case
    /// that really happens, and without this the pilot gets a screen showing the wrong thing and no reason for it.
    /// </summary>
    private void _Observe(Task task, string what) =>
        task.ContinueWith(faulted =>
        {
            Console.Error.WriteLine($"[dialog] {what}: {faulted.Exception}");
            // Back to the UI thread: a continuation runs on the pool, and a toast is a window operation.
            Dispatcher.UIThread.Post(() => _toasts?.Show("Screen out of date", what, ToastKind.Error));
        }, TaskContinuationOptions.OnlyOnFaulted);

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
        if (_activityWindow?.DataContext is ActivityWindowViewModel open && !ReferenceEquals(open, viewModel))
        {
            if (viewModel.SignatureName is { Length: > 0 } signature)
            {
                // The incoming view model is dropped here, so what it was asked to do travels with the signature or an
                // automatic start would only ever happen on the window that did not exist yet (ET-158).
                open.StartsOnArrival = viewModel.StartsOnArrival;
                // Including whose run it is, and before ApplySignature/ApplyMission rather than after: that is what
                // settles the character, and a caller that already asked would otherwise be asked again by the window
                // that was already up.
                //
                // Only to a window with no run of its own. A run on the clock belongs to the pilot who started it, and
                // the apply below may well keep it — a copy of what is already being flown changes nothing — so
                // writing a different pilot over it would leave the header, the gamelog filter and the stored row
                // disagreeing.
                if (open.RunId is null && viewModel.PickedCharacter is { } pilot)
                    open.UseCharacter(pilot.Id, pilot.Name);

                // A mission carries no scan id and no site catalogue match — same two routes ET-158 fixed for a
                // signature, applied to what ClipboardMissionOffer hands over instead (ET-172 sub 4).
                if (viewModel.Kind == ActivityKind.Mission)
                    open.ApplyMission(viewModel.MissionAgentId, viewModel.MissionLevel, viewModel.MissionSolarSystemId,
                        signature, viewModel.PendingParameters);
                else
                    open.ApplySignature(viewModel.SignatureId, viewModel.SignatureGroup, signature, viewModel.MatchedSites);
            }
            else
            {
                // No signature means the caller is asking for "whatever run the store has open now" — the manual
                // start (ET-163) and the runs-overview lane both do. A window already up adopted its run when it
                // opened and never looks again, so raising it would show the previous run and hide the one just
                // started. Re-reading is what it does on open anyway, and every side effect in there is guarded.
                _Observe(open.LoadAsync(), "the run window could not be brought up to date");
            }
        }

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

    public (int Id, string Name)? ActivityWindowPilot =>
        (_activityWindow?.DataContext as ActivityWindowViewModel)?.PickedCharacter;

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

    public async Task<bool?> ChooseAsync(string title, string message, string primaryText, string secondaryText)
    {
        if (_owner is null) return null;
        var dialog = new MessageBoxWindow(title, message, confirm: true, okText: primaryText, secondaryText: secondaryText);
        return await _Over(dialog).ShowDialog<bool?>(_owner);
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
        Route(new FleetsWindow(viewModel), "FLEETS", "fleet", "fleets", MaterialIconKind.AccountGroupOutline);

    public void ShowSettings(string currentDirectory, string detectedDefault, bool shareLocation, bool shareBounty, bool shareCombat, bool loadTypeImages, Theming.FactionTheme currentFaction, string sdeVersionLabel, Func<SettingsResult, Task> onApply, bool openFitDetailAfterImport = true, Notifications.ToastPosition toastPosition = Notifications.ToastPosition.TopRight, bool enableLocalApi = false, int localApiPort = LocalApi.LocalApiServer.DefaultPort, string localApiStatusLabel = "", LocalApi.ILocalApiServer? localApiServer = null, bool checkUpdatesOnStartup = true, Clipboard.ClipboardWatchService? clipboardWatch = null, int initialCategory = 0, bool openFleetRunWindowImmediately = false)
    {
        var window = new SettingsWindow(currentDirectory, detectedDefault, shareLocation, shareBounty, shareCombat, loadTypeImages, currentFaction, sdeVersionLabel, openFitDetailAfterImport, toastPosition, enableLocalApi, localApiPort, localApiStatusLabel, localApiServer, checkUpdatesOnStartup, clipboardWatch, onApply, initialCategory, openFleetRunWindowImmediately);
        Route(window, "SETTINGS", "settings", "settings", MaterialIconKind.TuneVariant); // docked tab in docked mode, floating window otherwise
    }

    public async Task<bool> ShowFleetSharingAsync(FleetShareViewModel viewModel)
    {
        if (_owner is null) return false;
        return await _Over(new FleetShareWindow(viewModel)).ShowDialog<bool>(_owner);
    }

    public void ShowMetrics(MetricsWindowViewModel viewModel) =>
        Route(new MetricsWindow(viewModel), "METRICS", null, "metrics", MaterialIconKind.ChartBar);

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
        Route(window, title, "compositions", viewModel.ModuleId, MaterialIconKind.ViewGridOutline);
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
        _Observe(viewModel.OnOpenedAsync(), "the inbox could not be marked as read");
        Route(new InboxWindow(viewModel), "INBOX", "inbox", "inbox", MaterialIconKind.EmailOutline);
    }

    public void ShowLogs(ClientLogViewModel viewModel) =>
        Route(new LogsWindow(viewModel), "APP LOGS", "logs", "app-logs", MaterialIconKind.FileDocumentOutline);

    public void ShowEsiMetrics(EsiMetricsViewModel viewModel) =>
        Route(new EsiMetricsWindow(viewModel), "ESI METRICS", "esi", "esi-metrics", MaterialIconKind.ChartBar);

    public void ShowSettingsSync(SettingsSyncViewModel viewModel) =>
        Route(new SettingsSyncWindow(viewModel), "EVE SETTINGS SYNC", "tools", "settings-sync", MaterialIconKind.Sync);

    public void ShowSettingsBackups(SettingsBackupsViewModel viewModel) =>
        Route(new SettingsBackupsWindow(viewModel), "SETTINGS BACKUPS", "tools", "settings-backups", MaterialIconKind.BackupRestore);

    public void ShowAppraisal(AppraisalViewModel viewModel) =>
        Route(new AppraisalWindow(viewModel), "APPRAISAL", "tools", "appraisal", MaterialIconKind.CurrencyUsd);

    public void ShowActivityDetail(ActivityDetailViewModel viewModel, Guid activitySummaryId)
    {
        _Observe(viewModel.LoadAsync(), "this screen could not be read");
        Route(new ActivityDetailWindow(viewModel), "ACTIVITY", "runs", $"activity-{activitySummaryId}", MaterialIconKind.TimelineTextOutline);
    }

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

    public void ShowRuns(RunsOverviewViewModel viewModel)
    {
        _Observe(viewModel.LoadAsync(), "this screen could not be read");
        Route(new RunsWindow(viewModel), "RUNS", "runs", "runs", MaterialIconKind.RocketLaunchOutline);
    }

    /// <summary>A modal dialog rather than a docked module (ET-163 nazorg): filling in a run is a moment, not a
    /// screen you keep open — the run itself lives in the activity window, which the view model opens on its way
    /// out.</summary>
    public async Task ShowManualRunStartAsync(ManualRunStartViewModel viewModel)
    {
        if (_owner is null) return;
        await _Over(new ManualRunStartWindow(viewModel)).ShowDialog(_owner);
    }

    public void ShowFitBrowser(FitBrowserViewModel viewModel) =>
        // One fit-browser module for the whole app (not per-entity, unlike roster/metrics): re-opening re-selects
        // it and refreshes instead of silently handing back the library as it stood at first open (ET-48, same
        // pattern as ET-46).
        Route(new FitBrowserWindow(viewModel), "FIT BROWSER", "fits", "fit-browser", MaterialIconKind.WrenchOutline);

    public void ShowCompositions(CompositionsViewModel viewModel) =>
        // Same fix as the fit browser above: one compositions module for the whole app, refreshed on re-open
        // instead of re-selecting a stale one (ET-48).
        Route(new CompositionsWindow(viewModel), "COMPOSITIONS", "compositions", "compositions", MaterialIconKind.ViewGridOutline);

    public void ShowFitDetail(FitDetailWindowViewModel viewModel) =>
        // The fits wrench, shared with the browser: a fit detail is titled after the fit, so its tab is the one that
        // most needs something saying which module it belongs to at all.
        Route(new FitDetailWindow(viewModel), string.IsNullOrWhiteSpace(viewModel.Name) ? "FIT DETAIL" : viewModel.Name,
            "fits", viewModel.ModuleId, MaterialIconKind.WrenchOutline);

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

    public async Task<StopFleetChoice> PickFleetExitAsync(StopFleetPrompt prompt)
    {
        if (_owner is null) return StopFleetChoice.Cancel;
        var dialog = new StopFleetWindow(prompt);
        return await _Over(dialog).ShowDialog<StopFleetChoice>(_owner);
    }

    public void ShowRoster(FleetRosterViewModel viewModel) =>
        // One roster module per fleet (de-duped on the fleet id): MANAGE on a second fleet opens its own window
        // instead of re-selecting the first fleet's roster, which used to stay bound to the original fleet.
        Route(new FleetRosterWindow(viewModel), $"FLEET ROSTER · {viewModel.FleetName}",
            "fleet", $"fleet-roster:{viewModel.FleetId}", MaterialIconKind.FormatListBulletedSquare);

    public void ShowFleetMetrics(FleetMetricsViewModel viewModel) =>
        // One metrics module per fleet, same as the roster above: the title alone used to identify it, so METRICS on
        // a second fleet re-selected the first fleet's screen. Re-opening the SAME fleet's metrics re-selects its
        // module and refreshes its roster — the host calls IRefreshableModule — so a member who joined while the
        // screen stood open is not missing from the totals and the WITH FC badge (ET-46).
        Route(new FleetMetricsWindow(viewModel), $"FLEET METRICS · {viewModel.FleetName}",
            "fleet", $"fleet-metrics:{viewModel.FleetId}", MaterialIconKind.ChartLine);

    public async Task ShowSdeUpdateAsync(SdeProgressViewModel viewModel)
    {
        if (_owner is null) return;
        // Modal: blocks interaction while the static-data store is (re)built; the window closes itself on success.
        await _Over(new SdeProgressWindow(viewModel)).ShowDialog(_owner);
    }
}
