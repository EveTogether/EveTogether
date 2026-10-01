using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace EveUtils.Client.Opsec;

/// <summary>
/// OPSEC mode (ET-417): while on, every location the app shows or hands out — systems, stations, structures,
/// signatures, routes, jumps — is masked, so a streamer can keep the app on screen without giving away where they are.
/// The one switch every screen, overlay and the Local API follow.
/// </summary>
public interface IOpsecService
{
    /// <summary>Whether locations are masked right now.</summary>
    bool IsEnabled { get; }

    /// <summary>Raised on the UI thread after <see cref="IsEnabled"/> changed.</summary>
    event Action? Changed;

    /// <summary>Reads the persisted state. Called once at startup, before the first window shows.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Switches OPSEC on or off, live, and remembers it across restarts.</summary>
    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Text carrying <see cref="OpsecText"/> markers as it may be shown right now: every marked location
    /// masked while OPSEC is on, readable otherwise; the markers themselves removed either way.</summary>
    [return: NotNullIfNotNull(nameof(text))]
    string? Render(string? text);
}
