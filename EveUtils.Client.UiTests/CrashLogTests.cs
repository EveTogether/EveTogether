using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using EveUtils.Shared.Logging;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-197: the last-chance net that ET-196 found missing. <see cref="CrashLog.Install"/> hooks
/// TaskScheduler.UnobservedTaskException and AppDomain.CurrentDomain.UnhandledException so a fault that escapes
/// everything else still leaves a readable line in app-errors.jsonl instead of vanishing with the process.
///
/// AppDomain.UnhandledException isn't covered here: raising a truly unhandled exception is exactly what it takes
/// down the process for, so it can only be measured out-of-process (see Program's <c>--crash-test=appdomain</c>,
/// exercised manually — a live subprocess kill is not something to wire into a test run). The unobserved-task and
/// clean-shutdown paths below don't kill anything and are safe to assert on directly.
/// </summary>
public class CrashLogTests
{
    [Fact]
    public void UnobservedTaskException_IsWrittenToAppErrorsJsonl()
    {
        var dir = Directory.CreateTempSubdirectory("et197-crashlog-").FullName;
        try
        {
            CrashLog.Install(dir);

            RunAndDropFaultingTask();
            // A Debug-build JIT frame reports its locals conservatively for its whole body, so the dropped task
            // must be confined to its own non-inlined method — otherwise this method's frame still roots it.
            for (var i = 0; i < 10 && !FileContains(dir, "UnobservedTaskException"); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Thread.Sleep(20);
            }

            var line = LastLine(dir);
            Assert.Contains("TaskScheduler.UnobservedTaskException", line);
            Assert.Contains("crash-log-test: unobserved task fault", line);

            var entry = JsonSerializer.Deserialize<LogEntry>(line)!;
            Assert.Equal("Crash", entry.Category);
            Assert.NotNull(entry.ExceptionText);
            Assert.Contains("at ", entry.ExceptionText); // a real stack trace, not just a message
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WriteShutdownMarker_WritesAReadableCleanExitLine()
    {
        var dir = Directory.CreateTempSubdirectory("et197-crashlog-").FullName;
        try
        {
            CrashLog.Install(dir);

            CrashLog.WriteShutdownMarker("test harness");

            var line = LastLine(dir);
            var entry = JsonSerializer.Deserialize<LogEntry>(line)!;
            Assert.Equal("Crash", entry.Category);
            Assert.Contains("Clean shutdown", entry.Message);
            Assert.Null(entry.ExceptionText);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NothingWritten_NeverContainsClipboardOrPlayerData()
    {
        // Guards the ET-57 promise at the point where it would be easiest to accidentally break: CrashLog must
        // never be handed anything beyond an exception and a fixed source label.
        var dir = Directory.CreateTempSubdirectory("et197-crashlog-").FullName;
        try
        {
            CrashLog.Install(dir);
            CrashLog.WriteShutdownMarker("test harness");

            var text = File.ReadAllText(Path.Combine(dir, "app-errors.jsonl"));
            Assert.DoesNotContain("clipboard", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunAndDropFaultingTask()
    {
        var faulting = Task.Run(() => throw new InvalidOperationException("crash-log-test: unobserved task fault"));
        while (!faulting.IsCompleted) Thread.Sleep(10);
    }

    private static bool FileContains(string dir, string text)
    {
        var path = Path.Combine(dir, "app-errors.jsonl");
        return File.Exists(path) && File.ReadAllText(path).Contains(text);
    }

    private static string LastLine(string dir) =>
        File.ReadAllLines(Path.Combine(dir, "app-errors.jsonl"))[^1];
}
