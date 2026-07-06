using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace EveUtils.Client.Platform;

/// <summary>
/// Windows probe: enumerates top-level windows owned by the EVE client process ("exefile") whose title is
/// "EVE - &lt;name&gt;". Filtering on the owning process is essential — EVE-O Preview's mirror windows carry the
/// SAME titles, so a bare title scan (or <c>Process.MainWindowTitle</c>) reports false positives. Window titles
/// are the gold signal here: they follow login/logout/character-switch live, so no command-line fallback is needed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsEveClientProbe : IEveClientProbe
{
    private const string ClientProcessName = "exefile";

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
}
