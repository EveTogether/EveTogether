using EveUtils.Client.Theming;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.FitBrowser;
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
    Task<CoupleServerResult?> CoupleServerAsync(Func<string, CancellationToken, Task<string?>> probeServerName);

    /// <summary>
    /// Server-picker dialog: choose which coupled server to share a fit to. Returns the chosen
    /// server address, or null if cancelled.
    /// </summary>
    Task<string?> SelectServerAsync(string prompt, IReadOnlyList<ServerPickOption> options);

    /// <summary>Shows a modal message box (used for error reporting instead of crashing).</summary>
    Task ShowMessageAsync(string title, string message);

    /// <summary>Paste-a-fit dialog: returns the pasted EFT/DNA text, or null if cancelled/empty.</summary>
    Task<string?> ImportFitTextAsync();

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

    /// <summary>Yes/No confirmation for destructive actions. Returns true if confirmed.</summary>
    Task<bool> ConfirmAsync(string title, string message, string okText = "Delete");

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

    /// <summary>Pops a character's live DPS into a borderless overlay: pinnable, opacity-adjustable,
    /// resizable, smoothed graph. Non-modal; one overlay per character (re-opening focuses the existing one).</summary>
    void ShowDpsOverlay(DpsViewModel tracker);

    /// <summary>
    /// Opens the settings module: a docked tab in docked mode, a floating window otherwise — non-modal so it
    /// matches the rest of the module shell. <paramref name="currentDirectory"/> is the saved gamelog path (empty if
    /// none), <paramref name="detectedDefault"/> the platform-probed fallback (Auto-detect). On Save the view invokes
    /// <paramref name="onApply"/> with the chosen values (the caller persists + applies live); Cancel/close does nothing.
    /// </summary>
    void ShowSettings(string currentDirectory, string detectedDefault, bool shareLocation, bool shareBounty, bool shareCombat, bool loadTypeImages, FactionTheme currentFaction, string sdeVersionLabel, Func<SettingsResult, Task> onApply, bool openFitDetailAfterImport = true, Notifications.ToastPosition toastPosition = Notifications.ToastPosition.TopRight, bool enableLocalApi = false, int localApiPort = LocalApi.LocalApiServer.DefaultPort, string localApiStatusLabel = "", LocalApi.ILocalApiServer? localApiServer = null);

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
