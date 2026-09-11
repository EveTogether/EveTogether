using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-236: the run window and the detail screen are built from the sections a run's type declares, and nothing else
/// decides them. These hold that shape in place — the way it slid back before (ET-222) was one check at a time.
/// </summary>
public sealed class RunSectionFrameworkTests
{
    // ── The catalogue ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A type with no sections would open an empty window. Counter-proof: give the Mission row an empty
    /// DetailSections list and this goes red on Mission.</summary>
    [Fact]
    public void EveryRunType_DeclaresSectionsOnBothScreens()
    {
        foreach (RunTypeDefinition type in RunTypeCatalogue.All)
        {
            Assert.True(type.WindowSections.Count > 0, $"{type.Name} has no run window sections");
            Assert.True(type.DetailSections.Count > 0, $"{type.Name} has no detail screen sections");
        }
    }

    /// <summary>Every <see cref="RunTypeId"/> has a row of its own, and every row is filed under the type it
    /// describes — a row under a type that does not exist, or under another type's key, is a section list nobody can
    /// reach. Counter-proof: file the DataSite row under <c>(RunTypeId)99</c> and this goes red twice.</summary>
    [Fact]
    public void EveryCatalogueRow_IsFiledUnderTheTypeItDescribes()
    {
        foreach ((RunTypeId key, RunTypeDefinition row) in RunTypeCatalogue.Rows)
        {
            Assert.True(Enum.IsDefined(key), $"a catalogue row is filed under {(int)key}, which is no RunTypeId");
            Assert.Equal(key, row.Id);
        }

        Assert.Equal(Enum.GetValues<RunTypeId>().Order(), RunTypeCatalogue.Rows.Select(row => row.Key).Order());
    }

    /// <summary>A section id a type names must be a module on the screen it names it for. Counter-proof: add
    /// <c>RunSectionId.Fit</c> to a type's DetailSections — FIT is a run window module only — and this goes red.</summary>
    [Fact]
    public void EveryDeclaredSection_IsAModuleOnThatScreen()
    {
        foreach (RunTypeDefinition type in RunTypeCatalogue.All)
        {
            foreach (RunSectionId id in type.WindowSections)
                Assert.True(RunSectionModules.All.Any(module => module.Id == id && module.CreateForWindow is not null),
                    $"{type.Name} names {id} for the run window, which has no {id} module");
            foreach (RunSectionId id in type.DetailSections)
                Assert.True(RunSectionModules.All.Any(module => module.Id == id && module.CreateForDetail is not null),
                    $"{type.Name} names {id} for the detail screen, which has no {id} module");
        }
    }

    /// <summary>A module no type claims is dead code on the run window, and on the detail screen it can only ever
    /// appear by accident. Counter-proof: take ESCALATION out of every site row and this goes red on it.</summary>
    [Fact]
    public void EveryModule_IsClaimedBySomeType()
    {
        foreach (RunSectionModule module in RunSectionModules.All)
        {
            if (module.CreateForWindow is not null)
                Assert.True(RunTypeCatalogue.All.Any(type => type.WindowSections.Contains(module.Id)),
                    $"no type draws {module.Id} in the run window");
            if (module.CreateForDetail is not null)
                Assert.True(RunTypeCatalogue.All.Any(type => type.DetailSections.Contains(module.Id)),
                    $"no type draws {module.Id} on the detail screen");
        }

        Assert.Equal(RunSectionModules.All.Count, RunSectionModules.All.Select(module => module.Id).Distinct().Count());
        Assert.True(Enum.GetValues<RunSectionId>().All(id => RunSectionModules.All.Any(module => module.Id == id)),
            "a RunSectionId has no module");
    }

    /// <summary>The screens draw sections in the registry's order whatever a type lists; a row that lists them in
    /// another order would read as a promise the screen does not keep.</summary>
    [Fact]
    public void EveryType_ListsItsSectionsInScreenOrder()
    {
        List<RunSectionId> order = [.. RunSectionModules.All.Select(module => module.Id)];
        foreach (RunTypeDefinition type in RunTypeCatalogue.All)
        {
            Assert.Equal(type.WindowSections.OrderBy(order.IndexOf), type.WindowSections);
            Assert.Equal(type.DetailSections.OrderBy(order.IndexOf), type.DetailSections);
        }
    }

