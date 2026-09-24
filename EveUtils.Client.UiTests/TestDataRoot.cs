using System.Runtime.CompilerServices;
using EveUtils.Client.Composition;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Points every client in this assembly at a scratch root under the temp folder before any test resolves the
/// player's data folder. A running process locks its root; later runs remove stale roots after crashes.
/// </summary>
internal static class TestDataRoot
{
    public static string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "eveutils-uitest-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Install()
    {
        CleanOrphans(System.IO.Path.GetTempPath());
        Directory.CreateDirectory(Path);
        FileStream runLock = new(System.IO.Path.Combine(Path, ".lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        ClientDataLocation.RootOverride = Path;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            runLock.Dispose();
            TryDelete();
        };
    }

    internal static void CleanOrphans(string parent)
    {
        foreach (string root in Directory.EnumerateDirectories(parent, "eveutils-uitest-*"))
        {
            if (Directory.GetLastWriteTimeUtc(root) >= DateTime.UtcNow.AddHours(-1))
            {
                continue;
            }

            try
            {
                using FileStream rootLock = new(System.IO.Path.Combine(root, ".lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"TestDataRoot: failed to delete stale {root}: {ex.Message}");
            }
        }
    }

    private static void TryDelete()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"TestDataRoot: failed to delete {Path}: {ex.Message}");
        }
    }
}
