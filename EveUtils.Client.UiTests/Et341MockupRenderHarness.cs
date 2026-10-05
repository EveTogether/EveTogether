// Throwaway render harness for the ET-341 mockup-fidelity review: renders every SKILLS screen in the mockup's state
// against a copy of a real SDE and client.db. Runs only with ET341_SOURCE set; removed before the PR is merged.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Dispatcher = Avalonia.Threading.Dispatcher;
using EveUtils.Client.Fittings;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.ViewModels.Skills;
using EveUtils.Client.ViewModels.Skills.Plans;
using EveUtils.Client.ViewModels.Skills.WhatIf;
using EveUtils.Client.Views;
using EveUtils.Client.Views.Skills;
using IDispatcher = EveUtils.Shared.Cqrs.IDispatcher;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Entities;
using EveUtils.Shared.Modules.Skills.Plans.Enums;
using EveUtils.Shared.Modules.Skills.Plans.Repositories;
using EveUtils.Shared.Modules.Skills.Plans.Commands;
using EveUtils.Shared.Modules.Skills.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class Et341MockupRenderHarness
{
    private const int Raymond = 883434905, Soldier = 90000001, Catbank = 90000002;
    private static readonly string? Source = Environment.GetEnvironmentVariable("ET341_SOURCE");
    private static readonly string OutDir = Environment.GetEnvironmentVariable("ET341_SHOTS")
        ?? Path.Combine(Path.GetTempPath(), "et341", "renders");

    [AvaloniaFact]
    public async Task RenderAll()
    {
        if (string.IsNullOrEmpty(Source))
        {
            return;
        }

        Directory.CreateDirectory(OutDir);
        string name = "uitest-et341-" + Guid.NewGuid().ToString("N");
        string dataDir = Path.Combine(TestDataRoot.Path, name);
        Directory.CreateDirectory(Path.Combine(dataDir, "sde"));
        File.Copy(Path.Combine(Source, "client.db"), Path.Combine(dataDir, "client.db"));
        File.Copy(Path.Combine(Source, "sde", "sde.sqlite"), Path.Combine(dataDir, "sde", "sde.sqlite"));
        using var instance = TestClientInstance.Create(instanceName: name);
        var services = instance.Services;
        var sde = services.GetRequiredService<ISdeAccessor>();
        int Id(string typeName) => sde.TryGetTypeId(typeName, out var id) ? id : throw new InvalidOperationException(typeName);

        await _SeedOtherCharactersAsync(services);
        var feroxFit = _Fit(Id, "Ferox · Rails (DPS)", "Ferox", ("250mm Railgun II", 6), ("50MN Microwarpdrive II", 1),
            ("Large Shield Extender II", 2), ("Multispectrum Shield Hardener II", 2), ("Magnetic Field Stabilizer II", 2), ("Damage Control II", 1));
        var basiliskFit = _Fit(Id, "Basilisk · Logi", "Basilisk", ("Large Remote Shield Booster II", 4), ("Large Cap Battery II", 1),
            ("Multispectrum Shield Hardener II", 2), ("Damage Control II", 1));
        var claymoreFit = _Fit(Id, "Claymore · Links", "Claymore", ("Shield Command Burst II", 2), ("Skirmish Command Burst II", 1),
            ("Large Shield Extender II", 2), ("Damage Control II", 1), ("Large Micro Jump Drive", 1));
        await _SeedDoctrineAsync(services, Id, feroxFit, basiliskFit, claymoreFit);
        await _SeedPlanAsync(services, Id, claymoreFit);

        // SKILLS: catalogue, queue, plans (+ what-if), optimise.
        var skills = new SkillsWindowViewModel(services, Raymond);
        await skills.LoadAsync();
        var skillsWindow = new SkillsWindow { DataContext = skills, Width = 1240, Height = 860 };
        skillsWindow.Show();
        if (skills.Catalogue is { } catalogue && catalogue.Groups.FirstOrDefault(g => g.Name == "Fleet Support") is { } fleetSupport)
        {
            catalogue.SelectedGroup = fleetSupport;
            catalogue.SelectedSkillRow = catalogue.Skills.FirstOrDefault(r => r.Name == "Wing Command") ?? catalogue.Skills.FirstOrDefault();
        }
        _Shoot(skillsWindow, "a-catalogue");
        skills.SelectedTabIndex = 1;
        if (skills.Queue is { } queueVm)
        {
            queueVm.SelectedRow = queueVm.Rows.FirstOrDefault();
        }
        _Shoot(skillsWindow, "a-queue");
        skills.SelectedTabIndex = 2;
        await _WaitAsync(() => skills.Plans?.WhatIf is not null);
        _Shoot(skillsWindow, "c-plan");
        _Shoot(skillsWindow, "e-whatif");
        skills.SelectedTabIndex = SkillsWindowViewModel.OptimiseTabIndex;
        _Shoot(skillsWindow, "d-optimise");

        // Share dialog for the plan.
        if (skills.Plans?.SelectedPlan is { } selectedPlan)
        {
            var planRows = await services.GetRequiredService<ISkillPlanReader>().GetRowsAsync(selectedPlan.Id);
            var share = new SkillPlanShareDialogViewModel(services.GetRequiredService<EveUtils.Client.Dialogs.IDialogService>(),
                "Claymore links", planRows, sde, () => Task.CompletedTask);
            var shareWindow = new SkillPlanShareDialogWindow(share) { Width = 620, Height = 520 };
            shareWindow.Show();
            _Shoot(shareWindow, "e-share");
            shareWindow.Close();
        }
        skillsWindow.Close();

        // COMP: doctrine readiness.
        var comps = new CompositionsViewModel(services);
        await comps.ReloadAsync();
        var compsWindow = new CompositionsWindow(comps) { Width = 1240, Height = 860 };
        compsWindow.Show();
        if (comps.SelectedTab?.Compositions.FirstOrDefault() is { } row)
        {
            await comps.ShowReadinessCommand.ExecuteAsync(row);
            if (comps.ReadinessEntries.LastOrDefault() is { } boosts)
            {
                comps.SelectedReadinessEntry = boosts;
            }
        }
        _Shoot(compsWindow, "b-doctrine");
        compsWindow.Close();
        if (comps.SelectedTab?.Compositions.FirstOrDefault() is { } editRow && await editRow.Client.GetAsync(editRow.Id) is { } detail)
        {
            var editor = CompositionEditorViewModel.ForExisting(services, editRow.Client, detail);
            var editorWindow = new CompositionEditorWindow(editor) { Width = 1240, Height = 860 };
            editorWindow.Show();
            _Shoot(editorWindow, "b-doctrine-minimum");
            editorWindow.Close();
        }

        // From a fit: the SKILL IMPACT window for the Ferox, as PLANS → + FROM FIT opens it.
        var dogma = services.GetRequiredService<IDogmaDataAccessor>();
        var calculator = services.GetRequiredService<IDogmaCalculator>();
        var validator = services.GetRequiredService<IFitValidator>();
        var levels = await services.GetRequiredService<ICharacterSkillRepository>().GetLevelsAsync(Catbank);
        var attrs = await services.GetRequiredService<ICharacterAttributesRepository>().GetAsync(Catbank);
        var attributeSet = attrs is { } a ? new CharacterAttributeSet(a.Charisma, a.Intelligence, a.Memory, a.Perception, a.Willpower)
            : CharacterAttributeSet.FittingPanelBaseline;
        var esi = JsonSerializer.Deserialize<EsiFitting>(feroxFit.RawJson) ?? throw new InvalidOperationException("fit");
        var input = new FitInput(esi.ShipTypeId, FitInputMapper.BuildModules(esi, sde, dogma), SkillSource.From(levels), FitInputMapper.BuildDrones(esi));
        var estimator = new SkillTrainingEstimator(dogma);
        var impact = new SkillImpactViewModel(new SkillImpactScanner(calculator, dogma), validator, estimator, attributeSet,
            FitNameResolverFactory.For(services), "skill-impact:et341", "Catbank", feroxFit.FitName, input, levels,
            new SkillTargetsCalculator(calculator, validator, estimator, attributeSet));
        await impact.LoadAsync();
        foreach (var chip in impact.Chips.Where(c => c.IsAvailable && c.Label is "DPS" or "Align time" or "Free CPU"))
        {
            chip.IsSelected = true;
        }
        await _WaitAsync(() => false);
        var impactWindow = new SkillImpactWindow(impact) { Width = 1240, Height = 860 };
        impactWindow.Show();
        _Shoot(impactWindow, "f-from-fit");
        impactWindow.Close();
    }

    private static async Task _WaitAsync(Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
    }

    private static void _Shoot(Window window, string name)
    {
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
        window.CaptureRenderedFrame()?.Save(Path.Combine(OutDir, name + ".png"), new PngBitmapEncoderOptions());
    }

    private static FitReference _Fit(Func<string, int> id, string fitName, string ship, params (string Type, int Count)[] items)
    {
        var slots = new Dictionary<string, int>();
        string Flag(string type)
        {
            string prefix = type.Contains("Railgun") || type.Contains("Command Burst") || type.Contains("Remote Shield") ? "HiSlot"
                : type.Contains("Damage Control") || type.Contains("Stabilizer") ? "LoSlot" : "MedSlot";
            int n = slots.GetValueOrDefault(prefix);
            slots[prefix] = n + 1;
            return prefix + n;
        }
        var parts = items.SelectMany(i => Enumerable.Range(0, i.Count).Select(_ =>
        {
            string flag = Flag(i.Type);
            string charge = i.Type.Contains("Railgun") ? $$""",{"type_id":{{id("Caldari Navy Antimatter Charge M")}},"flag":"{{flag}}","quantity":1}""" : "";
            return $$"""{"type_id":{{id(i.Type)}},"flag":"{{flag}}","quantity":1}""" + charge;
        }));
        string json = $$"""{"fitting_id":0,"name":"{{fitName}}","description":"","ship_type_id":{{id(ship)}},"items":[{{string.Join(',', parts)}}]}""";
        return new FitReference { ShipTypeId = id(ship), FitName = fitName, RawJson = json, ContentHash = fitName };
    }

    private static async Task _SeedOtherCharactersAsync(IServiceProvider services)
    {
        var registry = services.GetRequiredService<ICharacterRegistry>();
        var skills = services.GetRequiredService<ICharacterSkillRepository>();
        var attributes = services.GetRequiredService<ICharacterAttributesRepository>();
        var raymondLevels = await skills.GetLevelsAsync(Raymond);
        foreach (var (id, name, keep) in new[] { (Soldier, "SoldierJRNL", 0.8), (Catbank, "Catbank", 0.6) })
        {
            await registry.AddOrUpdateAsync(new Character(name, id, GrantedScopes: [EveUtils.Shared.Modules.Skills.SkillsScopeCatalog.ReadSkills, EveUtils.Shared.Modules.Skills.SkillsScopeCatalog.ReadSkillQueue]));
            var levels = raymondLevels.Where((_, index) => index % 10 < keep * 10).ToDictionary(p => p.Key, p => p.Value);
            await skills.ReplaceForCharacterAsync(id, levels);
            await attributes.ReplaceForCharacterAsync(new CharacterAttributes
            {
                CharacterId = id, Charisma = 21, Intelligence = 21, Memory = 21, Perception = 31, Willpower = 25,
                TotalSp = 60_000_000, UnallocatedSp = 0
            });
        }
    }

    private static async Task _SeedDoctrineAsync(IServiceProvider services, Func<string, int> id, FitReference ferox, FitReference basilisk, FitReference claymore)
    {
        var repo = services.GetRequiredService<IFleetCompositionRepository>();
        var now = DateTimeOffset.UtcNow;
        var compositionId = await repo.AddAsync(new FleetComposition { Name = "Ferox Fleet", OwnerCharacterId = Raymond, IsClientOnly = true, CreatedAt = now, UpdatedAt = now });
        int order = 0;
        foreach (var (role, min, fit, minimums) in new[]
        {
            ("Mainline DPS", 10, ferox, new[] { ("Medium Hybrid Turret", 5), ("Medium Railgun Specialization", 4), ("Caldari Battlecruiser", 4) }),
            ("Logistics", 2, basilisk, new[] { ("Logistics Cruisers", 4), ("Shield Emission Systems", 5) }),
            ("Boosts", 1, claymore, new[] { ("Command Ships", 3), ("Shield Command", 5), ("Leadership", 5) }),
        })
        {
            var roleId = await repo.AddRoleAsync(new FleetCompositionRole { CompositionId = compositionId, RoleName = role, GroupMinCount = min, SortOrder = order });
            await repo.AddEntryAsync(new FleetCompositionEntry
            {
                RoleId = roleId, Fit = fit, SortOrder = order++,
                SkillMinimums = minimums.Select(m => new FleetCompositionEntrySkillMinimum { SkillTypeId = id(m.Item1), Level = m.Item2 }).ToList()
            });
        }
    }

    private static async Task _SeedPlanAsync(IServiceProvider services, Func<string, int> id, FitReference claymore)
    {
        var dispatcher = services.GetRequiredService<IDispatcher>();
        var validator = services.GetRequiredService<IFitValidator>();
        var levels = await services.GetRequiredService<ICharacterSkillRepository>().GetLevelsAsync(Raymond);
        var esi = JsonSerializer.Deserialize<EsiFitting>(claymore.RawJson) ?? throw new InvalidOperationException("fit");
        var seeds = new[] { esi.ShipTypeId }.Concat(esi.Items.Select(i => i.TypeId)).Distinct().ToList();
        var built = SkillPlanRowFactory.FromFit(validator, seeds, levels, claymore.FitName);
        var plan = await dispatcher.Send(new CreateSkillPlanCommand(Raymond, "Claymore links"));
        await dispatcher.Send(new AddSkillPlanRowsCommand(Raymond, plan.Value, SkillPlanRowSource.Fit, claymore.ContentHash, built.Rows));
        await dispatcher.Send(new AddSkillPlanRowsCommand(Raymond, plan.Value, SkillPlanRowSource.Skill, null,
            [new SkillPlanRowDraft(id("Command Ships"), 2, "Ferox Fleet · Boosts"), new SkillPlanRowDraft(id("Command Ships"), 3, "Ferox Fleet · Boosts")]));
    }
}
