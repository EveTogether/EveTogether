using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EveUtils.Client.Clipboard;

/// <summary>
/// Wayland clipboard notification through a long-lived <c>wl-paste --watch</c>, which speaks the compositor's
/// data-control protocol and writes one line per change.
/// </summary>
/// <remarks>
/// The command it runs for each change is <c>echo</c>, which ignores the payload handed to it on stdin, so what
/// reaches this process is a bare line: an event, with no previous content to keep and nothing to compare.
/// </remarks>
public sealed class WaylandClipboardChangeSource : IClipboardChangeSource
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();

    private bool _supported = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 };
    private Process? _watcher;

    public bool IsSupported => _supported;

    public event Action? Changed;

    public void Start()
    {
        lock (_gate)
        {
            if (_watcher is not null || !_supported)
                return;

            Process watcher;
            try
            {
                watcher = Process.Start(new ProcessStartInfo("wl-paste")
                {
                    ArgumentList = { "--watch", "echo" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                })!;
            }
            catch (Exception)
            {
                _supported = false; // wl-clipboard is not installed
                return;
            }

            // A compositor that does not offer the data-control protocol makes wl-paste exit at once, and trying
            // is the only capability probe that does not read the clipboard — which is why it happens on Start
            // rather than in the constructor, where the user has not opted in yet.
            if (watcher.WaitForExit(StartupGrace))
            {
                _supported = false;
                watcher.Dispose();
                return;
            }

            _watcher = watcher;
            Task.Run(() => Pump(watcher.StandardOutput));
        }
    }

    public void Stop()
    {
        Process? watcher;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
        }

        if (watcher is null)
            return;

        try
        {
            // wl-paste forks the per-change command, so the tree is what has to go, not just the parent.
            watcher.Kill(entireProcessTree: true);
            watcher.WaitForExit(StopTimeout);
        }
        catch (Exception)
        {
            // Already gone is the outcome this was after.
        }

        watcher.Dispose();
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Turns wl-paste's lines into <see cref="Changed"/>, dropping the one it writes for the clipboard that was
    /// already there when it started.
    /// </summary>
    /// <remarks>
    /// Reads from a <see cref="TextReader"/> rather than the process so this rule can be exercised on any
    /// platform: the lines are the contract, the child process is only how they are produced.
    /// </remarks>
    internal void Pump(TextReader lines)
    {
        // The user did not copy the startup line's payload while watching, so switching on must not read it.
        var startup = true;

        while (lines.ReadLine() is not null)
        {
            if (startup)
            {
                startup = false;
                continue;
            }

            try
            {
                Changed?.Invoke();
            }
            catch (Exception)
            {
                // A throwing subscriber must not end the pump — that would silently stop every later change with
                // no way back short of a restart.
            }
        }
    }
}
