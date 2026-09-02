using System;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Platform;

/// <summary>
/// Notices that this machine stopped running for a while and came back — waking from sleep or hibernation, a
/// suspended VM, a process the OS froze. Everything that holds a live connection or a timed token needs that moment:
/// the sockets are dead, the ESI access tokens have aged past their hour, and nothing in the app knows yet.
/// <para>Detected from the wall clock rather than from <c>SystemEvents.PowerModeChanged</c>. The power event is
/// Windows-only and needs a message pump, and it answers a narrower question — a frozen process gets no power event
/// at all, and Linux/macOS have no equivalent worth having. A tick that should have come 5 seconds ago and comes back
/// minutes later says the same thing on every platform, with no new dependency and nothing to no-op away.</para>
/// </summary>
public sealed class SystemResumeWatcher(ILogger<SystemResumeWatcher> logger) : BackgroundService, ISingletonService
{
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    // How far a tick has to slip before it reads as a resume rather than as a busy machine. Well clear of the
    // scheduling jitter a loaded desktop produces, well under the shortest nap anyone takes.
    internal static readonly TimeSpan ResumeThreshold = TimeSpan.FromSeconds(30);

    /// <summary>Raised once per detected resume, carrying how long the gap was. Handlers run on a background
    /// thread and must not throw — one that does is logged and cannot stop the others.</summary>
    public event Action<TimeSpan>? Resumed;

    /// <summary>Whether a gap of this length between two consecutive ticks reads as a resume.</summary>
    internal static bool IsResume(TimeSpan sinceLastTick) => sinceLastTick >= ResumeThreshold;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        var previousTick = DateTimeOffset.UtcNow;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = DateTimeOffset.UtcNow;
            var gap = now - previousTick;
            previousTick = now;

            if (!IsResume(gap))
                continue;

            logger.LogInformation(
                "The machine appears to have resumed after {Gap:g} without a tick — rebuilding connections and rechecking tokens.",
                gap);
            Raise(gap);
        }
    }

    private void Raise(TimeSpan gap)
    {
        foreach (var handler in Resumed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<TimeSpan>)handler)(gap);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A resume handler failed; the remaining handlers still ran.");
            }
        }
    }
}
