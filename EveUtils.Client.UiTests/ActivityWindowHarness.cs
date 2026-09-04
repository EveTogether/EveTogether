using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Gamelog;
using EveUtils.Client.Platform;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.UiTests;

/// <summary>
/// A real client, a real gamelog directory and one registered character — the shortest arrangement in which the
/// activity window can be driven the way the application drives it. Nothing here reaches into the window: a test
/// writes a gamelog line or publishes a fleet sample, and then reads what the window shows.
/// </summary>
internal sealed class ActivityWindowHarness : IDisposable
{
    public const int CharacterId = 90000001;
    public const string CharacterName = "Test Pilot";

    private ActivityWindowHarness(TestClientInstance instance, string gamelogDirectory)
    {
        Instance = instance;
        GamelogDirectory = gamelogDirectory;
    }

    public TestClientInstance Instance { get; }

    public string GamelogDirectory { get; }

    public IServiceProvider Services => Instance.Services;

    public RecordingDialogService Dialogs => (RecordingDialogService)Services.GetRequiredService<IDialogService>();

    /// <summary>
    /// The client, with the one character this pilot flies already known to the registry.
    /// <paramref name="inGame"/> stands in for the one thing no headless test can have: a running EVE client. The
    /// real presence check reads the game's own window title, so without this every location would honestly read
    /// "offline" — see <see cref="StubPresence"/>.
    /// </summary>
    public static async Task<ActivityWindowHarness> CreateAsync(bool inGame = true,
        Action<IServiceCollection>? configure = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "eveutils-activity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        TestClientInstance instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor().Add(17155, "Centii Servant", 135, 11));
            services.AddSingleton<IDialogService, RecordingDialogService>();
            services.AddSingleton<ILocalCharacterPresence>(new StubPresence(inGame));
            configure?.Invoke(services);
        });

        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character(CharacterName, CharacterId));
        await instance.Services.GetRequiredService<IDispatcher>()
            .Send(new SetSettingCommand(GamelogWatcherService.GamelogDirectorySettingKey, directory));

        return new ActivityWindowHarness(instance, directory);
    }

    /// <summary>A window as the application opens one: constructed, then loaded.</summary>
    public async Task<ActivityWindowViewModel> OpenAsync(ActivityKind kind = ActivityKind.Site)
    {
        var model = new ActivityWindowViewModel(kind, Services);
        await model.LoadAsync();
        return model;
    }

    /// <summary>Start tailing the real gamelog directory, exactly as the shell does at start-up.</summary>
    public async Task<GamelogWatcherService> StartWatchingAsync()
    {
        var watcher = Services.GetRequiredService<GamelogWatcherService>();
        await watcher.StartAsync();
        // The watcher baselines a file at its current length, so the header has to be on disk and seen before the
        // lines a test cares about are appended — otherwise they arrive as history and are (correctly) ignored.
        await File.WriteAllTextAsync(LogPath, Header);
        await Task.Delay(150);
        return watcher;
    }

    public Task WriteLineAsync(string line) => File.AppendAllTextAsync(LogPath, line + "\n");

    /// <summary>A bounty payout line as EVE writes it, in the Dutch client's grouping (ET-41).</summary>
    public static string BountyLine(string isk) =>
        "[ 2030.01.01 12:00:07 ] (bounty) <font size=12><b><color=0xff00aa00>" + isk
        + " ISK</b><color=0x77ffffff> added to next bounty payout";

    /// <summary>One hit, as the combat log writes it.</summary>
    public static string CombatLine(int amount, string target, string atTime = "12:00:05") =>
        $"[ 2030.01.01 {atTime} ] (combat) {amount} to {target} - Light Ion Blaster II - Hits";

    /// <summary>The same fight the other way round — the rat shooting back, which EVE writes as "from".</summary>
    public static string IncomingCombatLine(int amount, string target, string atTime = "12:00:09") =>
        $"[ 2030.01.01 {atTime} ] (combat) {amount} from {target} - Light Ion Blaster II - Hits";

    public static string JumpLine(string system) =>
        $"[ 2030.01.01 12:00:03 ] (None) Jumping from Osmon to {system}";

    /// <summary>Poll until the window itself says what is expected — the wait is for the file watcher's next tick,
    /// never for the assertion.</summary>
    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (condition())
                return;

            await Task.Delay(25);
        }

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private string LogPath => Path.Combine(GamelogDirectory, "20300101_120000_90000001.txt");

    private static string Header =>
        "------------------------------------------------------------\n"
        + $"  Gamelog\n  Listener: {CharacterName}\n  Session Started: 2030.01.01 12:00:00\n"
        + "------------------------------------------------------------\n";

    /// <summary>Whether the pilot is at the keyboard — the one fact a headless run cannot observe for itself.</summary>
    /// <param name="inGameIds">Which characters are at the keyboard; empty means "all of them", the old blanket
    /// answer. Naming them is what lets a test have three registered pilots and one client open.</param>
    internal sealed class StubPresence(bool inGame, params int[] inGameIds) : ILocalCharacterPresence
    {
        public bool? IsInGame(int characterId, string? characterName) =>
            inGameIds.Length == 0 ? inGame : inGame && Array.IndexOf(inGameIds, characterId) >= 0;

        public bool? IsInGame(int characterId) => IsInGame(characterId, null);
        public IDisposable Subscribe(Action handler) => new Unsubscribed();

        private sealed class Unsubscribed : IDisposable
        {
            public void Dispose() { }
        }
    }

    public void Dispose()
    {
        Instance.Dispose();
        try
        {
            if (Directory.Exists(GamelogDirectory))
                Directory.Delete(GamelogDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch gamelog directory is harmless.
        }
    }
}
