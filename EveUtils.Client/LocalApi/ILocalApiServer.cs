using System;
using System.Threading;
using System.Threading.Tasks;

namespace EveUtils.Client.LocalApi;

/// <summary>
/// Manages the lifecycle of the opt-in local widget API host (an in-process Kestrel server bound to the
/// loopback interface only). Disabled by default; enabled and (re)bound from the Settings dialog. Read-only:
/// it never mutates app state and never exposes credentials.
/// </summary>
public interface ILocalApiServer
{
    /// <summary>The current host state; <see cref="LocalApiStatus.Stopped"/> until enabled.</summary>
    LocalApiStatusSnapshot Status { get; }

    /// <summary>Raised off the UI thread whenever <see cref="Status"/> changes; subscribers marshal themselves.</summary>
    event Action<LocalApiStatusSnapshot>? StatusChanged;

    /// <summary>Reads the persisted enabled/port settings and starts the host if it is enabled. Called once at startup.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies a settings change: stops the host, then (re)starts it on <paramref name="port"/> when enabled.</summary>
    Task ApplyAsync(bool enabled, int port, CancellationToken cancellationToken = default);

    /// <summary>Stops the host if it is running.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