    // ── The modules ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A section's body is found by the app's ViewLocator from its view model's name. Counter-proof: rename
    /// <c>FitWindowSectionView</c> and this goes red, where the window itself would only have shown "Not Found".</summary>
    [Fact]
    public void EverySectionViewModel_HasTheViewTheViewLocatorLooksFor()
    {
        IEnumerable<Type> sections = typeof(ActivitySection).Assembly.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(ActivitySection)) && !type.IsAbstract);

        foreach (Type section in sections)
        {
            string viewName = (section.FullName ?? section.Name).Replace("ViewModel", "View", StringComparison.Ordinal);
            Type? view = section.Assembly.GetType(viewName);
            Assert.True(view is not null && view.IsSubclassOf(typeof(Control)), $"{section.Name} has no {viewName}");
        }
    }

    /// <summary>Each module builds a section of its own id, on both screens — the id is what a type claims it by.</summary>
    [AvaloniaFact]
    public void EveryModule_BuildsASectionOfItsOwnId()
    {
        using var instance = TestClientInstance.Create();
        var window = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        var services = new RunDetailSectionServices(instance.Services.GetRequiredService<Shared.Cqrs.IDispatcher>(),
            null, null, null, null, null, null, null, null);

        foreach (RunSectionModule module in RunSectionModules.All)
        {
            if (module.CreateForWindow is { } createForWindow)
                Assert.Equal(module.Id, createForWindow(window).Id);
            if (module.CreateForDetail is { } createForDetail)
                Assert.Equal(module.Id, createForDetail(services).Id);
        }
    }

    // ── A type change mid-run ───────────────────────────────────────────────────────────────────────

    /// <summary>A site that starts without a scanner group and gets one later (the run it adopts carries it) changes
    /// type mid-run. The sections follow the new type by id, so the ones that stay are the same objects — open or
    /// folded as the pilot left them, with what they collected.</summary>
    [Fact]
    public void ATypeChangeMidRun_KeepsEverySectionItStillHas_AndItsState()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Site, new ServiceCollection().BuildServiceProvider());
        List<RunWindowSection> before = [.. model.Sections];
        model.Sections.Single(section => section.Id == RunSectionId.Loot).IsExpanded = true;
        model.Activity().IsExpanded = false;

        model.SignatureGroup = "Combat Site";
        model.SignatureGroup = "Data Site";

        Assert.Equal(RunTypeId.DataSite, model.RunType.Id);
        Assert.Equal(before, model.Sections);
        Assert.True(model.Sections.Single(section => section.Id == RunSectionId.Loot).IsExpanded);
        Assert.False(model.Activity().IsExpanded);
        Assert.Equal("Data Site", model.Activity().SignatureTypeText);
    }

    // ── No kind checks outside the framework ────────────────────────────────────────────────────────

    /// <summary>What these screens used to be made of: <c>Kind ==</c>, <c>IsAbyssal</c>, a switch on the kind. What a
    /// type means is <see cref="RunTypeCatalogue"/>'s to say, and what a section does is the section's; a check on the
    /// kind or a specific type id anywhere below is how the window grows back into one class that knows every type.
    ///
    /// Counter-proof: the same rules over <c>origin/main</c> 16b810c find 33 lines (24 in the run window, 9 in the
    /// detail screen); add <c>public bool IsSite => Kind == ActivityKind.Site;</c> to the window and it goes red on
    /// that line.</summary>
    [Fact]
    public void TheRunScreens_CheckNoKindOutsideTheCatalogue()
    {
        List<string> offences = [];
        foreach (string file in _ScreenSources())
        {
            string[] lines = File.ReadAllLines(file);
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                if (KindCheck.IsMatch(line) && !KeptOnPurpose.Any(kept => line.Contains(kept.Snippet, StringComparison.Ordinal)))
                    offences.Add($"{Path.GetFileName(file)}:{index + 1}: {line.Trim()}");
            }
        }

        Assert.True(offences.Count == 0,
            "A kind or type check outside the catalogue — move it into RunTypeCatalogue or the section:\n"
            + string.Join("\n", offences));
    }

    // The window's own Kind compared or switched on; any ActivityKind or RunTypeId member named; the old IsAbyssal.
    // A property of something else called Kind (a metric sample's) is preceded by a dot and not matched.
    private static readonly Regex KindCheck = new(
        @"(?<![\w.])Kind\s*(==|!=)|(?<![\w.])Kind\s+(is|switch)\b|(==|!=)\s*Kind\b|\bActivityKind\.[A-Z]\w*|\bRunTypeId\.[A-Z]\w*|\bIsAbyssal\b",
        RegexOptions.Compiled);

    /// <summary>The kind references that stay, each with its reason — the same reason stands beside it in the code.</summary>
    private static readonly (string Snippet, string Reason)[] KeptOnPurpose =
    [
        ("run.ActivityKind != Kind", "identity: a window only adopts or switches to a run of the kind it was opened as"),
        ("start.ActivityKind != Kind", "identity: a window only joins a fleet start of the kind it was opened as"),
        ("_SiteTypeSource() => Kind switch", "storage: how the store files the row's site, keyed on the kind it is filed under"),
        ("ActivityKind.Mission => SiteTypeSource.Mission", "storage, the same rule"),
        ("ActivityKind.Site when SignatureName is not null", "storage, the same rule"),
        ("new PendingCopy(ActivityKind.Site", "a copied signature opens a site window"),
        ("new PendingCopy(ActivityKind.Mission", "a copied mission opens a mission window")
    ];

    private static IEnumerable<string> _ScreenSources()
    {
        string viewModels = _SourcePath("EveUtils.Client/ViewModels");
        return
        [
            Path.Combine(viewModels, "Activity", "ActivityWindowViewModel.cs"),
            Path.Combine(viewModels, "Runs", "ActivityDetailViewModel.cs"),
            .. Directory.EnumerateFiles(Path.Combine(viewModels, "Runs", "Sections"), "*.cs")
        ];
    }

    /// <summary>The repository file, found from the test binary rather than from a checkout path baked in here.</summary>
    private static string _SourcePath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EVE-Together.slnx")))
            directory = directory.Parent;

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("the solution root is not above the test binary"),
            relative);
    }
}
