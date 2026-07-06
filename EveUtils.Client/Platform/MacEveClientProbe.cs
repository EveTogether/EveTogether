using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace EveUtils.Client.Platform;

/// <summary>
/// macOS probe: the macOS EVE client is wine-based, so its process command line carries the same
/// <c>exefile.exe … /autoSelectCharacter:&lt;id&gt;</c> shape as on Linux — read it via <c>ps</c> (no permissions
/// needed). Window titles would need AppleScript + the Accessibility permission, which is too fragile for a
/// background poller, so this probe yields character IDS only (launch intent; see <see cref="EveClientEvidence"/>).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacEveClientProbe : IEveClientProbe
{
    public EveClientEvidence Probe()
    {
        var ids = new HashSet<int>();
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ps", "-axo command=")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
                return EveClientEvidence.Empty;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);

            foreach (var line in output.Split('\n'))
                if (EveClientTitleParser.CharacterIdFromCommandLine(line) is { } id)
                    ids.Add(id);
        }
        catch
        {
            // Best-effort: no ps output reads as "no client detected".
        }

        return new EveClientEvidence(new HashSet<string>(StringComparer.OrdinalIgnoreCase), ids);
    }

    // Focusing a specific client window would need AppleScript + the Accessibility permission, too fragile to wire
    // here — so click-to-focus is a no-op on macOS (the overlay click simply does nothing).
    public bool Activate(string characterName) => false;
}
