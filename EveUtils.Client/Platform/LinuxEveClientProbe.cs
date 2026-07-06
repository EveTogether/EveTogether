using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace EveUtils.Client.Platform;

/// <summary>
/// Linux probe, two layered signals because neither covers everything:
/// <list type="bullet">
/// <item><c>wmctrl -l</c> window titles → character NAMES. Live-accurate, but X11-only (silent no-op on
/// Wayland or when wmctrl is not installed).</item>
/// <item><c>/proc/*/cmdline</c> of wine/Proton EVE clients (<c>exefile.exe</c>) → the launcher's
/// <c>/autoSelectCharacter:&lt;id&gt;</c> character IDS. Works everywhere incl. Wayland, but is launch intent —
/// stale after an in-client character switch and absent on manual logins.</item>
/// </list>
/// The union of both is the evidence; the presence service matches a character on either.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxEveClientProbe : IEveClientProbe
{
    public EveClientEvidence Probe()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<int>();

        try
        {
            if (RunWmctrl() is { } output)
                foreach (var line in output.Split('\n'))
                    if (EveClientTitleParser.CharacterNameFromWmctrlLine(line) is { } name)
                        names.Add(name);
        }
        catch
        {
            // wmctrl missing or Wayland — fall through to the /proc signal.
        }

        try
        {
            foreach (var procDir in Directory.GetDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(procDir), out _))
                    continue;
                try
                {
                    var cmdlinePath = Path.Combine(procDir, "cmdline");
                    if (File.Exists(cmdlinePath) &&
                        EveClientTitleParser.CharacterIdFromCommandLine(File.ReadAllText(cmdlinePath)) is { } id)
                        ids.Add(id);
                }
                catch
                {
                    // Skip processes we can't read (permission denied, exited mid-sweep).
                }
            }
        }
        catch
        {
            // /proc unreadable — evidence stays whatever the title sweep produced.
        }

        return new EveClientEvidence(names, ids);
    }

    public bool Activate(string characterName)
    {
        // wmctrl -F -a matches the full window title exactly and activates that window. X11-only and silently
        // unavailable on Wayland / when wmctrl is missing — best-effort, mirroring the title-based detection above.
        try
        {
            var startInfo = new ProcessStartInfo("wmctrl") { UseShellExecute = false, CreateNoWindow = true };
            startInfo.ArgumentList.Add("-F");
            startInfo.ArgumentList.Add("-a");
            startInfo.ArgumentList.Add($"{EveClientTitleParser.WindowTitlePrefix}{characterName}");
            using var process = Process.Start(startInfo);
            if (process is null)
                return false;
            process.WaitForExit(2000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? RunWmctrl()
    {
        using var process = Process.Start(new ProcessStartInfo("wmctrl", "-l")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process is null)
            return null;

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(2000);
        return output;
    }
}
