using System.Text.RegularExpressions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-218: ISK is shown as a whole number everywhere, and the one thing that keeps it that way is that every
/// readout goes through <see cref="EveUtils.Client.Formatting.IskFormat"/> instead of formatting its own number
/// and appending "ISK" beside it — the shape the original bug had, duplicated across a dozen view models
/// (<c>$"{value:N2} ISK"</c>). Counter-proof: a search through the client's own interface code for a number
/// format specifier standing next to the word "ISK" finds nothing outside <c>IskFormat.cs</c> itself. Red against
/// the pre-fix code, where this same search found the dozen call sites the ticket listed.
/// </summary>
public class IskFormatCoverageTests
{
    // The exact shapes a duplicated ISK format takes in this codebase: an interpolation format (":N0}"), an
    // explicit ToString argument ("N0"), or a literal decimal/grouping pattern ("0.00", "0.##", "#,##0"). Deliberately
    // anchored to quotes/braces so this never matches an example figure sitting in a doc comment ("12.4M ISK",
    // "0 ISK") — only an actual format-string token as C# would write it.
    private static readonly Regex NumberFormatToken = new(
        """
        :N[012]}|"N[012]"|"0\.0+"|"0\.#+"|"#,##0[.\d#]*"
        """,
        RegexOptions.Compiled);

    [Fact]
    public void NoIskFormattingLivesOutsideIskFormat()
    {
        string clientRoot = _ClientProjectRoot();
        string iskFormatPath = Path.Combine(clientRoot, "Formatting", "IskFormat.cs");

        List<string> offenders =
        [
            .. Directory.EnumerateFiles(clientRoot, "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(clientRoot, "*.axaml", SearchOption.AllDirectories))
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                               && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                               && !string.Equals(file, iskFormatPath, StringComparison.OrdinalIgnoreCase))
                .SelectMany(file => File.ReadAllLines(file)
                    .Select((line, index) => (file, line, number: index + 1))
                    .Where(entry => entry.line.Contains("ISK") && NumberFormatToken.IsMatch(entry.line))
                    .Select(entry => $"{Path.GetRelativePath(clientRoot, entry.file)}:{entry.number}: {entry.line.Trim()}")),
        ];

        Assert.True(offenders.Count == 0,
            "ISK formatting found outside IskFormat.cs — route it through IskFormat instead:\n" + string.Join('\n', offenders));
    }

    private static string _ClientProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "EveUtils.Client", "EveUtils.Client.csproj");
            if (File.Exists(candidate))
                return Path.Combine(directory.FullName, "EveUtils.Client");

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "No folder above the test output holds EveUtils.Client/EveUtils.Client.csproj — this test reads the repo it belongs to.");
    }
}
