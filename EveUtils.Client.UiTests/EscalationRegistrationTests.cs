using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-125 — one counter-proof per acceptance criterion. Before this ticket, <c>RunParameterKey.Escalation</c> had
/// four read sites and two test sites in the whole repo and not one write site (2026-09-05 grooming), so AC-1 below
/// is the test that proved it: it must fail on the commit before this one.
/// </summary>
public sealed class EscalationRegistrationTests
{
    /// <summary>AC-1: a run can be saved as escalated with a site name, a destination system and a computed
    /// deadline, and all three read back on the detail screen. This is the test the grooming flagged as the trap —
    /// it must be red before <see cref="ActivityWindowViewModel.RegisterEscalationCommand"/> exists, or it is only
    /// proving <c>RunRewardStorageTests</c>' already-working storage rather than this ticket's input.</summary>
    [AvaloniaFact]
    public async Task RegisteringAnEscalation_StoresSiteSystemAndDeadline_VisibleOnTheDetailScreen()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync(ActivityKind.Site);
        model.SignatureName = "Sansha Refuge";
        await model.StartRunCommand.ExecuteAsync(null);
        Assert.NotNull(model.RunId);

        harness.Dialogs.OnShowEscalationDialog = dialog =>
        {
            dialog.SiteQuery = "Command Relay Outpost";
            dialog.DestinationSystem = "Amamake";
            dialog.RemainingTimeText = "23:57:45";
            dialog.RegisterCommand.Execute(null);
            return Task.FromResult(true);
        };
        DateTime beforeRegister = DateTime.UtcNow;
        await model.RegisterEscalationCommand.ExecuteAsync(null);
        await model.SaveRunCommand.ExecuteAsync(null);

        var dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview = await dispatcher.Query(new GetActivityOverviewQuery());
        ActivityOverviewRowDto row = Assert.Single(overview.Value!);
        Assert.True(row.HasEscalation);

        var detail = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId);
        await detail.LoadAsync();

        Assert.Equal("Command Relay Outpost", detail.EscalationText);
        Assert.Equal("Amamake", detail.EscalationSystemText);
        Assert.NotNull(detail.EscalationExpiresAtText);

        await using ClientDbContext db = await harness.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        RunParameter expiry = await db.Set<RunParameter>()
            .SingleAsync(parameter => parameter.ParameterKey == RunParameterKey.EscalationExpiresAtUtc);
        DateTime storedExpiry = DateTime.Parse(expiry.TypedValue, null, System.Globalization.DateTimeStyles.RoundtripKind);
        // Computed from what the pilot typed, not read off the wall clock at some other moment.
        Assert.InRange(storedExpiry, beforeRegister.AddSeconds(23 * 3600 + 57 * 60 + 40),
            beforeRegister.AddSeconds(23 * 3600 + 57 * 60 + 50));
    }

    /// <summary>AC-2: the choice carries the dungeonId, not the name. <c>Sansha's Command Relay Outpost</c> is both
    /// <c>2251</c> (a Combat Site) and <c>2406</c> (an Escalation) — an implementation that matched on name alone
    /// cannot tell them apart, and one that forgets to narrow the picker to the Escalation archetype offers both
    /// under one name, which is exactly what <c>Single()</c> below refuses to let through quietly.</summary>
    [AvaloniaFact]
    public async Task EscalationDungeonId_ComesFromTheArchetypeNarrowedPick_NotFromTheAmbiguousName()
    {
        const string collidingName = "Sansha's Command Relay Outpost";
        var sde = new FakeSdeAccessor()
            .AddSite(new SdeSite(2251, collidingName, null, "Combat Site", null, "Sansha's Nation", null, 3, false, []))
            .AddSite(new SdeSite(2406, collidingName, null, "Escalation", null, "Sansha's Nation", null, 3, false, []));
        using var harness = await ActivityWindowHarness.CreateAsync(
            configure: services => services.AddSingleton<ISdeAccessor>(sde));
        ActivityWindowViewModel model = await harness.OpenAsync(ActivityKind.Site);
        model.SignatureName = "Sansha Refuge";
        await model.StartRunCommand.ExecuteAsync(null);

        harness.Dialogs.OnShowEscalationDialog = dialog =>
        {
            dialog.SiteQuery = collidingName;
            // Only the Escalation archetype survives the dialog's own narrowing — a name-only match would still
            // have both 2251 and 2406 here.
            dialog.SelectedOption = Assert.Single(dialog.SiteResults);
            dialog.RemainingTimeText = "10:00:00";
            dialog.RegisterCommand.Execute(null);
            return Task.FromResult(true);
        };
        await model.RegisterEscalationCommand.ExecuteAsync(null);
        await model.SaveRunCommand.ExecuteAsync(null);

        await using ClientDbContext db = await harness.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        RunParameter dungeonId = await db.Set<RunParameter>()
            .SingleAsync(parameter => parameter.ParameterKey == RunParameterKey.EscalationDungeonId);
        Assert.Equal("2406", dungeonId.TypedValue);
    }

    /// <summary>AC-3: nothing here may guess a duration on the pilot's behalf (ET-124 measured one escalation at
    /// 23h57m45s remaining, which does not prove every escalation carries a 24-hour window). A default sneaked into
    /// the field initializer, or a "24 hours" button wired to <c>RemainingTimeText</c>, turns this red.</summary>
    [Fact]
    public void TheDialog_NeverPrefillsATimeRemaining()
    {
        var dialog = new EscalationDialogViewModel(new FakeSdeAccessor());

        Assert.Equal(string.Empty, dialog.RemainingTimeText);
        Assert.False(dialog.RegisterCommand.CanExecute(null));
    }
}
