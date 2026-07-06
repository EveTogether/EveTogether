using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Platform;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Local EVE-client presence: the parsing that turns process evidence (window titles, launcher command lines)
/// into character names/ids, the poller's change gate, and the green dot on the character list. The per-OS
/// process plumbing itself is thin and best-effort by design; the fragile parts are these pure pieces.
/// </summary>
public class EveClientPresenceTests
{
    // --- Title parsing: the EVE client titles a logged-in window "EVE - <name>"; char-select is just "EVE". ---

    [Theory]
    [InlineData("EVE - Jithran", "Jithran")]
    [InlineData("EVE - Jon-Paul", "Jon-Paul")]   // a '-'-split would truncate this to "Jon" (EJT bug class)
    [InlineData("EVE", null)]                     // character-selection screen → not logged in
    [InlineData("EVE Launcher", null)]
    [InlineData("Some other window", null)]
    [InlineData(null, null)]
    public void CharacterNameFromTitle_ParsesLoggedInClientsOnly(string? title, string? expected) =>
        Assert.Equal(expected, EveClientTitleParser.CharacterNameFromTitle(title));

    [Theory]
    [InlineData(@"Z:\eve\bin64\exefile.exe /noconsole /autoSelectCharacter:96123456 /server:tranquility", 96123456)]
    [InlineData("wine exefile.exe /AUTOSELECTCHARACTER:42", 42)]                  // case-insensitive
    [InlineData(@"Z:\eve\bin64\exefile.exe /noconsole", null)]                    // manual login: no selection arg
    [InlineData("/usr/bin/someapp /autoSelectCharacter:7", null)]                 // not an EVE client
    [InlineData(null, null)]
    public void CharacterIdFromCommandLine_ParsesEveClientsOnly(string? commandLine, int? expected) =>
        Assert.Equal(expected, EveClientTitleParser.CharacterIdFromCommandLine(commandLine));

    [Fact]
    public void CharacterNameFromWmctrlLine_ParsesTheTitleColumn()
    {
        Assert.Equal("Catbank", EveClientTitleParser.CharacterNameFromWmctrlLine("0x04000007  0 myhost EVE - Catbank"));
        Assert.Null(EveClientTitleParser.CharacterNameFromWmctrlLine("0x04000008  0 myhost Mozilla Firefox"));
        Assert.Null(EveClientTitleParser.CharacterNameFromWmctrlLine("malformed"));
    }

    // --- Evidence matching: name (case-insensitive) OR ESI id; id 0 (local-only without ESI) never id-matches. ---

    [Fact]
    public void Evidence_MatchesOnNameOrId()
    {
        var evidence = new EveClientEvidence(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Jithran" }, new HashSet<int> { 96000001 });

        Assert.True(evidence.Matches("jithran", 0));        // name, case-insensitive
        Assert.True(evidence.Matches("Other Name", 96000001)); // id fallback (Wayland/mac cmdline evidence)
        Assert.False(evidence.Matches("Noahmarr", 12345));
        Assert.False(evidence.Matches("Noahmarr", 0));      // id 0 = no ESI id → never an id match
    }

    // --- The poller's change gate: Changed fires only when a sweep actually changes the evidence. ---

    private sealed class QueuedProbe : IEveClientProbe
    {
        public Queue<EveClientEvidence> Results { get; } = new();
        public EveClientEvidence Probe() => Results.Count > 0 ? Results.Dequeue() : EveClientEvidence.Empty;
        public bool Activate(string characterName) => false;
    }

    private static EveClientEvidence Names(params string[] names) =>
        new(new HashSet<string>(names, StringComparer.OrdinalIgnoreCase), new HashSet<int>());

    [Fact]
    public void PollOnce_FiresChanged_OnlyOnARealChange()
    {
        var probe = new QueuedProbe();
        using var service = new EveClientPresenceService(NullLogger<EveClientPresenceService>.Instance, probe);
        var fired = 0;
        service.Changed += _ => fired++;

        probe.Results.Enqueue(Names("Jithran"));
        service.PollOnce();
        Assert.Equal(1, fired);
        Assert.True(service.Current.Matches("Jithran", 0));

        probe.Results.Enqueue(Names("Jithran")); // identical sweep → gated
        service.PollOnce();
        Assert.Equal(1, fired);

        probe.Results.Enqueue(EveClientEvidence.Empty); // client closed → change
        service.PollOnce();
        Assert.Equal(2, fired);
        Assert.False(service.Current.Matches("Jithran", 0));
    }

    // --- End-to-end: a presence change reaches the character rows (and survives a list rebuild). ---

    [AvaloniaFact]
    public async Task PresenceChange_MarksTheMatchingCharacterRow_AndSurvivesRebuild()
    {
        var probe = new QueuedProbe();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IEveClientProbe>(probe));
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Jithran", 96000001));

        var vm = new MainWindowViewModel(instance.Services);
        await vm.RefreshCharactersAsync();
        var row = Assert.Single(vm.Characters, c => c.Name == "Jithran");
        Assert.False(row.HasActiveClient);

        // The 5 s sweep finds a running client for Jithran → the row's dot turns on via the Changed event.
        var service = instance.Services.GetRequiredService<EveClientPresenceService>();
        probe.Results.Enqueue(Names("Jithran"));
        service.PollOnce();
        Dispatcher.UIThread.RunJobs();
        Assert.True(Assert.Single(vm.Characters, c => c.Name == "Jithran").HasActiveClient);

        // A list rebuild replaces the rows — the fresh row re-seeds from the latest sweep instead of resetting.
        await vm.RefreshCharactersAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(Assert.Single(vm.Characters, c => c.Name == "Jithran").HasActiveClient);
    }

    // --- Iron Law #9: the presence dot + the mockup status chips ("ESI" / "ET") are on screen — one
    // signed-in + coupled character with a running client, one untouched character. ---

    [AvaloniaFact]
    public void CharacterList_WithPresenceDotAndStatusChips_Renders()
    {
        var vm = new MainWindowViewModel();
        var jithran = new CharacterViewModel(new Character("Jithran", 96000001))
        {
            Affiliation = "Imperial Academy [IAC]", HasActiveClient = true, IsLocal = true
        };
        jithran.ServerLinks.Add(new ServerLinkViewModel(
            96000001, "localhost:7443", "ET", Client.Messaging.ServerConnectionState.Connected, _ => Task.CompletedTask));
        vm.Characters.Add(jithran);
        vm.Characters.Add(new CharacterViewModel(new Character("Lyra Custos")) { Affiliation = "Iron Souls Corp [TIS]" });

        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(Path.GetTempPath(), "eveutils-client-presence-dot.png"));
        window.Close();
    }
}
