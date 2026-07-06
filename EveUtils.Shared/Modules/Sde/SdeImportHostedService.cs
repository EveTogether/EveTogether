using EveUtils.Shared.Modules.Sde.Import;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Shared.Modules.Sde;

/// <summary>
/// Server-side autonomous SDE updater: on startup it brings the store up to date silently (no UI, log-only) and
/// without blocking the host — a slow first build runs on a background task while the server serves requests off
/// the previous good store (or none yet). The client does NOT use this; it prompts the user and shows progress.
/// </summary>
public sealed class SdeImportHostedService(
    ISdeImporter importer, ILogger<SdeImportHostedService> logger) : IHostedService
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _worker = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await importer.EnsureUpToDateAsync(progress: null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Host shutting down mid-import; the partial build was discarded by the importer.
        }
        catch (Exception ex)
        {
            // Never let a background import fault tear down the host — the server keeps running without SDE.
            logger.LogError(ex, "SDE startup import failed; server continues without an updated store.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cts.CancelAsync();
        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(cancellationToken);
            }
            catch
            {
                // Best-effort drain on shutdown.
            }
        }
    }
}
