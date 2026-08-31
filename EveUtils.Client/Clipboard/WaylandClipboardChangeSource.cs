using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EveUtils.Client.Clipboard;

/// <summary>
/// Wayland clipboard notification through a long-lived <c>wl-paste --watch</c>, which speaks the compositor's
/// data-control protocol and writes a line for every change.
/// </summary>
/// <remarks>
/// The command it runs for each change is <c>echo</c>, which ignores the payload handed to it on stdin, so what
/// reaches this process is a bare line: an event, with no previous content to keep and nothing to compare.
/// </remarks>
public sealed class WaylandClipboardChangeSource : IClipboardChangeSource
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();

    private bool _supported = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 };
    private Process? _watcher;

    public bool IsSupported
    {
        get
        {
            lock (_gate)
                return _supported;
        }
    }

    public event Action? Changed;

    public event Action? SupportChanged;

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
                    UseShellExecute = false
                })!;
            }
            catch (Exception)
            {
                _supported = false; // wl-clipboard is not installed
                SupportChanged?.Invoke();
                return;
            }

            // Nothing is waited for here: whether this desktop can notify at all is answered by the first line,
            // and answering it on the calling thread would freeze the UI for as long as the answer takes.
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
    /// Turns wl-paste's lines into <see cref="Changed"/>, after dropping the one it writes for the clipboard that
    /// was already there when it started.
    /// </summary>
    /// <remarks>
    /// Reads from a <see cref="TextReader"/> rather than the process so these rules can be exercised on any
    /// platform: the lines are the contract, the child process is only how they are produced.
    /// </remarks>
    internal void Pump(TextReader lines)
    {
        // The first line does double duty. A desktop that cannot notify makes wl-paste exit without writing one,
        // so its arrival is the capability probe; and its payload was copied before watching began, so switching
        // on must not read it.
        if (lines.ReadLine() is null)
        {
            lock (_gate)
                _supported = false;
            SupportChanged?.Invoke();
            return;
        }

        while (lines.ReadLine() is not null)
        {
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

        // The pump ends when the watcher does: a compositor restart, an OOM kill, or Stop killing it. Clear it so
        // a later Start can replace it, and say so — a silent end is what leaves a switch looking on while nothing
        // arrives. Restarting is deliberately not done: that needs a policy, and switching on again is the retry.
        lock (_gate)
            _watcher = null;
        SupportChanged?.Invoke();
    }
}
