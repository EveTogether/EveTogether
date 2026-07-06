namespace EveUtils.Shared.Modules.Sde.Import;

/// <summary>
/// A progress snapshot for an SDE import. The client renders a single popup from this: the download phase shows
/// <see cref="DownloadFraction"/> as 0-100%, then it switches to "<see cref="ProcessedItems"/> / <see cref="TotalItems"/>
/// processed". The server consumes the same stream silently (it just logs). Terminal phases (Completed/
/// AlreadyUpToDate/Failed) tell the popup it may close itself.
/// </summary>
public sealed record SdeImportProgress(
    SdeImportPhase Phase,
    long DownloadedBytes = 0,
    long TotalBytes = 0,
    long ProcessedItems = 0,
    long TotalItems = 0,
    string? CurrentDataset = null,
    string? Error = null)
{
    public double DownloadFraction => TotalBytes > 0 ? (double)DownloadedBytes / TotalBytes : 0;

    public double ProcessFraction => TotalItems > 0 ? (double)ProcessedItems / TotalItems : 0;

    public bool IsTerminal =>
        Phase is SdeImportPhase.Completed or SdeImportPhase.AlreadyUpToDate or SdeImportPhase.Failed;
}
