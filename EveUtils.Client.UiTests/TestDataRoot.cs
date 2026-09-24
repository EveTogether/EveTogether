using System.Runtime.CompilerServices;
using EveUtils.Client.Composition;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Points every client in this assembly at a scratch root under the temp folder, before any test can resolve the
/// player's data folder. A test that is aborted, hangs or keeps its data can therefore never leave anything in the
/// real one; the whole root goes when the test process ends.
/// </summary>
internal static class TestDataRoot
{
    public static string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "evetogether-uitests-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Install()
    {
        ClientDataLocation.RootOverride = Path;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete();
    }

    private static void TryDelete()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"TestDataRoot: failed to delete {Path}: {ex.Message}");
        }
    }
}
