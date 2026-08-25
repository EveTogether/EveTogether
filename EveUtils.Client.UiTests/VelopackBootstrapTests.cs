using EveUtils.Client.Updates;
using Velopack;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The two halves of the bootstrap: the hook that has to run before anything else in <c>Main</c>, and the reading
/// of whether this copy is one the installer placed.
/// </summary>
public class VelopackBootstrapTests
{
    private const string TheHook = "VelopackApp.Build().Run();";

    /// <summary>
    /// Installing, updating and uninstalling re-run this executable with arguments Velopack handles and then exits
    /// on, so whatever sits above the hook runs during every one of those passes: the EF migration and eleven
    /// background services against the user's SQLite, in a window nobody sees. Read from the source because the
    /// property being pinned is <em>position</em>, which a loaded assembly no longer carries.
    /// </summary>
    [Fact]
    public void Main_StartsWithTheVelopackHook_BeforeAnyServiceOrMigration() =>
        Assert.Equal(TheHook, _FirstStatementInMain());

    /// <summary>
    /// The test host never runs the bootstrap, so this covers the "no locator at all" state rather than the
    /// "locator knows of no installed version" one. <c>VelopackLocator.Current</c> throws when unset.
    /// </summary>
    [Fact]
    public void Detect_WithoutABootstrappedLocator_ReportsNotInstalled() =>
        Assert.Equal(UpdateSupport.NotInstalled, new VelopackUpdateSupportProbe().Detect());

    [Fact]
    public void IsInstalledCopy_WithoutABootstrappedLocator_AnswersRatherThanThrows() =>
        Assert.False(VelopackUpdateSupportProbe.IsInstalledCopy());

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void IsInstalledCopy_OnlyWithALocatorThatNamesAVersion_IsTrue(bool locatorIsSet, bool hasVersion, bool expected)
    {
        var version = hasVersion ? new SemanticVersion(1, 2, 3) : null;

        Assert.Equal(expected, VelopackUpdateSupportProbe.IsInstalledCopy(locatorIsSet, () => version));
    }

    /// <summary>Reading the version is exactly what throws without a locator, so the order of the two halves is the guard.</summary>
    [Fact]
    public void IsInstalledCopy_WithoutALocator_DoesNotReachForTheVersion() =>
        Assert.False(VelopackUpdateSupportProbe.IsInstalledCopy(
            locatorIsSet: false,
            static () => throw new InvalidOperationException("the version was read without a locator")));

    [Fact]
    public void Detect_WhenTheCopyIsInstalled_ReportsSupported() =>
        Assert.Equal(UpdateSupport.Supported, VelopackUpdateSupportProbe.Detect(static () => true));

    [Fact]
    public void Detect_WhenTheReadingFails_ReportsNotInstalledRatherThanThrowing() =>
        Assert.Equal(UpdateSupport.NotInstalled, VelopackUpdateSupportProbe.Detect(
            static () => throw new InvalidOperationException("the locator could not work out what this copy is")));

    private static string? _FirstStatementInMain()
    {
        var lines = File.ReadAllLines(_ProgramPath());

        var main = Array.FindIndex(lines, line => line.Contains("public static void Main(", StringComparison.Ordinal));
        Assert.True(main >= 0, "Program.cs no longer declares a 'public static void Main(' — this test reads that method's body");

        var opening = Array.FindIndex(lines, main, line => line.Trim() == "{");
        Assert.True(opening > main, "no opening brace found after Main's signature");

        return lines.Skip(opening + 1)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal));
    }

    private static string _ProgramPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "EveUtils.Client", "Program.cs");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "No folder above the test output holds EveUtils.Client/Program.cs — this test reads the repo it belongs to.");
    }
}
