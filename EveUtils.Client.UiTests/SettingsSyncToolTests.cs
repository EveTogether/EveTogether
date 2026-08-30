using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
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
            instance.Services.GetRequiredService<SettingsPresetService>(),
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
        var backup = Assert.Single(tool.RecentBackups);
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

        var backup = Assert.Single(tool.RecentBackups);
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
        Assert.Empty(tool.RecentBackups);
    }

    /// <summary>
    /// Restoring puts a backup back over the profile it came from. It lives in the backups window now (ET-67), and
    /// the tool that opened it re-reads the profile afterwards rather than leaving write times standing that the
    /// restore has just made untrue.
    /// </summary>
    [AvaloniaFact]
    public async Task BackupsWindow_RestoresABackup_AndTheToolBehindItRe_Reads()
    {
        var profileDirectory = _WriteProfile("settings_Default", [(90000001, "original")], []);
        using var instance = _NewInstance();
        var dialogs = new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(true) };
        var tool = await _BuildToolAsync(instance, dialogs);

        await tool.BackupNowCommand.ExecuteAsync(null);
        Assert.Single(tool.RecentBackups);
        Assert.Contains("Last backup", tool.LastBackupDisplay);

        tool.OpenBackupsCommand.Execute(null);
        var backups = dialogs.LastSettingsBackups;
        Assert.NotNull(backups);

        File.WriteAllText(Path.Combine(profileDirectory, "core_char_90000001.dat"), "ruined");
        backups!.SelectedBackup = backups.Backups.Single(row => row.ReasonDisplay == "made by hand");

        await backups.RestoreBackupCommand.ExecuteAsync(null);

        Assert.Equal("original", File.ReadAllText(Path.Combine(profileDirectory, "core_char_90000001.dat")));
        Assert.Contains("Restored 1 files", backups.Status);
        Assert.True(await _WaitForAsync(() => tool.Status.Contains("re-read")));
    }

    /// <summary>
    /// What the sync screen keeps of the backups: that they are being taken, when the last one was, and the two
    /// doors out. Everything you can actually do with a backup moved to its own window, because in a column beside
    /// two file lists a single backup was never readable in one go.
    /// </summary>
    [AvaloniaFact]
    public async Task Tool_KeepsOnlyTheLastTwoBackups_AndADoorToTheRest()
    {
        _WriteProfile("settings_Default", [(90000001, "a"), (90000002, "b")], [(1001, "one")]);
        using var instance = _NewInstance();
        var dialogs = new RecordingDialogService();
        var tool = await _BuildToolAsync(instance, dialogs);

        Assert.Empty(tool.RecentBackups);
        Assert.Contains("No backups yet", tool.LastBackupDisplay);

        for (var index = 0; index < 4; index++)
            await tool.BackupNowCommand.ExecuteAsync(null);

        Assert.Equal(SettingsSyncViewModel.RecentBackupCount, tool.RecentBackups.Count);
        Assert.Equal(4, tool.BackupCount);
        Assert.Contains("Last backup", tool.LastBackupDisplay);
        Assert.Contains("2 characters and 1 account", tool.LastBackupDisplay);
        Assert.Contains("4 kept", tool.LastBackupDisplay);   // the ones not shown are still accounted for

        tool.OpenBackupsCommand.Execute(null);
        var backups = dialogs.LastSettingsBackups;
        Assert.NotNull(backups);
        Assert.Equal(4, backups!.Backups.Count);   // all of them, in the window that has room
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
    /// The backups window with a backup in it: the list beside the whole of what is inside the selected one, both
    /// groups named and counted, the account files listed as plainly as the character ones. This is the view that
    /// made "was my account data backed up too?" a question, and then made the sync screen unusable — it now has a
    /// window of its own, and it is rendered docked as well, which is where the space ran out.
    /// </summary>
    [AvaloniaFact]
    public async Task BackupsWindow_ShowsCharactersAndAccounts_Renders()
    {
        _WriteProfile("settings_Default",
            [(90000001, "a"), (90000002, "b"), (90000003, "c"), (90000004, "d")], [(1001, "one"), (1002, "two")]);
        using var instance = _NewInstance();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        foreach (var (name, id) in new[] { ("Jithran", 90000001), ("Lyra Custos", 90000002) })
            await registry.AddOrUpdateAsync(new Character(name, id));
        await instance.Services.GetRequiredService<EveSettingsPreferences>().SaveAccountNameAsync(1001, "Main account");
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        var dialogs = new RecordingDialogService();
        var tool = await _BuildToolAsync(instance, dialogs);
        await tool.BackupNowCommand.ExecuteAsync(null);
        tool.OpenBackupsCommand.Execute(null);

        var window = new SettingsBackupsWindow(dialogs.LastSettingsBackups!) { Width = 900, Height = 620 };
        window.Show();
        await _WaitForAsync(() => false, tries: 12);

        window.CaptureRenderedFrame()!.Save(
            Path.Combine(_ShotDirectory(), "eveutils-settings-backups.png"), new PngBitmapEncoderOptions());

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Contains("CHARACTERS (4)", texts);
        Assert.Contains("ACCOUNTS (2)", texts);
        Assert.Contains("Main account · 1001", texts);   // the accounts are on screen, not clipped below the fold
        Assert.Contains("Account 1002", texts);
        Assert.Contains(texts, text => text is not null && text.Contains("Restoring writes these 6 files back"));
        window.Close();
    }

    /// <summary>Docked, in the host where the old in-tool panel showed a fraction of one backup: the list, the
    /// contents of the selected one and the restore button all still fit.</summary>
    [AvaloniaFact]
    public async Task BackupsModule_RendersDocked()
    {
        _WriteProfile("settings_Default",
            [(90000001, "a"), (90000002, "b"), (90000003, "c"), (90000004, "d")], [(1001, "one"), (1002, "two")]);
        using var instance = _NewInstance();
        await instance.Services.GetRequiredService<ISettingRepository>()
            .UpsertAsync("eve-settings.install-root", InstallRoot);
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Jithran", 90000001));
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
        var tool = (SettingsSyncViewModel)shell.SelectedHostTab!.Content.DataContext!;
        await tool.BackupNowCommand.ExecuteAsync(null);

        tool.OpenBackupsCommand.Execute(null);
        Assert.True(await _WaitForAsync(() => shell.HostTabs.Count == 2));
        Assert.Equal("SETTINGS BACKUPS", shell.HostTabs[1].Title);
        await _WaitForAsync(() => false, tries: 12);

        window.CaptureRenderedFrame()!.Save(
            Path.Combine(_ShotDirectory(), "eveutils-settings-backups-docked.png"), new PngBitmapEncoderOptions());

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Contains("CHARACTERS (4)", texts);
        Assert.Contains("ACCOUNTS (2)", texts);
        Assert.Contains("Jithran · 90000001", texts);
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

    /// <summary>
    /// Typing a name and pressing Enter is the whole gesture — reaching for the mouse to finish it is a bug. Escape
    /// is its counterpart: the prompt goes away and the account keeps the name it had.
    /// </summary>
    [AvaloniaFact]
    public async Task NamingAnAccount_IsConfirmedWithEnter_AndCancelledWithEscape()
    {
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();

        Assert.Equal("Alt account", await _PromptResultAsync(owner, "Alt account", Key.Enter, PhysicalKey.Enter));
        // Escape returns nothing at all, so the caller never writes a name and the account keeps the one it had.
        Assert.Null(await _PromptResultAsync(owner, "Alt account", Key.Escape, PhysicalKey.Escape));

        owner.Close();
    }

    private static async Task<string?> _PromptResultAsync(Window owner, string value, Key key, PhysicalKey physical)
    {
        var prompt = new TextPromptWindow("Name this account", "EVE gives accounts no name.", value);
        var result = prompt.ShowDialog<string?>(owner);
        await _WaitForAsync(() => false, tries: 8);

        prompt.KeyPress(key, RawInputModifiers.None, physical, keySymbol: null);
        await _WaitForAsync(() => result.IsCompleted);
        return result.IsCompleted ? await result : "the prompt never closed";
    }

    // ── Which characters are on which account (ET-64) ────────────────────────────────────────────

    /// <summary>
    /// The operator's actual case: the profile he is looking at was written by six clients closing at once and says
    /// nothing, while another profile holds a clean one-by-one login. The tool reads both, so the account rows in
    /// front of him carry their characters.
    /// </summary>
    [AvaloniaFact]
    public async Task AccountRows_ShowTheirCharacters_ReadFromEveryProfile()
    {
        // The profile on screen: everything stamped the same second — nothing to conclude from it alone.
        var multibox = _WriteProfile("settings_Default", [(90250177, "a"), (2123169375, "b")], [(7417348, "x"), (31203498, "y")]);
        var together = new DateTime(2026, 8, 29, 23, 34, 0, DateTimeKind.Utc);
        foreach (var path in Directory.GetFiles(multibox))
            File.SetLastWriteTimeUtc(path, together);

        // Another profile: logged in one after another, seconds apart.
        var minimal = _WriteProfile("settings_minimal", [(90250177, "a"), (2123169375, "b")], [(7417348, "x"), (31203498, "y")]);
        var start = new DateTime(2025, 11, 18, 19, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(minimal, "core_user_7417348.dat"), start);
        File.SetLastWriteTimeUtc(Path.Combine(minimal, "core_char_90250177.dat"), start);
        File.SetLastWriteTimeUtc(Path.Combine(minimal, "core_user_31203498.dat"), start.AddSeconds(5));
        File.SetLastWriteTimeUtc(Path.Combine(minimal, "core_char_2123169375.dat"), start.AddSeconds(5));

        using var instance = _NewInstance();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character("Jithran", 90250177));
        await registry.AddOrUpdateAsync(new Character("Lyra Custos", 2123169375));

        var tool = await _BuildToolAsync(instance);

        var first = tool.Accounts.Single(row => row.Id == 7417348);
        Assert.Equal(["Jithran"], first.AccountCharacters);
        Assert.Equal(AccountLinkOrigin.Derived, first.LinkOrigin);
        Assert.Contains("Jithran", first.HintDisplay);
        Assert.Contains("write times", first.HintDisplay);   // said to be an inference, not a fact
        Assert.Equal(["Lyra Custos"], tool.Accounts.Single(row => row.Id == 31203498).AccountCharacters);

        // And it is remembered, so a later evening of multiboxing cannot take it away again.
        var stored = await instance.Services.GetRequiredService<EveSettingsPreferences>().LoadAccountLinksAsync();
        Assert.Equal([90250177L], stored[7417348].CharacterIds);
    }

    /// <summary>What the user says outranks what was worked out, and it says so on the row.</summary>
    [AvaloniaFact]
    public async Task AccountRow_TakesTheCharactersTheUserNames_AndKeepsThem()
    {
        var profileDirectory = _WriteProfile("settings_Default", [(90000001, "a"), (90000002, "b")], [(1001, "one")]);
        // The account was last written a day apart from either character: no session ties them, so nothing can be
        // worked out and the row has to say so rather than guess.
        File.SetLastWriteTimeUtc(Path.Combine(profileDirectory, "core_user_1001.dat"),
            DateTime.UtcNow.AddDays(-1));

        using var instance = _NewInstance();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character("Jithran", 90000001));
        await registry.AddOrUpdateAsync(new Character("Lyra Custos", 90000002));

        var dialogs = new RecordingDialogService
        {
            OnPickCharacters = (_, _) => Task.FromResult<IReadOnlyList<int>?>([90000001, 90000002])
        };
        var tool = await _BuildToolAsync(instance, dialogs);

        var account = Assert.Single(tool.Accounts);
        Assert.True(account.NeedsLink);   // nothing could be established, and the row says so rather than sitting blank

        await tool.LinkAccountCharactersCommand.ExecuteAsync(account);

        Assert.Equal(["Jithran", "Lyra Custos"], account.AccountCharacters);
        Assert.Equal(AccountLinkOrigin.UserSet, account.LinkOrigin);
        Assert.Contains("set by you", account.HintDisplay);
        Assert.Contains("1001", dialogs.LastPrompt);   // asked about this account by its id, not in the abstract
        Assert.Equal(2, dialogs.LastOptions!.Count);   // every character it knows of is offered

        var stored = await instance.Services.GetRequiredService<EveSettingsPreferences>().LoadAccountLinksAsync();
        Assert.Equal(AccountLinkOrigin.UserSet, stored[1001].Origin);
        Assert.Equal([90000001L, 90000002L], stored[1001].CharacterIds);
    }

    // ── Keeping it in step by itself (ET-60) ─────────────────────────────────────────────────────

    /// <summary>
    /// The automatic rule is the selection on screen, remembered — there is no second, invisible place to configure
    /// it. And it cannot be switched on before there is something to run.
    /// </summary>
    [AvaloniaFact]
    public async Task AutoSync_RemembersWhatTheBlocksAreShowing_AndRefusesToRunOnNothing()
    {
        _WriteProfile("settings_Default", [(90000001, "a"), (90000002, "b")], [(1001, "one"), (1002, "two")]);
        using var instance = _NewInstance();
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Jithran", 90000001));
        var tool = await _BuildToolAsync(instance);

        Assert.False(tool.AutoSyncEnabled);
        Assert.False(tool.HasAutoSyncRule);
        Assert.False(tool.CanRememberAutoSync);   // nothing selected yet

        tool.AutoSyncEnabled = true;              // switching it on with nothing to run changes nothing
        Assert.False(tool.AutoSyncEnabled);
        Assert.True(tool.StatusIsError);

        tool.CharacterSource = tool.Characters.Single(row => row.Id == 90000001);
        tool.Characters.Single(row => row.Id == 90000002).IsTarget = true;
        tool.AccountSource = tool.Accounts.Single(row => row.Id == 1001);
        tool.Accounts.Single(row => row.Id == 1002).IsTarget = true;
        Assert.True(tool.CanRememberAutoSync);

        await tool.RememberAutoSyncCommand.ExecuteAsync(null);

        Assert.True(tool.AutoSyncEnabled);
        Assert.True(tool.HasAutoSyncRule);
        Assert.Contains("Jithran →", tool.AutoSyncRuleSummary);
        Assert.Contains("settings_Default", tool.AutoSyncRuleSummary);

        var saved = await instance.Services.GetRequiredService<EveSettingsPreferences>().LoadAutoSyncAsync();
        Assert.True(saved.Enabled);
        Assert.Equal(90000001, saved.CharacterSourceId);
        Assert.Equal([90000002L], saved.CharacterTargetIds);
        Assert.Equal(1001, saved.AccountSourceId);
        Assert.Equal([1002L], saved.AccountTargetIds);
        Assert.Equal("settings_Default", saved.ProfileName);

        // Off is off, and forgotten means forgotten.
        await tool.ForgetAutoSyncCommand.ExecuteAsync(null);
        Assert.False(tool.AutoSyncEnabled);
        Assert.False((await instance.Services.GetRequiredService<EveSettingsPreferences>().LoadAutoSyncAsync()).IsConfigured);
    }

    /// <summary>A run that happened is findable in the tool afterwards: when it was, and what it overwrote.</summary>
    [AvaloniaFact]
    public async Task AutoSync_ShowsWhatItDid_WhenTheToolIsOpenedAgain()
    {
        _WriteProfile("settings_Default", [(90000001, "a"), (90000002, "b")], []);
        using var instance = _NewInstance();
        var preferences = instance.Services.GetRequiredService<EveSettingsPreferences>();
        await preferences.SaveAutoSyncAsync(new AutoSyncSettings
        {
            Enabled = true,
            InstallRoot = InstallRoot,
            ProfileName = "settings_Default",
            CharacterSourceId = 90000001,
            CharacterTargetIds = [90000002L]
        });
        await preferences.AppendAutoSyncRunAsync(new AutoSyncRun(
            new DateTimeOffset(2026, 8, 30, 1, 12, 0, TimeSpan.Zero),
            "Copied Jithran → Lyra Custos.", "20260830-011200-settings_Default", false));

        var tool = await _BuildToolAsync(instance);

        Assert.True(tool.AutoSyncEnabled);
        Assert.Contains("Copied Jithran → Lyra Custos.", tool.AutoSyncLastRun);
        Assert.Contains("2026-08-30", tool.AutoSyncLastRun);
        Assert.Contains("Copied Jithran", tool.AutoSyncHistory);
    }

    // ── Carrying settings to another machine (ET-61) ─────────────────────────────────────────────

    /// <summary>
    /// The whole journey the operator described, without a screen: on this machine pick one account and one
    /// character and save them as "default"; on the other machine — a fresh EVE install with an empty profile —
    /// read it in and see both land as new files, with the profile snapshotted first.
    /// </summary>
    [AvaloniaFact]
    public async Task Preset_SavedFromOneMachine_ReadsInOnAFreshOne()
    {
        _WriteProfile("settings_Default",
            [(90250177, "jithran-layout"), (90382598, "someone-else")], [(7417348, "jithran-account")]);
        using var instance = _NewInstance();
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Jithran", 90250177));
        await instance.Services.GetRequiredService<EveSettingsPreferences>().SaveAccountNameAsync(7417348, "Main account");

        var presetPath = Path.Combine(_root, "carried", "default.etpreset");
        var dialogs = new RecordingDialogService
        {
            OnShowPresetExport = async export =>
            {
                export.PresetName = "default";
                foreach (var row in export.Characters.Where(row => row.Id == 90250177).Concat(export.Accounts))
                    row.IsIncluded = true;
                Assert.True(export.CanExport);
                Assert.Contains("1 character and 1 account", export.Summary);
                await export.ExportToAsync(presetPath);
            }
        };

        var tool = await _BuildToolAsync(instance, dialogs);
        await tool.ExportPresetCommand.ExecuteAsync(null);

        Assert.NotNull(dialogs.LastPresetExport);
        Assert.True(dialogs.LastPresetExport!.Saved);
        Assert.True(File.Exists(presetPath));

        // The other machine: EVE has made the folder and nothing else yet.
        var fresh = Path.Combine(_root, "other-pc", "settings_Default");
        Directory.CreateDirectory(fresh);
        var presets = instance.Services.GetRequiredService<SettingsPresetService>();
        var importer = new PresetImportViewModel(presets, EveSettingsLocator.LoadProfile(fresh),
            Path.Combine(_root, "other-pc"), EveSettingsNames.Empty);

        await importer.LoadAsync(presetPath);

        Assert.True(importer.HasPreset);
        Assert.Contains("\"default\"", importer.PresetHeader);
        Assert.Contains("EVE Together", importer.PresetOrigin);      // date and build it was made with
        Assert.Equal(2, importer.Rows.Count);
        Assert.All(importer.Rows, row => Assert.True(row.IsNew));    // nothing here to overwrite yet
        Assert.Contains("0 overwritten, 2 new, 0 skipped", importer.PlanSummary);
        Assert.True(importer.CanApply);

        await importer.ApplyCommand.ExecuteAsync(null);

        Assert.True(importer.Applied);
        Assert.False(importer.StatusIsError);
        Assert.Equal("jithran-layout", File.ReadAllText(Path.Combine(fresh, "core_char_90250177.dat")));
        Assert.Equal("jithran-account", File.ReadAllText(Path.Combine(fresh, "core_user_7417348.dat")));
        Assert.False(File.Exists(Path.Combine(fresh, "core_char_90382598.dat")));   // not ticked, not carried
    }

    /// <summary>A line can be pointed elsewhere or skipped before anything is written, and the preview says which
    /// it is per row.</summary>
    [AvaloniaFact]
    public async Task PresetImport_LetsEveryLineBePointedSomewhereElse()
    {
        _WriteProfile("settings_Default", [(90250177, "jithran-layout")], []);
        using var instance = _NewInstance();
        var tool = await _BuildToolAsync(instance);
        var presets = instance.Services.GetRequiredService<SettingsPresetService>();

        var presetPath = Path.Combine(_root, "carried", "default.etpreset");
        var source = EveSettingsLocator.LoadProfile(Path.Combine(InstallRoot, "settings_Default"));
        Assert.True(presets.Export(presetPath, "default", PresetScope.Selection, source, source.Characters,
            new Dictionary<long, string> { [90250177] = "Jithran" }).IsSuccess);

        // A machine with a different pilot in it: the id does not match, so the preset would land as a new file —
        // unless the user says otherwise.
        var other = Path.Combine(_root, "other-pc", "settings_Default");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "core_char_2123169375.dat"), "lyra-layout");

        var importer = new PresetImportViewModel(presets, EveSettingsLocator.LoadProfile(other),
            Path.Combine(_root, "other-pc"), EveSettingsNames.Empty);
        await importer.LoadAsync(presetPath);

        var row = Assert.Single(importer.Rows);
        Assert.True(row.IsNew);
        Assert.Equal(3, row.Options.Count);   // skip, new file, or onto the pilot that is here

        row.SelectedOption = row.Options.Single(option => option.TargetFileName == "core_char_2123169375.dat");
        Assert.Equal("OVERWRITES", row.ActionDisplay);

        await importer.ApplyCommand.ExecuteAsync(null);

        // The target kept its own name and id; only the bytes moved.
        Assert.Equal("jithran-layout", File.ReadAllText(Path.Combine(other, "core_char_2123169375.dat")));
        Assert.False(File.Exists(Path.Combine(other, "core_char_90250177.dat")));
    }

    // ── On screen ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tool with everything ET-60/61/64 added to it, in the two shells that matter — its own window and the
    /// docked tab at 1100x720, which is where this screen has always been tightest. Rendered and looked at, because
    /// green view-model tests have said nothing about what the operator saw more than once on this project.
    /// </summary>
    [AvaloniaFact]
    public async Task SettingsSyncWindow_WithAutoSyncAndPresets_Renders()
    {
        _WriteProfile("settings_Default",
            [(90250177, "a"), (2123169375, "b"), (2122696898, "c")], [(7417348, "x"), (31203498, "y")]);
        using var instance = _NewInstance();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        foreach (var (name, id) in new[] { ("Jithran", 90250177), ("Lyra Custos", 2123169375), ("Noahmarr", 2122696898) })
            await registry.AddOrUpdateAsync(new Character(name, id));
        await instance.Services.GetRequiredService<EveSettingsPreferences>().SaveAccountNameAsync(7417348, "Main account");
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        var tool = await _BuildToolAsync(instance);
        tool.CharacterSource = tool.Characters.Single(row => row.Id == 90250177);
        tool.Characters.Single(row => row.Id == 2123169375).IsTarget = true;
        await tool.RememberAutoSyncCommand.ExecuteAsync(null);

        var window = new SettingsSyncWindow(tool) { Width = 1180, Height = 760 };
        window.Show();
        await _WaitForAsync(() => false, tries: 12);

        window.CaptureRenderedFrame()!.Save(
            Path.Combine(_ShotDirectory(), "eveutils-settings-sync-autosync.png"), new PngBitmapEncoderOptions());

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Contains("PRESETS", texts);
        Assert.Contains(texts, text => text is not null && text.Contains("Jithran →"));     // the remembered rule
        Assert.Contains(texts, text => text is not null && text.Contains("It has not run yet"));
        window.Close();
    }

    /// <summary>Docked, where the height has always been the problem: the automatic strip and the presets panel are
    /// both on screen, and the two file lists still show rows.</summary>
    [AvaloniaFact]
    public async Task SettingsSyncModule_Docked_StillShowsEveryPanel()
    {
        _WriteProfile("settings_Default",
            [(90250177, "a"), (2123169375, "b")], [(7417348, "x"), (31203498, "y")]);
        using var instance = _NewInstance();
        await instance.Services.GetRequiredService<ISettingRepository>()
            .UpsertAsync("eve-settings.install-root", InstallRoot);
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Jithran", 90250177));
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
        await _WaitForAsync(() => false, tries: 12);

        window.CaptureRenderedFrame()!.Save(
            Path.Combine(_ShotDirectory(), "eveutils-settings-sync-docked-autosync.png"), new PngBitmapEncoderOptions());

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Contains("CHARACTER SETTINGS", texts);
        Assert.Contains("ACCOUNT SETTINGS", texts);
        Assert.Contains("BACKUPS", texts);
        Assert.Contains("PRESETS", texts);
        Assert.Contains(texts, text => text is not null && text.Contains("Nothing set yet"));
        // The file rows are still there — the new panels did not push the lists off the screen.
        Assert.Contains("Jithran", texts);
        Assert.Contains("Unnamed account", texts);
        window.Close();
    }

    /// <summary>The two preset dialogs, rendered: the one that decides what travels, and the one that decides what
    /// happens when it arrives.</summary>
    [AvaloniaFact]
    public async Task PresetWindows_Render()
    {
        _WriteProfile("settings_Default", [(90250177, "a"), (2123169375, "b")], [(7417348, "x")]);
        using var instance = _NewInstance();
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Jithran", 90250177));
        await instance.Services.GetRequiredService<EveSettingsPreferences>().SaveAccountNameAsync(7417348, "Main account");
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        var presets = instance.Services.GetRequiredService<SettingsPresetService>();
        var profile = EveSettingsLocator.LoadProfile(Path.Combine(InstallRoot, "settings_Default"));
        var names = await instance.Services.GetRequiredService<EveSettingsNameResolver>().ResolveAsync(profile);

        var export = new PresetExportViewModel(presets, profile, InstallRoot, names);
        export.Characters.Single(row => row.Id == 90250177).IsIncluded = true;   // the named one, Jithran
        export.Accounts[0].IsIncluded = true;

        var exportWindow = new PresetExportWindow(export) { Width = 620, Height = 620 };
        exportWindow.Show();
        await _WaitForAsync(() => false, tries: 12);
        exportWindow.CaptureRenderedFrame()!.Save(
            Path.Combine(_ShotDirectory(), "eveutils-preset-export.png"), new PngBitmapEncoderOptions());
        var exportTexts = exportWindow.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Contains(exportTexts, text => text is not null && text.Contains("no login tokens", StringComparison.OrdinalIgnoreCase));
        exportWindow.Close();

        var presetPath = Path.Combine(_root, "carried", "default.etpreset");
        Assert.True(await export.ExportToAsync(presetPath));

        var other = Path.Combine(_root, "other-pc", "settings_Default");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "core_char_90250177.dat"), "already-here");

        var import = new PresetImportViewModel(presets, EveSettingsLocator.LoadProfile(other),
            Path.Combine(_root, "other-pc"), names);
        await import.LoadAsync(presetPath);

        var importWindow = new PresetImportWindow(import) { Width = 720, Height = 620 };
        importWindow.Show();
        await _WaitForAsync(() => false, tries: 12);
        importWindow.CaptureRenderedFrame()!.Save(
            Path.Combine(_ShotDirectory(), "eveutils-preset-import.png"), new PngBitmapEncoderOptions());

        var importTexts = importWindow.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Contains("OVERWRITES", importTexts);   // the character already here
        Assert.Contains("NEW FILE", importTexts);     // the account that is not
        importWindow.Close();
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
