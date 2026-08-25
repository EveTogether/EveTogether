using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using EveUtils.Client.Updates;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The release pipeline writes the update feed that <see cref="UpdateChannelName"/> reads, and the two agree only
/// by both spelling the same four names. Nothing fails when they stop agreeing: <c>vpk pack --channel</c> writes
/// <c>releases.{channel}.json</c>, an install asks for the name it builds itself, and a name that is not there is
/// not an error — the check finds nothing, reports no update, and that installation is never offered a version
/// again. So the workflow is read here and held against the app.
///
/// The workflow's own guard covers the other half of the loop, at release time: it checks that the files vpk
/// actually produced carry these names. This covers the half the guard cannot, because it derives its names from
/// the workflow too — a change to how <see cref="UpdateChannelName"/> is built would leave that guard satisfied.
/// </summary>
public class ReleaseWorkflowChannelTests
{
    /// <summary>The four the app publishes for, asked through the platform/architecture seam so one machine can form all four.</summary>
    private static string[] PublishedChannels =>
    [
        UpdateChannelName.For("win", Architecture.X64),
        UpdateChannelName.For("linux", Architecture.X64),
        UpdateChannelName.For("osx", Architecture.Arm64),
        UpdateChannelName.For("osx", Architecture.X64),
    ];

    /// <summary>
    /// Every build job passes its own <c>RUNTIME_ID</c> straight through as <c>--channel</c>, so these are the
    /// names <c>vpk pack</c> writes into the release. The macOS pair arrives as a matrix rather than as a literal.
    /// </summary>
    [Fact]
    public void TheChannelsTheWorkflowPacks_AreTheChannelsTheAppAsksFor()
    {
        var workflow = File.ReadAllText(_WorkflowPath());

        string[] packed =
        [
            .. _Matches(workflow, @"^\s*RUNTIME_ID:\s*(?!\$\{\{)(\S+)\s*$"),
            .. _Matches(workflow, @"^\s*rid:\s*\[([^\]]+)\]\s*$")
                .SelectMany(list => list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
        ];

        Assert.Equal(PublishedChannels.Order(), packed.Order());
    }

    /// <summary>
    /// The publish-assets guard refuses to attach a release whose feed is incomplete, and it reads the names to
    /// check from this one line. A name here that the app never asks for would guard a file nobody reads.
    /// </summary>
    [Fact]
    public void TheChannelsTheWorkflowGuards_AreTheChannelsTheAppAsksFor()
    {
        var workflow = File.ReadAllText(_WorkflowPath());

        var declared = Assert.Single(_Matches(workflow, @"^\s*CHANNELS:\s*(.+?)\s*$"));

        Assert.Equal(
            PublishedChannels.Order(),
            declared.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order());
    }

    private static List<string> _Matches(string text, string pattern) =>
    [
        .. Regex.Matches(text, pattern, RegexOptions.Multiline).Select(match => match.Groups[1].Value),
    ];

    private static string _WorkflowPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".github", "workflows", "release.yml");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "No folder above the test output holds .github/workflows/release.yml — this test reads the repo it belongs to.");
    }
}
