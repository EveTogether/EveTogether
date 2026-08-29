using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.EveSettings;
using EveUtils.Client.Fleet;
using EveUtils.Client.Platform;
using EveUtils.Client.Theming;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The EVE Settings Sync tool as the user meets it (ET-59): the Tools menu that opens it, the two separated blocks,
/// the running-client block, naming an account, and the screen actually rendering — empty, with one profile and
/// with several — in both shells. Every test runs against a throwaway settings folder, never a real EVE install.
/// </summary>
public sealed class SettingsSyncToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eveutils-synctool-" + Guid.NewGuid().ToString("N"));

    /// <summary>Public ESI never runs in a test: an unknown character id resolves to "not found" without a call.</summary>
    private sealed class OfflineEsiSource : IExternalCharacterEsiSource
    {
        public Task<ExternalCharacterInfo> FetchAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ExternalCharacterInfo.Unknown(characterId));
    }

    private sealed class FakeClientProbe : IEveClientProbe
    {
        public int Processes { get; set; }
        public EveClientEvidence Evidence { get; set; } = EveClientEvidence.Empty;
        public EveClientEvidence Probe() => Evidence;
        public int RunningClientCount() => Processes;
        public bool Activate(string characterName) => false;
    }

    private string _WriteProfile(string name, (long Id, string Content)[] characters, (long Id, string Content)[] accounts)
    {
        var directory = Path.Combine(_root, "install", name);
        Directory.CreateDirectory(directory);
        foreach (var (id, content) in characters)
            File.WriteAllText(Path.Combine(directory, $"core_char_{id}.dat"), content);
        foreach (var (id, content) in accounts)
            File.WriteAllText(Path.Combine(directory, $"core_user_{id}.dat"), content);
        return directory;
    }

    private string InstallRoot => Path.Combine(_root, "install");

    private static TestClientInstance _NewInstance(FakeClientProbe? probe = null) =>
        TestClientInstance.Create(services =>
        {
            services.AddSingleton<IExternalCharacterEsiSource, OfflineEsiSource>();
            services.AddSingleton<IEveClientProbe>(probe ?? new FakeClientProbe());
        });

    /// <summary>Points the tool at the scratch folder before it is built, so its own load never reads the machine's
    /// real EVE installation.</summary>
    private async Task<SettingsSyncViewModel> _BuildToolAsync(TestClientInstance instance, IDialogService? dialogs = null)
    {
        await instance.Services.GetRequiredService<ISettingRepository>()
            .UpsertAsync("eve-settings.install-root", InstallRoot);

        var tool = new SettingsSyncViewModel(
            instance.Services.GetRequiredService<SettingsSyncService>(),
            instance.Services.GetRequiredService<SettingsBackupService>(),
            instance.Services.GetRequiredService<EveSettingsNameResolver>(),
            instance.Services.GetRequiredService<EveSettingsPreferences>(),
            instance.Services.GetRequiredService<EveClientPresenceService>(),
            dialogs);
        await tool.LoadAsync();
        return tool;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Scratch files.
        }
    }

    // ── Names, not ids ───────────────────────────────────────────────────────────────────────────

    /// <summary>A linked character shows the name from the registry; an id that resolves nowhere still reads as
    /// something, and an account carries the name the user gave it rather than its number.</summary>
    [AvaloniaFact]
    public async Task Tool_ShowsNamesForLinkedCharactersAndNamedAccounts()
    {
        _WriteProfile("settings_Default", [(90000001, "alice"), (90000002, "bob")], [(1001, "account one")]);
        using var instance = _NewInstance();
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Jithran", 90000001));
        await instance.Services.GetRequiredService<EveSettingsPreferences>().SaveAccountNameAsync(1001, "Main account");

        var tool = await _BuildToolAsync(instance);

        Assert.Equal("settings_Default", tool.SelectedProfileName);
        Assert.Contains(tool.Characters, row => row.DisplayName == "Jithran");
        Assert.Contains(tool.Characters, row => row.DisplayName == "Character 90000002");   // never blank, never crashes
        var account = Assert.Single(tool.Accounts);
        Assert.Equal("Main account", account.DisplayName);
        Assert.False(account.NeedsName);
    }

    /// <summary>An account with no name yet says so and offers to be named — the placeholder is a prompt, not a
    /// number the user has to live with.</summary>
    [AvaloniaFact]
    public async Task Tool_MarksAnUnnamedAccount_AndRemembersTheNameTheUserGivesIt()
    {
        _WriteProfile("settings_Default", [(90000001, "alice")], [(1001, "account one")]);
        using var instance = _NewInstance();
        var dialogs = new RecordingDialogService { OnPromptText = (_, _, _) => Task.FromResult<string?>("Alt account") };

        var tool = await _BuildToolAsync(instance, dialogs);
        var account = Assert.Single(tool.Accounts);
        Assert.True(account.NeedsName);
        Assert.Equal(EveSettingsNames.UnnamedAccount, account.DisplayName);

        tool.NameAccountCommand.Execute(account);
        await _WaitForAsync(() => account.DisplayName == "Alt account");

        Assert.False(account.NeedsName);
        var stored = await instance.Services.GetRequiredService<EveSettingsPreferences>().LoadAccountNamesAsync();
        Assert.Equal("Alt account", stored[1001]);   // survives the next open
    }

    // ── The two blocks stay apart ────────────────────────────────────────────────────────────────

    /// <summary>The character block only ever plans character files and the account block only account files: the
    /// source lists are separate, so there is no gesture that crosses them.</summary>
    [AvaloniaFact]
    public async Task Tool_PlansPerBlock_AndNeverCrossesCharacterAndAccountFiles()
    {
        _WriteProfile("settings_Default", [(90000001, "alice"), (90000002, "bob")], [(1001, "one"), (1002, "two")]);
        using var instance = _NewInstance();
        var tool = await _BuildToolAsync(instance);

        tool.CharacterSource = tool.Characters.First();
        foreach (var row in tool.Characters.Skip(1))
            row.IsTarget = true;
        tool.AccountSource = tool.Accounts.First();
        tool.Accounts.Last().IsTarget = true;

        var characterPlan = tool.BuildPlan(SettingsFileKind.Character);
        var accountPlan = tool.BuildPlan(SettingsFileKind.Account);

        Assert.NotNull(characterPlan);
        Assert.NotNull(accountPlan);
        Assert.All(characterPlan!.Targets, file => Assert.Equal(SettingsFileKind.Character, file.Kind));
        Assert.All(accountPlan!.Targets, file => Assert.Equal(SettingsFileKind.Account, file.Kind));
    }

    /// <summary>Picking a row as the source clears and locks its own tick, so nothing can copy onto itself.</summary>
    [AvaloniaFact]
    public async Task Tool_SourceRow_CannotAlsoBeATarget()
    {
        _WriteProfile("settings_Default", [(90000001, "alice"), (90000002, "bob")], []);
        using var instance = _NewInstance();
        var tool = await _BuildToolAsync(instance);

        tool.SelectAllCharactersCommand.Execute(null);
        tool.CharacterSource = tool.Characters.First();

        Assert.False(tool.Characters.First().IsTarget);
        Assert.False(tool.Characters.First().CanBeTarget);
        Assert.Single(tool.BuildPlan(SettingsFileKind.Character)!.Targets);
    }

    /// <summary>The preview says what the button will do, in names, before it is pressed.</summary>
    [AvaloniaFact]
    public async Task Tool_PreviewsThePlanBeforeAnythingIsWritten()
    {
        _WriteProfile("settings_Default", [(90000001, "alice"), (90000002, "bob")], []);
        using var instance = _NewInstance();
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Jithran", 90000001));
        var tool = await _BuildToolAsync(instance);

        Assert.Contains("Choose the character", tool.CharacterPlanSummary);

        tool.CharacterSource = tool.Characters.Single(row => row.DisplayName == "Jithran");
        Assert.Contains("Tick the characters", tool.CharacterPlanSummary);

        tool.Characters.Single(row => row.Id == 90000002).IsTarget = true;
        Assert.Contains("Jithran →", tool.CharacterPlanSummary);
        Assert.Contains("backed up first", tool.CharacterPlanSummary);
    }

    // ── Running clients ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A running client makes a sync pointless — EVE rewrites these files on exit. The block has to fire on the
    /// process itself, not only on who is visibly in-game: a client sitting on the login screen shows up in no
    /// game log and still overwrites everything at logout.
    /// </summary>
    [AvaloniaFact]
    public async Task Tool_BlocksWhileAnEveClientRuns_EvenWithNobodyLoggedIn()
    {
        _WriteProfile("settings_Default", [(90000001, "alice"), (90000002, "bob")], []);
        var probe = new FakeClientProbe { Processes = 1 };   // running, but no character in-game
        using var instance = _NewInstance(probe);
        var tool = await _BuildToolAsync(instance);

        tool.CharacterSource = tool.Characters.First();
        tool.Characters.Last().IsTarget = true;

        Assert.True(tool.ClientsRunning);
        Assert.False(tool.CanSyncCharacters);
        Assert.Contains("running", tool.ClientWarning);

        // A copy attempted anyway changes nothing on disk and says why.
        await tool.SyncCharactersCommand.ExecuteAsync(null);
        Assert.Equal("bob", File.ReadAllText(Path.Combine(InstallRoot, "settings_Default", "core_char_90000002.dat")));
        Assert.True(tool.StatusIsError);

        probe.Processes = 0;
        tool.CheckClientsCommand.Execute(null);
        Assert.False(tool.ClientsRunning);
        Assert.True(tool.CanSyncCharacters);
    }

    /// <summary>With clients in-game the warning names them, so the user knows which window to close.</summary>
    [AvaloniaFact]
    public async Task Tool_NamesTheCharactersWhoseClientsAreStillOpen()
    {
        _WriteProfile("settings_Default", [(90000001, "alice")], []);
        var probe = new FakeClientProbe
        {
            Processes = 2,
            Evidence = new EveClientEvidence(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Jithran", "Lyra Custos" }, new HashSet<int>())
        };
        using var instance = _NewInstance(probe);
        instance.Services.GetRequiredService<EveClientPresenceService>().PollOnce();

        var tool = await _BuildToolAsync(instance);

        Assert.Contains("Jithran", tool.ClientWarning);
        Assert.Contains("Lyra Custos", tool.ClientWarning);
    }

    // ── Copying, with the backup that is not optional ────────────────────────────────────────────

    /// <summary>The whole round trip through the screen: confirm, back up, copy, and say what happened.</summary>
    [AvaloniaFact]
    public async Task Tool_CopiesAfterConfirmation_AndReportsWhereTheBackupWent()
    {
        var profileDirectory = _WriteProfile("settings_Default",
            [(90000001, "alice-layout"), (90000002, "bob-layout")], [(1001, "account one")]);
        using var instance = _NewInstance();
        var dialogs = new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(true) };
        var tool = await _BuildToolAsync(instance, dialogs);

        tool.CharacterSource = tool.Characters.Single(row => row.Id == 90000001);
        tool.Characters.Single(row => row.Id == 90000002).IsTarget = true;

        await tool.SyncCharactersCommand.ExecuteAsync(null);

        // The confirmation named both sides before anything was written.
        Assert.Contains("Character 90000001", dialogs.LastConfirmMessage);
        Assert.Contains("Character 90000002", dialogs.LastConfirmMessage);

        Assert.Equal("alice-layout", File.ReadAllText(Path.Combine(profileDirectory, "core_char_90000002.dat")));
        Assert.False(tool.StatusIsError);
        // The outcome names what the backup covers, both kinds — not just where it went.
        Assert.Contains("Backed up settings_Default (2 characters and 1 account)", tool.Status);
        var backup = Assert.Single(tool.Backups);
        Assert.Equal("before a sync", backup.ReasonDisplay);
        Assert.Equal("ACCOUNTS (1)", backup.AccountHeader);
        Assert.Single(backup.AccountContents);   // the backup covers the whole profile, accounts included
    }

    /// <summary>
    /// A backup covers the whole profile, and every place that summarises one has to say so in both kinds. The
    /// account files used to be listed behind a long character list and simply clipped off the bottom, which left
    /// "was my account data backed up too?" a question you could only answer by reading the code.
    /// </summary>
    [AvaloniaFact]
    public async Task Backup_NamesItsCharactersAndItsAccounts_Separately()
    {
        _WriteProfile("settings_Default",
            [(90000001, "a"), (90000002, "b"), (90000003, "c")], [(1001, "one"), (1002, "two")]);
        using var instance = _NewInstance();
        await instance.Services.GetRequiredService<EveSettingsPreferences>().SaveAccountNameAsync(1001, "Main account");
        var tool = await _BuildToolAsync(instance);

        await tool.BackupNowCommand.ExecuteAsync(null);

        Assert.Contains("3 characters and 2 accounts", tool.Status);

        var backup = Assert.Single(tool.Backups);
        Assert.Equal("3 characters and 2 accounts", backup.Backup.Manifest.ContentsSummary);
        Assert.Contains("3 characters and 2 accounts", backup.ContentsDisplay);
        Assert.Equal("CHARACTERS (3)", backup.CharacterHeader);
        Assert.Equal("ACCOUNTS (2)", backup.AccountHeader);
        Assert.Equal(3, backup.CharacterContents.Count);
        Assert.Equal(2, backup.AccountContents.Count);
        Assert.Contains("Main account · 1001", backup.AccountContents);   // named account, id beside it
        // A character that resolved to no name is still labelled, never a bare number.
        Assert.Contains("Character 90000001", backup.CharacterContents);
        Assert.Contains("Account 1002", backup.AccountContents);
    }

    /// <summary>The id rides along beside the name as a reference, for characters and accounts alike, so the user
    /// can check which file a row actually is.</summary>
    [AvaloniaFact]
    public async Task Rows_CarryTheirIdBesideTheName()
    {
        _WriteProfile("settings_Default", [(90000001, "a")], [(1001, "one")]);
        using var instance = _NewInstance();
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Jithran", 90000001));
        var tool = await _BuildToolAsync(instance);

        var character = Assert.Single(tool.Characters);
        Assert.Equal("Jithran", character.DisplayName);
        Assert.Equal("90000001", character.IdDisplay);

        var account = Assert.Single(tool.Accounts);
        Assert.Equal("1001", account.IdDisplay);
    }

    /// <summary>Cancelling the confirmation writes nothing at all — not even a backup.</summary>
    [AvaloniaFact]
    public async Task Tool_CancellingTheConfirmation_WritesNothing()
    {
        var profileDirectory = _WriteProfile("settings_Default", [(90000001, "alice"), (90000002, "bob")], []);
        using var instance = _NewInstance();
        var dialogs = new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(false) };
        var tool = await _BuildToolAsync(instance, dialogs);

        tool.CharacterSource = tool.Characters.First();
        tool.Characters.Last().IsTarget = true;
        await tool.SyncCharactersCommand.ExecuteAsync(null);

        Assert.Equal("bob", File.ReadAllText(Path.Combine(profileDirectory, "core_char_90000002.dat")));
        Assert.Empty(tool.Backups);
    }

    /// <summary>Restoring puts a backup back over the profile it came from.</summary>
    [AvaloniaFact]
    public async Task Tool_RestoresABackupOverTheProfileItCameFrom()
    {
        var profileDirectory = _WriteProfile("settings_Default", [(90000001, "original")], []);
        using var instance = _NewInstance();
        var dialogs = new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(true) };
        var tool = await _BuildToolAsync(instance, dialogs);

        await tool.BackupNowCommand.ExecuteAsync(null);
        Assert.Single(tool.Backups);

        File.WriteAllText(Path.Combine(profileDirectory, "core_char_90000001.dat"), "ruined");
        tool.SelectedBackup = tool.Backups.Single(row => row.ReasonDisplay == "made by hand");

        await tool.RestoreBackupCommand.ExecuteAsync(null);

        Assert.Equal("original", File.ReadAllText(Path.Combine(profileDirectory, "core_char_90000001.dat")));
        Assert.Contains("Restored 1 files", tool.Status);
    }

    // ── The Tools menu ───────────────────────────────────────────────────────────────────────────

    /// <summary>The rail's TOOLS entry reaches the module — the menu is wired, not decorative.</summary>
    [AvaloniaFact]
    public async Task ToolsMenu_OpensTheSettingsSyncModule()
    {
        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IExternalCharacterEsiSource, OfflineEsiSource>();
            services.AddSingleton<IEveClientProbe>(new FakeClientProbe());
            services.AddSingleton<IDialogService, RecordingDialogService>();
        });

        var shell = new MainWindowViewModel(instance.Services);
        await shell.LaunchModuleCommand.ExecuteAsync("settings-sync");

        var dialogs = (RecordingDialogService)instance.Services.GetRequiredService<IDialogService>();
        Assert.NotNull(dialogs.LastSettingsSync);
    }

    // ── On screen ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The screen itself, in the three states the operator will actually meet it in: nothing installed, one profile,
    /// and several profiles with a handful of characters and accounts. Rendered rather than asserted-about, because
    /// green view-model tests have said nothing about what the operator saw more than once on this project.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("empty", 0, 0, 0)]
    [InlineData("one-profile", 1, 5, 3)]
    [InlineData("many-profiles", 3, 6, 4)]
    public async Task SettingsSyncWindow_Renders(string label, int profiles, int characters, int accounts)
    {
        for (var index = 0; index < profiles; index++)
            _WriteProfile(index == 0 ? "settings_Default" : $"settings_profile{index}",
                Enumerable.Range(0, characters).Select(n => (90000001L + n, $"character-{n}")).ToArray(),
                Enumerable.Range(0, accounts).Select(n => (1001L + n, $"account-{n}")).ToArray());

        using var instance = _NewInstance();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        foreach (var (name, id) in new[] { ("Jithran", 90000001), ("Lyra Custos", 90000002), ("Noahmarr", 90000003) })
            await registry.AddOrUpdateAsync(new Character(name, id));
        if (accounts > 0)
            await instance.Services.GetRequiredService<EveSettingsPreferences>().SaveAccountNameAsync(1001, "Main account");

        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);
        var tool = await _BuildToolAsync(instance);

        var window = new SettingsSyncWindow(tool) { Width = 1180, Height = 760 };
        window.Show();
        await _WaitForAsync(() => false, tries: 12);

        var rendered = window.CaptureRenderedFrame();
        Assert.NotNull(rendered);
        rendered!.Save(Path.Combine(_ShotDirectory(), $"eveutils-settings-sync-{label}.png"),
            new PngBitmapEncoderOptions());

        // The two blocks are both on screen and labelled apart — the mistake this screen must not allow.
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Contains("CHARACTER SETTINGS", texts);
        Assert.Contains("ACCOUNT SETTINGS", texts);
        Assert.Contains("BACKUPS", texts);

        // Names lead, ids ride along beside them — both actually rendered, not merely bound.
        if (profiles > 0)
        {
            Assert.Contains("Jithran", texts);
            Assert.Contains("90000001", texts);
            Assert.Contains("1001", texts);
        }
        window.Close();
    }

    /// <summary>
    /// The backups panel with a backup actually in it: both groups named and counted, the account files listed as
    /// plainly as the character ones. This is the view that made "was my account data backed up too?" a question.
    /// </summary>
    [AvaloniaFact]
    public async Task BackupsPanel_ShowsCharactersAndAccounts_Renders()
    {
        _WriteProfile("settings_Default",
            [(90000001, "a"), (90000002, "b"), (90000003, "c"), (90000004, "d")], [(1001, "one"), (1002, "two")]);
        using var instance = _NewInstance();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        foreach (var (name, id) in new[] { ("Jithran", 90000001), ("Lyra Custos", 90000002) })
            await registry.AddOrUpdateAsync(new Character(name, id));
        await instance.Services.GetRequiredService<EveSettingsPreferences>().SaveAccountNameAsync(1001, "Main account");
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        var tool = await _BuildToolAsync(instance);
        await tool.BackupNowCommand.ExecuteAsync(null);

        var window = new SettingsSyncWindow(tool) { Width = 1180, Height = 760 };
        window.Show();
        await _WaitForAsync(() => false, tries: 12);

        window.CaptureRenderedFrame()!.Save(
            Path.Combine(_ShotDirectory(), "eveutils-settings-sync-backups.png"), new PngBitmapEncoderOptions());

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Contains("CHARACTERS (4)", texts);
        Assert.Contains("ACCOUNTS (2)", texts);
        Assert.Contains("Main account · 1001", texts);   // the accounts are on screen, not clipped below the fold
        Assert.Contains("Account 1002", texts);
        window.Close();
    }

    /// <summary>
    /// Both shells, through the real module host: docked the tool is a tab in the main window, floating it is its
    /// own window, and the open module survives the switch.
    /// </summary>
    [AvaloniaFact]
    public async Task SettingsSyncModule_RendersDockedAndFloating()
    {
        _WriteProfile("settings_Default", [(90000001, "alice"), (90000002, "bob")], [(1001, "one")]);
        using var instance = _NewInstance();
        await instance.Services.GetRequiredService<ISettingRepository>()
            .UpsertAsync("eve-settings.install-root", InstallRoot);
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        var shell = new MainWindowViewModel(instance.Services);
        var window = new MainWindow { DataContext = shell, Width = 1100, Height = 720 };
        var dialogs = (DialogService)instance.Services.GetRequiredService<IDialogService>();
        dialogs.SetOwner(window);
        dialogs.SetHost(shell);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        await shell.LaunchModuleCommand.ExecuteAsync("settings-sync");
        Assert.True(await _WaitForAsync(() => shell.HostTabs.Count == 1));

        Assert.Equal("EVE SETTINGS SYNC", shell.HostTabs[0].Title);
        Assert.Equal("tools", shell.HostTabs[0].ModuleKey);
        Assert.True(shell.IsToolsActive);
        Assert.IsType<SettingsSyncViewModel>(shell.SelectedHostTab!.Content.DataContext);

        // The tool re-lays out for the narrower docked host (ApplyWidth); let that second pass run before capturing.
        await _WaitForAsync(() => false, tries: 12);
        window.CaptureRenderedFrame()!.Save(
            Path.Combine(_ShotDirectory(), "eveutils-settings-sync-docked.png"), new PngBitmapEncoderOptions());

        shell.ToggleDockModeCommand.Execute(null);   // → floating: the tool becomes its own window, not an orphan
        Dispatcher.UIThread.RunJobs();
        Assert.True(shell.IsHomeShown);

        shell.ToggleDockModeCommand.Execute(null);   // → docked again: the same module comes back as a tab
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("EVE SETTINGS SYNC", shell.HostTabs[0].Title);
        window.Close();
    }

    private static string _ShotDirectory()
    {
        var directory = Environment.GetEnvironmentVariable("EVEUTILS_SHOT_DIR");
        return string.IsNullOrWhiteSpace(directory) ? Path.GetTempPath() : directory;
    }

    private static async Task<bool> _WaitForAsync(Func<bool> condition, int tries = 150)
    {
        for (var attempt = 0; attempt < tries; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }
}
