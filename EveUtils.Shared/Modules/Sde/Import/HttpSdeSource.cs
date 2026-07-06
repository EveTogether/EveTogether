using System.Text.Json;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Sde.Import;

/// <summary>
/// <see cref="ISdeSource"/> over CCP's static-data CDN, using the named "sde" <see cref="IHttpClientFactory"/>
/// client (central User-Agent — a nice CCP citizen). The download streams to disk with progress so a one-time
/// ~80 MB fetch never buffers in memory.
/// </summary>
public sealed class HttpSdeSource(IHttpClientFactory httpClientFactory) : ISdeSource, ISingletonService
{
    public async Task<SdeVersion> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(SdeEndpoints.HttpClientName);
        await using var stream = await client.GetStreamAsync(SdeEndpoints.LatestManifest, cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var build = root.GetProperty("buildNumber").GetInt64();
        var release = root.TryGetProperty("releaseDate", out var releaseElement)
            && releaseElement.TryGetDateTimeOffset(out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
        return new SdeVersion(build, release);
    }

    public async Task DownloadZipAsync(
        long buildNumber,
        string destinationPath,
        IProgress<(long Downloaded, long Total)>? progress,
        CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(SdeEndpoints.HttpClientName);
        using var response = await client.GetAsync(
            SdeEndpoints.DataZip(buildNumber),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20, useAsync: true);

        var buffer = new byte[1 << 20];
        long downloaded = 0;
        long lastReported = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            // Throttle reports to ~1 MB steps so the UI updates smoothly without flooding the dispatcher.
            if (progress is not null && (downloaded - lastReported >= 1 << 20 || (total > 0 && downloaded == total)))
            {
                progress.Report((downloaded, total));
                lastReported = downloaded;
            }
        }
        progress?.Report((downloaded, total == 0 ? downloaded : total));
    }
}
