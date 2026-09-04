using EveUtils.Client.Runs;
using EveUtils.Client.Theming;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.Dialogs;

/// <summary>
/// Shows modal dialogs over the main window. Implemented in the view layer; the view-model depends
/// only on this abstraction (keeps the VM free of Avalonia <c>Window</c> types).
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Scope-selection dialog: shows each ESI scope the client can request with name + scope +
    /// description (from the scope registry). At sign-in (no <paramref name="preselected"/>) every scope is
    /// checked; on a re-authenticate the granted scopes are passed so only those start checked and the user can
    /// add or drop scopes. Returns the selected scope strings, or null if the user cancelled.
    /// </summary>
    Task<IReadOnlyList<string>?> SelectScopesAsync(IReadOnlyList<EsiScopeRequirement> available,
        IReadOnlyCollection<string>? preselected = null);

    /// <summary>
    /// Fit-import dialog: shows the fits fetched from ESI with checkboxes. Returns the selected
    /// ESI fitting ids, or null if the user cancelled.
    /// </summary>
    Task<IReadOnlyList<int>?> SelectFittingsAsync(IReadOnlyList<EsiFitting> fits);

    /// <summary>
    /// Character-picker dialog: choose which character to perform an action for (import / push).
    /// Returns the chosen character id, or null if cancelled.
    /// </summary>
    Task<int?> PickCharacterAsync(string prompt, IReadOnlyList<CharacterPickOption> options);

    /// <summary>
    /// Multi-select character picker: choose one or more characters for a bulk action (e.g. join / add to a fleet
    /// with several toons at once). Returns the chosen character ids, or null if cancelled.
    /// </summary>
    Task<IReadOnlyList<int>?> PickCharactersAsync(string prompt, IReadOnlyList<CharacterPickOption> options);

    /// <summary>
    /// Couple-server dialog: asks for a server address + optional label. Returns the result,
    /// or null if cancelled. <paramref name="probeServerName"/> is called on open and (debounced) on every
    /// address change to show the server's own name before pairing — an unauthenticated, accept-any-
    /// cert probe; null/throw means "not reachable". Real trust is still established via TOFU at pairing.
    /// </summary>
    /// <param name="prefill">
    /// What is already known about the coupling being restored, filling the fields in so the user only has to
    /// connect and sign in (ET-123). Null for a fresh coupling, where nothing is known yet. Only offered where the
    /// address is not in question — never after a refused certificate, which is exactly the case where the user has
    /// to check the address is still answered by their own server.
    /// </param>
    Task<CoupleServerResult?> CoupleServerAsync(
        Func<string, CancellationToken, Task<string?>> probeServerName, CoupleServerResult? prefill = null);

    /// <summary>
    /// Server-picker dialog: choose which coupled server to share a fit to. Returns the chosen
    /// server address, or null if cancelled.
    /// </summary>
    Task<string?> SelectServerAsync(string prompt, IReadOnlyList<ServerPickOption> options);

    /// <summary>Shows a modal message box (used for error reporting instead of crashing).</summary>
    Task ShowMessageAsync(string title, string message);

    /// <summary>
    /// Paste-a-fit dialog: returns the pasted EFT/DNA text, or null if cancelled/empty.
    /// </summary>
    /// <param name="initialText">Pre-fills the box, for a caller that already has the fit text.</param>
    Task<string?> ImportFitTextAsync(string? initialText = null);

    /// <summary>eveship.fit (ESF) link dialog: returns the pasted link, or null if cancelled/empty. The
    /// link decodes through the same fit-text importer.</summary>
    Task<string?> ImportFitEsfLinkAsync();

    /// <summary>Edit-fit-metadata dialog (fit-metadata): prefilled with the fit's current name/description/tags, returns
    /// the edited <see cref="FitMetadataDraft"/> on Save or null on cancel. The fit's modules/identity are untouched.</summary>
    Task<FitMetadataDraft?> EditFitMetadataAsync(FitMetadataDraft current);

    /// <summary>Export-a-fit dialog: shows the fit as EFT, DNA and an eveship.fit link with copy buttons.</summary>
    Task ShowFitExportAsync(string fitName, string eft, string dna, string eveshipUrl);

    /// <summary>Copies text to the system clipboard: a direct "copy eveship.fit link" without a window.</summary>
    Task SetClipboardTextAsync(string text);

    /// <summary>
    /// Reads the system clipboard's text, or null when it holds no text or is momentarily unreadable (the app that
    /// copied can still have it open). Used by <see cref="EveUtils.Client.Clipboard.ClipboardWatchService"/>, which
    /// is off unless the user turned it on.
    /// </summary>
    Task<string?> GetClipboardTextAsync();

    /// <summary>Yes/No confirmation for destructive actions. Returns true if confirmed.</summary>
    Task<bool> ConfirmAsync(string title, string message, string okText = "Delete");

    /// <summary>A question with two answers and a way out: true for <paramref name="primaryText"/>, false for
    /// <paramref name="secondaryText"/>, null for cancel. Cancel is not a third opinion but the absence of one —
    /// somebody who hit the close button by accident has to be able to take it back.</summary>
    Task<bool?> ChooseAsync(string title, string message, string primaryText, string secondaryText);

    /// <summary>Opens the per-character settings dialog: ESI scopes, coupled servers, couple/decouple.</summary>
    Task ShowCharacterAsync(CharacterDialogViewModel viewModel);

    /// <summary>
    /// Server info/trust dialog: address, live status and the pinned cert fingerprint.
    /// Returns true if the user pressed Decouple inside it.
    /// </summary>
    Task<bool> ShowServerTrustAsync(string displayName, string address, string fingerprint, string statusLabel);

    /// <summary>Opens the Fleets window — non-modal so its live member graphs keep updating alongside the main window.</summary>
    void ShowFleets(FleetsViewModel viewModel);

    /// <summary>Opens the per-character metrics window — non-modal so its live graphs/stats keep updating.</summary>
    void ShowMetrics(MetricsWindowViewModel viewModel);

    /// <summary>Opens the About dialog: app identity + version, creator credits, inspiration links,
    /// the AGPLv3 license and the mandatory CCP attribution. Modal and purely informational.</summary>
    Task ShowAboutAsync(AboutViewModel viewModel);

    /// <summary>
    /// The update offer: both version numbers, the download size and the feed's release notes. Returns true if the
    /// user chose to download and install, false on Later — nothing is fetched before that.
    /// </summary>
    Task<bool> ShowUpdateAvailableAsync(string installedVersion, Updates.AppRelease release);

    /// <summary>Pops a character's live DPS into a borderless overlay: pinnable, opacity-adjustable,
    /// resizable, smoothed graph. Non-modal; one overlay per character (re-opening focuses the existing one).</summary>
    void ShowDpsOverlay(DpsViewModel tracker);

    /// <summary>Closes the pop-out showing exactly this tracker, if one is open — the pop-out's half of "an action on
    /// a fleet member updates every screen showing them" (ET-52). Matched on the tracker instance, not the character
    /// name, so a removed fleet member's pop-out closes while an own-meter pop-out for a same-named pilot does not.
    /// A no-op when nothing is popped out for them.</summary>
    void CloseDpsOverlay(DpsViewModel tracker);

    /// <summary>Pops the whole fleet's readout into a borderless overlay beside the per-character ones (ET-72):
    /// the WITH FC ratio plus who is taking the most damage and who is being neuted the most. Non-modal; one overlay
    /// per fleet, so re-opening focuses the window that is already up rather than stacking a second.</summary>
    void ShowFleetOverlay(FleetOverlayViewModel viewModel);

    /// <summary>Closes the fleet overlay for this fleet, if one is open. Called when the fleet-metrics screen it
    /// reads from goes away: its figures come from that screen's member rows, so without it the window would stand
    /// there for good showing the last frame before the screen closed. A no-op when nothing is open for the fleet.</summary>
    void CloseFleetOverlay(long fleetId);

    /// <summary>Pops the activity window (ET-98) into its own topmost overlay. Non-modal; only one is ever open —
    /// re-opening focuses the run already up instead of stacking a second, same rule as the two overlays above.
    /// <paramref name="trigger"/> decides whether focus may be taken: a run the fleet commander started comes up
    /// on the other members' machines without touching the keyboard (ET-105 AC-2).</summary>
    void ShowActivityWindow(ActivityWindowViewModel viewModel,
        RunWindowOpenTrigger trigger = RunWindowOpenTrigger.LocalUser);

    /// <summary>Whether the activity window is up right now, so a caller can tell there is nothing left to offer —
    /// the same "leave it alone" answer <see cref="RunWindowPresentation"/> gives once one is open.</summary>
    bool IsActivityWindowOpen { get; }

    /// <summary>
    /// Opens the settings module: a docked tab in docked mode, a floating window otherwise — non-modal so it
    /// matches the rest of the module shell. <paramref name="currentDirectory"/> is the saved gamelog path (empty if
    /// none), <paramref name="detectedDefault"/> the platform-probed fallback (Auto-detect). On Save the view invokes
    /// <paramref name="onApply"/> with the chosen values (the caller persists + applies live); Cancel/close does nothing.
    /// </summary>
    void ShowSettings(string currentDirectory, string detectedDefault, bool shareLocation, bool shareBounty, bool shareCombat, bool loadTypeImages, FactionTheme currentFaction, string sdeVersionLabel, Func<SettingsResult, Task> onApply, bool openFitDetailAfterImport = true, Notifications.ToastPosition toastPosition = Notifications.ToastPosition.TopRight, bool enableLocalApi = false, int localApiPort = LocalApi.LocalApiServer.DefaultPort, string localApiStatusLabel = "", LocalApi.ILocalApiServer? localApiServer = null, bool checkUpdatesOnStartup = true, Clipboard.ClipboardWatchService? clipboardWatch = null, int initialCategory = 0, bool openFleetRunWindowImmediately = false);

    /// <summary>Per-fleet sharing dialog: per character a three-way override per metric. Returns true if the user saved.</summary>
    Task<bool> ShowFleetSharingAsync(ViewModels.FleetShareViewModel viewModel);

    /// <summary>
    /// Create/edit-fleet dialog: name, description, visibility and an optional planned window. Pass an
    /// existing fleet to pre-fill + switch to edit mode. Returns the entered values, or null if cancelled.
    /// </summary>
    Task<Fleet.FleetEditResult?> EditFleetAsync(Fleet.FleetInfo? existing);

    /// <summary>
    /// Create/edit-composition dialog: name, description and role groups with their fit entries. The
    /// view-model persists on save (diff-and-replay of the granular commands); returns true if it was saved.
    /// </summary>
    Task<bool> ShowCompositionEditorAsync(CompositionEditorViewModel viewModel);

    /// <summary>
    /// Reusable fit picker: multi-selects fits from the local library or a coupled server. Returns the chosen
    /// fits' snapshots to add to a composition role group, or null if cancelled.
    /// </summary>
    Task<IReadOnlyList<Fleet.FitReferenceInfo>?> ShowFitPickerAsync(FitPickerViewModel viewModel);

    /// <summary>
    /// Single-select fit picker: picks one fit immediately, optionally scoped to a coupled
    /// composition's allowed fits. Returns the chosen fit's snapshot, or null if cancelled.
    /// </summary>
    Task<Fleet.FitReferenceInfo?> PickFitAsync(FitPickerViewModel viewModel);

    /// <summary>Opens the message inbox window — non-modal so deliveries keep landing while it is open;
    /// marks the shown messages read so the unread badge clears.</summary>
    void ShowInbox(InboxViewModel viewModel);

    /// <summary>Shows the client log window non-modally so new entries keep arriving while it is open.</summary>
    void ShowLogs(ClientLogViewModel viewModel);

    /// <summary>Shows the client ESI-metrics window non-modally so the per-bucket counters keep
    /// updating live while it is open.</summary>
    void ShowEsiMetrics(EsiMetricsViewModel viewModel);

    /// <summary>Opens the EVE Settings Sync tool as a hosted module — a docked tab or a floating window, like the
    /// other feature modules. Non-modal: the tool re-reads the folder itself when the user reloads.</summary>
    void ShowSettingsSync(SettingsSyncViewModel viewModel);

    /// <summary>Opens the settings-backups module — a docked tab or a floating window, like the sync tool itself.
    /// Non-modal: it is a place to read a backup and put one back, not a question to answer.</summary>
    void ShowSettingsBackups(SettingsBackupsViewModel viewModel);

    /// <summary>Opens the Appraisal tool as a hosted module — a docked tab or a floating window, like the other
    /// tools. Non-modal: pasting a second list is the next thing the user does, not a reason to reopen it.</summary>
    void ShowAppraisal(AppraisalViewModel viewModel);

    /// <summary>Opens the manual run-start screen (ET-163) as a hosted module — a docked tab or a floating window,
    /// like the other tools.</summary>
    void ShowManualRunStart(ManualRunStartViewModel viewModel);

    /// <summary>Save-a-preset dialog (ET-61): pick what goes in, name it, write it to one portable file. Modal — the
    /// file picker inside it belongs to the window, so the view-model never sees a path it did not ask for.</summary>
    Task ShowPresetExportAsync(PresetExportViewModel viewModel);

    /// <summary>
    /// Read-a-preset dialog (ET-61): opens the file, shows what is in it and where every line would land, and
    /// applies it only on the user's word. Returns true when something was actually written, so the tool behind it
    /// can re-read the profile.
    /// </summary>
    Task<bool> ShowPresetImportAsync(PresetImportViewModel viewModel);

    /// <summary>Shows the FITS fit-browser window non-modally so the Local library and server tabs stay
    /// usable alongside it.</summary>
    void ShowFitBrowser(FitBrowserViewModel viewModel);

    /// <summary>Opens the Fleet Compositions library as a hosted module — a docked tab or a floating
    /// window, like the other feature modules.</summary>
    void ShowCompositions(CompositionsViewModel viewModel);

    /// <summary>Shows the radial fit-detail window non-modally — the fitting wheel plus the computed stats.</summary>
    void ShowFitDetail(FitDetailWindowViewModel viewModel);

    /// <summary>Shows a small "Show Info" card for a module/charge type.</summary>
    void ShowTypeInfo(TypeInfoWindowViewModel viewModel);

    /// <summary>
    /// Invite dialog: pick a connected character, the role to grant on accept and an optional message.
    /// Returns the entered values, or null if cancelled.
    /// </summary>
    Task<FleetInviteResult?> PickFleetInviteAsync(string fleetName, IReadOnlyList<CharacterPickOption> options);

    /// <summary>
    /// Add-external-member dialog: a character-id field with a public-ESI name/affiliation preview on
    /// field-leave. Returns the verified character id, or null if cancelled.
    /// </summary>
    Task<int?> AddExternalMemberAsync(Fleet.IExternalCharacterLookup lookup);

    /// <summary>
    /// Single-line text prompt, used for the add-wing / add-squad name. Returns the trimmed value, or
    /// null if cancelled or left empty.
    /// </summary>
    Task<string?> PromptTextAsync(string title, string header, string? defaultValue = null);

    /// <summary>
    /// On-start ESI-invite prompt — a pure UI seam: when starting a fleet whose members lack an ESI link,
    /// offers a no-op "invite via ESI" checkbox. Returns true if the owner pressed Start (proceed).
    /// </summary>
    Task<bool> ConfirmStartFleetAsync(string fleetName, int unlinkedCount);

    /// <summary>Opens the per-fleet roster window — non-modal so it stays usable beside the fleets window.</summary>
    void ShowRoster(FleetRosterViewModel viewModel);

    /// <summary>
    /// Modal SDE-update popup: shows download 0-100% then "x / y processed", driven by the importer
    /// reporting into <paramref name="viewModel"/>. Closes itself on success; stays open on failure with a Close button.
    /// </summary>
    Task ShowSdeUpdateAsync(SdeProgressViewModel viewModel);

    /// <summary>Opens the free-standing fleet-metrics window — non-modal so its live graphs keep updating.</summary>
    void ShowFleetMetrics(FleetMetricsViewModel viewModel);

    /// <summary>Re-render the open module set after a dock/float switch — migrates modules to the new mode.</summary>
    void SwitchMode();
}
