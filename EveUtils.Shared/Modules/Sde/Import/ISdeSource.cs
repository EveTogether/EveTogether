using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Sde.Import;

/// <summary>Fetches the CCP static-data manifest and downloads the build zip. Abstracted so the importer is testable.</summary>
public interface ISdeSource
{
    /// <summary>Reads <c>latest.jsonl</c> and returns the current build identity.</summary>
    Task<SdeVersion> GetLatestAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the build's JSONL zip to <paramref name="destinationPath"/>. <paramref name="progress"/> receives
    /// (downloadedBytes, totalBytes); totalBytes is 0 when the server omits Content-Length.
    /// </summary>
    Task DownloadZipAsync(
        long buildNumber,
        string destinationPath,
        IProgress<(long Downloaded, long Total)>? progress,
        CancellationToken cancellationToken = default);
}
