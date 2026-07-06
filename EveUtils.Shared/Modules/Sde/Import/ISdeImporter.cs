namespace EveUtils.Shared.Modules.Sde.Import;

/// <summary>
/// Orchestrates keeping the local SDE store current: compare builds, download, build a fresh SQLite and atomically
/// swap it in. The server calls <see cref="EnsureUpToDateAsync"/> silently on startup; the client calls
/// <see cref="CheckForUpdateAsync"/> to decide whether to prompt, then <see cref="ImportAsync"/> on accept with a
/// progress sink driving the popup.
/// </summary>
public interface ISdeImporter
{
    /// <summary>Compares the local store against CCP's latest manifest (true when missing or a newer build exists).</summary>
    Task<SdeUpdateCheck> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>Imports only when an update is available; otherwise reports <see cref="SdeImportPhase.AlreadyUpToDate"/>.</summary>
    Task<SdeImportResult> EnsureUpToDateAsync(
        IProgress<SdeImportProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Fetches the latest build and (re)imports it unconditionally — used after the user accepts the prompt.</summary>
    Task<SdeImportResult> ImportAsync(
        IProgress<SdeImportProgress>? progress = null, CancellationToken cancellationToken = default);
}
