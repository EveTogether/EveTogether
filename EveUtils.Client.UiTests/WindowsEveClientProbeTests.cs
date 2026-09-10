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

    /// <summary>ET-211: the diagnostic snapshot carries the game window's title, since that title is the game's own.</summary>
    [Fact]
    public void DescribeForegroundWindow_OnTheClient_ReportsTheTitleToo()
    {
        var jithranWindow = new IntPtr(1);
        var source = new FakeWindowSource(foreground: jithranWindow,
            windows: new() { [jithranWindow] = (ProcessId: 100u, Title: "EVE - Jithran") },
            clientPids: [100u], processNames: new() { [100u] = "exefile" });

        var probe = new WindowsEveClientProbe(source);
        var snapshot = probe.DescribeForegroundWindow();

        Assert.Equal(100, snapshot.ProcessId);
        Assert.Equal("exefile", snapshot.ProcessName);
        Assert.True(snapshot.IsClientProcess);
        Assert.Equal("EVE - Jithran", snapshot.WindowTitle);
    }

    /// <summary>ET-211: for anything that isn't the game, the process name is reported but the title is withheld —
    /// a browser tab or a chat window's title can carry exactly the kind of text that has no business in a log.</summary>
    [Fact]
    public void DescribeForegroundWindow_OnAnotherProgram_WithholdsTheTitle()
    {
        var browserWindow = new IntPtr(4);
        var source = new FakeWindowSource(foreground: browserWindow,
            windows: new() { [browserWindow] = (ProcessId: 400u, Title: "Something private — Browser") },
            clientPids: [100u], processNames: new() { [400u] = "chrome" });

        var probe = new WindowsEveClientProbe(source);
        var snapshot = probe.DescribeForegroundWindow();

        Assert.Equal(400, snapshot.ProcessId);
        Assert.Equal("chrome", snapshot.ProcessName);
        Assert.False(snapshot.IsClientProcess);
        Assert.Null(snapshot.WindowTitle);
    }

    /// <summary>ET-211: nothing focused, or nothing readable about it, reads as unknown rather than a guess.</summary>
    [Fact]
    public void DescribeForegroundWindow_WithNoForegroundWindow_ReadsAsUnknown()
    {
        var source = new FakeWindowSource(foreground: IntPtr.Zero, windows: new(), clientPids: []);

        var probe = new WindowsEveClientProbe(source);

        Assert.Equal(ForegroundWindowSnapshot.Unknown, probe.DescribeForegroundWindow());
    }

    private sealed class FakeWindowSource(IntPtr foreground,
        Dictionary<IntPtr, (uint ProcessId, string Title)> windows, HashSet<uint> clientPids,
        Dictionary<uint, string>? processNames = null)
        : WindowsEveClientProbe.IForegroundWindowSource
    {
        public IntPtr GetForegroundWindow() => foreground;

        public (uint ProcessId, string Title)? Describe(IntPtr handle) =>
            windows.TryGetValue(handle, out var window) ? window : null;

        public bool IsClientProcess(uint processId) => clientPids.Contains(processId);

        public string? ProcessName(uint processId) =>
            processNames is not null && processNames.TryGetValue(processId, out var name) ? name : null;
    }
}
