using EveUtils.Client.LocalApi;
using EveUtils.Client.Notifications;
using EveUtils.Client.Theming;

namespace EveUtils.Client.Dialogs;

/// <summary>
/// The values returned by the settings dialog: the gamelog directory the watcher tails and the global
/// fleet-sharing defaults — location (opt-IN) plus <see cref="ShareCombat"/> (opt-OUT: one toggle for all live
/// combat data — DPS out/in, neut, cap). These are the baseline for every fleet; a per-fleet override can still change
/// them per fleet. Null = cancelled. <see cref="ReimportSde"/> is set when the user pressed "Re-download &amp; re-import"
/// in the SDE section — the caller saves the other settings and then runs a forced SDE import (fallback/debug).
/// </summary>
public sealed record SettingsResult(
    string GamelogDirectory, bool ShareLocation, bool ShareBounty, bool ShareCombat, bool LoadTypeImages,
    FactionTheme Faction, bool ReimportSde = false, bool OpenFitDetailAfterImport = true,
    ToastPosition ToastPosition = ToastPosition.TopRight,
    bool EnableLocalApi = false, int LocalApiPort = LocalApiServer.DefaultPort);
