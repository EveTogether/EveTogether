using System.Diagnostics;
using System.Text;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Reading;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-278: the client language is read from the gamelog header, and a language this build cannot read is reported
/// instead of silently producing nothing. The non-English headers are SYNTHETIC — CCP's own header words for each
/// language laid out like the English header; none was seen in a real file.
/// </summary>
public sealed class GamelogLanguageDetectionTests : IDisposable
{
    private const string CharacterName = "Test Pilot";
    private const string Rule = "------------------------------------------------------------\n";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "eveutils-gamelog-language-" + Guid.NewGuid().ToString("N"));

    public GamelogLanguageDetectionTests() => Directory.CreateDirectory(_directory);

    private string LogPath() => Path.Combine(_directory, "20300101_120000_95123456.txt");

    private static string Header(string listener, string sessionStarted, string separator = ": ") =>
        Rule + $"  Gamelog\n  {listener}{separator}{CharacterName}\n  {sessionStarted}{separator}2030.01.01 12:00:00\n" + Rule;

    private static GameLogHeader? Read(string header)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(header));
        return GameLogHeader.TryRead(stream);
    }

    [Theory]
    [InlineData("Listener", "Session Started", ": ", GamelogLanguage.English)]
    [InlineData("Empfänger", "Sitzung gestartet", ": ", GamelogLanguage.German)]
    [InlineData("Слушатель", "Сеанс начат", ": ", GamelogLanguage.Russian)]
    [InlineData("Auditeur", "Session commencée", " : ", GamelogLanguage.French)]
    [InlineData("Oyente", "Sesión iniciada", ": ", GamelogLanguage.Spanish)]
    [InlineData("傍聴者", "セッション開始", "：", GamelogLanguage.Japanese)]
    [InlineData("收听者", "进程开始", "：", GamelogLanguage.Chinese)]
    public void Header_GivesTheLanguage_AndTheSameCharacterAndStart(string listener, string sessionStarted, string separator, GamelogLanguage expected)
    {
        GameLogHeader? header = Read(Header(listener, sessionStarted, separator));

        Assert.NotNull(header);
        Assert.Equal((CharacterName, new DateTime(2030, 1, 1, 12, 0, 0), expected), (header.CharacterName, header.SessionStarted, header.Language));
    }

    [Fact]
    public void Header_InALanguageWithoutTemplates_ReadsAsUnknown()
    {
        GameLogHeader? header = Read(Header("청취자", "세션 시작됨"));

        Assert.NotNull(header);
        Assert.Equal((CharacterName, GamelogLanguage.Unknown), (header.CharacterName, header.Language));
    }

    [Theory]
    [InlineData("Session Started")]
    [InlineData("Sitzung gestartet")]
    [InlineData("세션 시작됨")]
    public void CharacterSelectHeader_WithNoListener_IsNotATrackedLog(string sessionStarted)
    {
        string header = Rule + $"  Gamelog\n  {sessionStarted}: 2030.01.01 12:00:00\n" + Rule;

        Assert.Null(Read(header));
    }

    [Fact]
    public async Task GermanLog_IsReadThroughTheWatcher_IncludingItsLastKnownLocation()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(LogPath(), Header("Empfänger", "Sitzung gestartet")
            + "[ 2030.01.01 12:00:01 ] (None) Springe von Rancer nach Hykkota\n", ct);

        using GameLogWatcher watcher = new(_directory, TimeSpan.FromMilliseconds(20));
        List<GameLogEvent> events = [];
        watcher.EventParsed += (_, args) => { lock (events) events.Add(args.LogEvent); };

        watcher.Start();
        watcher.TrackByCharacterNames([CharacterName]);

        // The last known location is found by a substring test on the German words, not the English ones.
        lock (events)
        {
            Assert.Equal("Hykkota", Assert.IsType<LocationEvent>(Assert.Single(events)).System);
        }

        await File.AppendAllTextAsync(LogPath(),
            "[ 2030.01.01 12:00:05 ] (combat) 250 nach Guristas Destroyer - Light Ion Blaster II - Treffer\n", ct);

        await WaitUntil(() => { lock (events) return events.OfType<CombatEvent>().Any(); }, ct);
        lock (events)
        {
            CombatEvent hit = events.OfType<CombatEvent>().Single();
            Assert.Equal((250, HitQuality.Hits, DamageDirection.Outgoing), (hit.Amount, hit.Quality, hit.Direction));
        }
    }

    [Fact]
    public async Task UnsupportedLog_IsReportedOnce_AndNoneOfItsLinesAreRead()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(LogPath(), Rule, ct);

        using GameLogWatcher watcher = new(_directory, TimeSpan.FromMilliseconds(20));
        List<string> reported = [];
        List<GameLogEvent> events = [];
        watcher.LanguageNotSupported += (_, name) => { lock (reported) reported.Add(name); };
        watcher.EventParsed += (_, args) => { lock (events) events.Add(args.LogEvent); };

        watcher.Start();
        await Task.Delay(100, ct);
        await File.WriteAllTextAsync(LogPath(), Header("청취자", "세션 시작됨")
            + "[ 2030.01.01 12:00:05 ] (combat) 250 to Guristas Destroyer - Light Ion Blaster II - Hits\n", ct);

        await WaitUntil(() => { lock (reported) return reported.Count > 0; }, ct);
        await Task.Delay(150, ct);

        lock (reported)
        {
            Assert.Equal(CharacterName, Assert.Single(reported));
        }

        lock (events)
        {
            Assert.Empty(events);
        }
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken cancellationToken, int timeoutMs = 3000)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20, cancellationToken);
        }

        Assert.Fail("Condition was not met within the timeout.");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
