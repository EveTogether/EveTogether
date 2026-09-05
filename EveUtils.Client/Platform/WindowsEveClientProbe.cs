using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace EveUtils.Client.Platform;

/// <summary>Which EVE character currently holds OS input focus (ET-138) — read-only, and kept separate from
/// <see cref="IEveClientProbe"/> so the many existing test doubles for that interface do not have to grow a
/// member they have no opinion on.</summary>
public interface IForegroundEveClientReader
{
    /// <summary>The character whose EVE client window has OS focus right now, or null when it doesn't — a
    /// different app, a mirror window, or a platform this hasn't been built for. Null means "unknown", never a
    /// guess: the caller decides what unknown means for it, this only reports what was actually seen.</summary>
    string? CharacterAtForegroundWindow();
}

/// <summary>No platform support: focus is always unknown here, same contract as <see cref="NullEveClientProbe"/>.</summary>
public sealed class NullForegroundEveClientReader : IForegroundEveClientReader
{
    public string? CharacterAtForegroundWindow() => null;
}

/// <summary>
/// Windows probe: enumerates top-level windows owned by the EVE client process ("exefile") whose title is
/// "EVE - &lt;name&gt;". Filtering on the owning process is essential — EVE-O Preview's mirror windows carry the
/// SAME titles, so a bare title scan (or <c>Process.MainWindowTitle</c>) reports false positives. Window titles
/// are the gold signal here: they follow login/logout/character-switch live, so no command-line fallback is needed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsEveClientProbe : IEveClientProbe, IForegroundEveClientReader
{
    private const string ClientProcessName = "exefile";

    private readonly IForegroundWindowSource _foreground;

    public WindowsEveClientProbe() : this(new Win32ForegroundWindowSource())
    {
    }

    // Test-only seam (ET-138): a fake window source proves the PID-then-title matching below without a real EVE
    // client or a real EVE-O Preview install — neither exists in this environment or in CI.
    internal WindowsEveClientProbe(IForegroundWindowSource foreground) => _foreground = foreground;

    public EveClientEvidence Probe()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (_, name) in EnumerateClientWindows())
                names.Add(name);
        }
        catch
        {
            // Best-effort: a failed sweep reads as "no client detected" rather than crashing the poller.
        }

        return new EveClientEvidence(names, new HashSet<int>());
    }

    public int RunningClientCount()
    {
        try
        {
            var processes = Process.GetProcessesByName(ClientProcessName);
            foreach (var process in processes)
                process.Dispose();
            return processes.Length;
        }
        catch
        {
            // Best-effort like Probe(): an unreadable process list reads as "saw none".
            return 0;
        }
    }

    public bool Activate(string characterName)
    {
        try
        {
            foreach (var (handle, name) in EnumerateClientWindows())
            {
                if (!string.Equals(name, characterName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (IsIconic(handle))
                    ShowWindow(handle, SW_RESTORE);
                return SetForegroundWindow(handle);
            }
        }
        catch
        {
            // Best-effort: a failed activation reads as "couldn't focus" rather than crashing the click.
        }
        return false;
    }

    /// <summary>The character whose window currently holds OS input focus, or null when it isn't one of ours.
    /// Read-only — GetForegroundWindow + GetWindowThreadProcessId + GetWindowText, nothing that moves focus or
    /// sends input (ET-138; <see cref="Activate"/> is the one method here allowed to do that).</summary>
    public string? CharacterAtForegroundWindow()
    {
        try
        {
            var handle = _foreground.GetForegroundWindow();
            if (handle == IntPtr.Zero)
                return null;

            if (_foreground.Describe(handle) is not { } window)
                return null;

            // PID before title, same order EnumerateClientWindows uses below — that is what makes a look-alike
            // title safe rather than lucky. EVE-O Preview's mirror windows carry the identical "EVE - <name>"
            // text but belong to EVE-O Preview's own process, so they fail this check and read as "no EVE client
            // focused". Duplicate titles are therefore not a risk for this method by construction: it never
            // searches by title, it only asks whether this one already-known handle's owner is a client.
            if (!_foreground.IsClientProcess(window.ProcessId))
                return null;

            return EveClientTitleParser.CharacterNameFromTitle(window.Title);
        }
        catch
        {
            // Best-effort, same contract as Probe()/Activate(): an unreadable focus reads as "unknown".
            return null;
        }
    }

    // Enumerate the visible top-level windows owned by an EVE client process, yielding (handle, character name).
    private static IEnumerable<(IntPtr Handle, string Name)> EnumerateClientWindows()
    {
        var clientPids = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName(ClientProcessName))
        {
            clientPids.Add((uint)process.Id);
            process.Dispose();
        }

        var results = new List<(IntPtr, string)>();
        if (clientPids.Count == 0)
            return results;

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
                return true;

            GetWindowThreadProcessId(handle, out var pid);
            if (!clientPids.Contains(pid))
                return true;

            var length = GetWindowTextLength(handle);
            if (length == 0)
                return true;

            var sb = new StringBuilder(length + 1);
            GetWindowText(handle, sb, sb.Capacity);
            if (EveClientTitleParser.CharacterNameFromTitle(sb.ToString()) is { } name)
                results.Add((handle, name));
            return true;
        }, IntPtr.Zero);

        return results;
    }

    private const int SW_RESTORE = 9;

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // Seam for CharacterAtForegroundWindow only — EnumerateClientWindows above is untouched, so the existing
    // scan keeps calling the real Win32 functions directly rather than growing a second path through this.
    internal interface IForegroundWindowSource
    {
        IntPtr GetForegroundWindow();
        (uint ProcessId, string Title)? Describe(IntPtr handle);
        bool IsClientProcess(uint processId);
    }

    private sealed class Win32ForegroundWindowSource : IForegroundWindowSource
    {
        public IntPtr GetForegroundWindow() => WindowsEveClientProbe.GetForegroundWindow();

        public (uint ProcessId, string Title)? Describe(IntPtr handle)
        {
            var length = GetWindowTextLength(handle);
            if (length == 0)
                return null;

            GetWindowThreadProcessId(handle, out var pid);
            var sb = new StringBuilder(length + 1);
            GetWindowText(handle, sb, sb.Capacity);
            return (pid, sb.ToString());
        }

        public bool IsClientProcess(uint processId)
        {
            foreach (var process in Process.GetProcessesByName(ClientProcessName))
            {
                using (process)
                {
                    if ((uint)process.Id == processId)
                        return true;
                }
            }
            return false;
        }
    }
}
