using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Enums;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// How far the opening that ET-167 makes is allowed to reach. The automatic stop is the first place the server fills
/// in an acting character itself instead of taking it from a validated session, and the argument that this is safe
/// rests on one property: <b>nothing that handles a request can get at it</b>. That property is true today by
/// accident of who calls whom, and this is what stops it being quietly widened later — a second caller, especially
/// one on a request path, fails here rather than at a review six months from now.
/// </summary>
public class FleetAutoStopReachTests
{
    /// <summary>
    /// Every production file allowed to so much as name an automatic stop, and why. This is the reviewable surface:
    /// adding to it is the moment someone has to re-make the argument, which is the whole point of the list.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        [Path.Combine("EveUtils.Shared", "Modules", "Fleet", "Enums", "FleetStopTrigger.cs")] = "declares them",
        [Path.Combine("EveUtils.Shared", "Modules", "Fleet", "Cleanup", "FleetAutoStopPolicy.cs")] = "decides them",
        [Path.Combine("EveUtils.Shared", "Modules", "Fleet", "Commands", "StopFleetCommandHandler.cs")] = "words them",
        [Path.Combine("EveUtils.Server", "Grpc", "FleetAutoStopRunner.cs")] = "the server's sweep",
        [Path.Combine("EveUtils.Client", "Fleet", "LocalFleetAutoStopService.cs")] = "the client's start-up reckoning",
        [Path.Combine("EveUtils.Server", "Checks", "FleetAutoStopCheck.cs")] = "the headless proof; asserts, never dispatches",
    };

    /// <summary>
    /// The one that matters. A file outside <see cref="Allowed"/> that names an automatic
    /// <see cref="FleetStopTrigger"/> is a new caller, and a new caller needs this argument made again — a request
    /// handler that reached for one would arrive here rather than at a review six months from now.
    /// </summary>
    [Fact]
    public void OnlyTheKnownFew_MayAttributeAStopToTheSystem()
    {
        var root = RepositoryRoot();
        var offenders = ProductionSources()
            .Where(file => Mentions(file, nameof(FleetStopTrigger.RosterEmpty))
                        || Mentions(file, nameof(FleetStopTrigger.AllMembersOffline)))
            .Select(file => Path.GetRelativePath(root.FullName, file))
            .Where(relative => !Allowed.ContainsKey(relative))
            .ToList();

        Assert.True(offenders.Count == 0,
            "A stop attributed to the system may only be raised by the two sweeps. New file(s) naming one: "
            + string.Join(", ", offenders)
            + ". If that is intended, re-make the argument in the PR before adding it to Allowed — the safety of this "
            + "path rests on the acting character never coming from a request.");
    }

    /// <summary>The two sweeps are still on the list; a rename or a delete that quietly took one off would leave the
    /// rule guarding nothing.</summary>
    [Fact]
    public void TheTwoSweeps_AreStillThere()
    {
        var root = RepositoryRoot();
        foreach (var sweep in new[]
                 {
                     Path.Combine("EveUtils.Server", "Grpc", "FleetAutoStopRunner.cs"),
                     Path.Combine("EveUtils.Client", "Fleet", "LocalFleetAutoStopService.cs"),
                 })
        {
            Assert.Contains(sweep, Allowed.Keys);
            Assert.True(File.Exists(Path.Combine(root.FullName, sweep)), sweep);
        }
    }

    /// <summary>
    /// The gRPC surface may not hold the sweep. It is in the same assembly, so visibility cannot keep it out; what
    /// keeps it out is that it is never handed one, and that is what this asserts.
    /// </summary>
    [Fact]
    public void TheGrpcSurface_IsNotHandedTheSweep()
    {
        var injected = typeof(FleetsGrpcService)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType);

        Assert.DoesNotContain(typeof(FleetAutoStopRunner), injected);
    }

    /// <summary>
    /// There is no way into the sweep for a character id at all: it takes a clock, its tuning and the brake, and
    /// reads everything else out of the fleet rows. An overload that accepted one would be the actual widening this
    /// whole argument is about.
    /// </summary>
    [Fact]
    public void TheSweepTakesNoCharacterFromItsCaller()
    {
        var parameters = typeof(FleetAutoStopRunner)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters());

        Assert.DoesNotContain(parameters, parameter =>
            parameter.ParameterType == typeof(int) || parameter.ParameterType == typeof(int?));
    }

    /// <summary>The manual stop keeps its default, so every existing caller goes on meaning a pressed button.</summary>
    [Fact]
    public void AStopWithNoStatedReason_IsStillAManualOne()
    {
        Assert.Equal(FleetStopTrigger.Manual, new StopFleetCommand(FleetId: 1, ActingCharacterId: 2).Trigger);
        Assert.Equal(0, (int)FleetStopTrigger.Manual);
    }

    // ── Reading the repository this test belongs to ─────────────────────────────────────────────────

    private static bool Mentions(string file, string symbol) =>
        File.ReadAllText(file).Contains($"{nameof(FleetStopTrigger)}.{symbol}", StringComparison.Ordinal);

    private static IEnumerable<string> ProductionSources()
    {
        var root = RepositoryRoot();
        foreach (var project in new[] { "EveUtils.Shared", "EveUtils.Server", "EveUtils.Client" })
        {
            var directory = Path.Combine(root.FullName, project);
            if (!Directory.Exists(directory))
                continue;

            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
                if (!file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    yield return file;
        }
    }

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EVE-Together.slnx")))
                return directory;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "No folder above the test output holds EVE-Together.slnx — this test reads the repo it belongs to.");
    }
}
