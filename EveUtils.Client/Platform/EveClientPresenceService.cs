using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Platform;

/// <summary>
/// Polls the local machine for running EVE clients (~5 s, mirrors <c>EveServerStatusService</c>) so the character
/// list can show who has an active client right now. The platform probe supplies the evidence (window titles
/// and/or client command lines, see <see cref="EveClientEvidence"/>); this service only owns the cadence and the
/// change gate. <see cref="Changed"/> fires off the UI thread and only on a REAL change — subscribers marshal.
/// </summary>
public sealed class EveClientPresenceService(ILogger<EveClientPresenceService> logger, IEveClientProbe? probe = null)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IEveClientProbe _probe = probe ?? CreatePlatformProbe();

    /// <summary>The latest evidence; empty until the first sweep.</summary>
    public EveClientEvidence Current { get; private set; } = EveClientEvidence.Empty;

    /// <summary>Raised off the UI thread when a sweep changes the evidence; subscribers marshal to their own thread.</summary>
    public event Action<EveClientEvidence>? Changed;

    private static IEveClientProbe CreatePlatformProbe()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsEveClientProbe();
        if (OperatingSystem.IsLinux())
            return new LinuxEveClientProbe();
        if (OperatingSystem.IsMacOS())
            return new MacEveClientProbe();
        return new NullEveClientProbe();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                PollOnce();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error sweeping for running EVE clients.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>One probe sweep + the change gate. Normally driven by the poll loop; public so a test (or a
    /// future "refresh now" action) can drive sweeps without the timer.</summary>
    public void PollOnce()
    {
        var evidence = _probe.Probe();
        if (evidence.SameAs(Current))
            return;

        Current = evidence;
        Changed?.Invoke(evidence);
    }
}
