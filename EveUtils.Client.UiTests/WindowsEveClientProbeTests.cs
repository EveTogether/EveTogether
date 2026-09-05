using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using EveUtils.Client.Platform;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-138: which EVE character currently holds OS focus. Proven with a fake window source — neither a running
/// EVE client nor a real EVE-O Preview install exists here — so test budget stays at one counter-proof per
/// acceptance criterion from the ticket's Klaarbewijs: multi-client attribution, and focus landing on something
/// that only looks like a client.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsEveClientProbeTests
{
    [Fact]
    public void CharacterAtForegroundWindow_WithTwoRunningClients_PicksTheFocusedOne()
    {
        var jithranWindow = new IntPtr(1);
        var noahmarrWindow = new IntPtr(2);
        var source = new FakeWindowSource(foreground: noahmarrWindow,
            windows: new()
            {
                [jithranWindow] = (ProcessId: 100u, Title: "EVE - Jithran"),
                [noahmarrWindow] = (ProcessId: 200u, Title: "EVE - Noahmarr"),
            },
            clientPids: [100u, 200u]);

        var probe = new WindowsEveClientProbe(source);

        // Both are genuine, running clients — the answer has to be the one actually focused, not just "a" client.
        Assert.Equal("Noahmarr", probe.CharacterAtForegroundWindow());
    }

    [Fact]
    public void CharacterAtForegroundWindow_WhenFocusIsOnlyALookAlikeTitle_ReadsAsUnknown()
    {
        // Same title text as a real client's window — an EVE-O Preview mirror, say — but a different owning
        // process. Proves the match rides on the PID, not the title string the grooming worried was not unique.
        var mirrorWindow = new IntPtr(3);
        var source = new FakeWindowSource(foreground: mirrorWindow,
            windows: new() { [mirrorWindow] = (ProcessId: 300u, Title: "EVE - Jithran") },
            clientPids: [100u]); // 300 is deliberately not a client pid

        var probe = new WindowsEveClientProbe(source);

        Assert.Null(probe.CharacterAtForegroundWindow());
    }

    private sealed class FakeWindowSource(IntPtr foreground,
        Dictionary<IntPtr, (uint ProcessId, string Title)> windows, HashSet<uint> clientPids)
        : WindowsEveClientProbe.IForegroundWindowSource
    {
        public IntPtr GetForegroundWindow() => foreground;

        public (uint ProcessId, string Title)? Describe(IntPtr handle) =>
            windows.TryGetValue(handle, out var window) ? window : null;

        public bool IsClientProcess(uint processId) => clientPids.Contains(processId);
    }
}
