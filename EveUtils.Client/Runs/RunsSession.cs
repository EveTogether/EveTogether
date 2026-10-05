using System;
using System.Diagnostics;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.Runs;

/// <summary>The streamer's session the run totals can be counted over (ET-436): from the moment the app started until
/// the pilot resets it. Kept in memory only — a restart is a new session.</summary>
public sealed class RunsSession : ISingletonService
{
    public DateTime StartedAtUtc { get; private set; } = _AppStartedAtUtc();

    /// <summary>Raised on the caller's thread after <see cref="Reset"/>.</summary>
    public event Action? WasReset;

    public void Reset()
    {
        StartedAtUtc = DateTime.UtcNow;
        WasReset?.Invoke();
    }

    // The process start rather than this object's: it is first resolved whenever something first asks for it.
    private static DateTime _AppStartedAtUtc()
    {
        using Process process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }
}
