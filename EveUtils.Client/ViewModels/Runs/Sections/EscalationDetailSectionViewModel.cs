using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Opsec;
using EveUtils.Client.Runs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>ESCALATION on the detail screen: every site this activity led to, where, how far it is from the pilot now,
/// and how it ended — with the one-click start of its run and the hand tick-off beside it (ET-451).</summary>
public sealed partial class EscalationDetailSectionViewModel(RunDetailSectionServices services)
    : RunDetailSection(RunSectionId.Escalation, "ESCALATION")
{
    // The jump counts on screen, per destination they were read for — only a new destination is worth asking ESI for.
    private readonly Dictionary<int, (string? Text, string? EmptyText)> _jumpsByDestination = [];

    private ActivityDetailDto? _detail;
    private Guid? _registerRunId;
    private Func<long, string> _nameOf = id => id.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty] private IReadOnlyList<EscalationEntryViewModel> _entries = [];
    [ObservableProperty] private string? _escalationEmptyText;
    [ObservableProperty] private string? _registerMessage;
    [ObservableProperty] private bool _canRegister;

    public override bool HasContent => Entries.Count > 0;

    public override void Apply(RunDetailSectionInput input)
    {
        _detail = input.Detail;
        _nameOf = input.NameOf;
        DateTime nowUtc = DateTime.UtcNow;
        Entries =
        [
            .. input.Detail.Parameters
                .GroupBy(parameter => parameter.RunId)
                .SelectMany(run => RunEscalations.Read(run).Select(escalation => new EscalationEntryViewModel(
                    run.Key, escalation, _IsOwn(input.Detail, run.Key), nowUtc,
                    _StartAsync, _SetOutcomeAsync, _OpenCompletedRunAsync, _ChangeAsync)))
                .OrderBy(entry => entry.Escalation.RegisteredAtUtc)
        ];
        foreach (EscalationEntryViewModel entry in Entries)
            if (entry.DestinationSystemId is { } destination
                && _jumpsByDestination.TryGetValue(destination, out (string? Text, string? EmptyText) jumps))
                (entry.EscalationJumpsText, entry.EscalationJumpsEmptyText) = jumps;

        _registerRunId = _RegisterTargetOf(input.Detail);
        // A site escalates once in practice: a run that carries one changes it rather than registering another (ET-457).
        CanRegister = _registerRunId is { } target && Entries.All(entry => entry.SourceRunId != target);
        EscalationEmptyText = Entries.Count == 0 ? "No escalation has been registered for this activity." : null;
        HeaderSummary = Entries switch
        {
            [] => "none registered",
            [var only] => only.EscalationText ?? "registered",
            _ => $"{Entries.Count} escalations, {Entries.Count(entry => entry.IsOpen)} open"
        };
    }

    public override async Task LoadAsync(RunDetailSectionInput input, bool followUp, CancellationToken cancellationToken)
    {
        // The jump count is a live ESI read (ET-127); a follow-up only asks it again for a destination not read yet.
        foreach (EscalationEntryViewModel entry in Entries)
        {
            if (entry.DestinationSystemId is not { } destination)
                continue;

            if (!_jumpsByDestination.TryGetValue(destination, out (string? Text, string? EmptyText) jumps) || !followUp)
            {
                jumps = await _JumpsAsync(input.Detail, destination, cancellationToken);
                _jumpsByDestination[destination] = jumps;
            }

            (entry.EscalationJumpsText, entry.EscalationJumpsEmptyText) = jumps;
        }
    }

    public override string AbsentReason(string noun) => $"no ESCALATION — {noun} does not escalate";

    /// <summary>A run of this machine's own pilot — the only kind this client may change or fly for (ET-214). A screen
    /// given no list of own pilots treats every run as its own, as every other section does.</summary>
    private bool _IsOwn(ActivityDetailDto detail, Guid runId) =>
        services.OwnCharacterIds is not { } own
        || detail.Runs.FirstOrDefault(run => run.RunId == runId) is { } run && own.Contains(run.CharacterId);

    /// <summary>The run a late registration lands on: this machine's own saved run of the activity (ET-453) — the
    /// escalation was offered to that pilot, and the run is theirs to change (ET-214).</summary>
    private Guid? _RegisterTargetOf(ActivityDetailDto detail) =>
        detail.Runs.FirstOrDefault(run => _IsOwn(detail, run.RunId))?.RunId;

    /// <summary>Registers an escalation on the saved run, the dialog the run window uses (ET-453).</summary>
    [RelayCommand]
    private async Task RegisterAsync()
    {
        if (_registerRunId is not { } runId || await _AskEscalationAsync(null) is not { } registration)
            return;

        Result result = await services.Dispatcher.Send(new RegisterRunEscalationCommand(runId, registration.SiteName,
            registration.DungeonId, registration.DestinationSystem, registration.DestinationSolarSystemId,
            registration.ExpiresAtUtc));
        if (!result.IsSuccess)
        {
            RegisterMessage = result.Messages.Count > 0 ? result.Messages[0].Text : "The escalation could not be registered.";
            return;
        }

        RegisterMessage = null;
        RaiseActivityCorrected();
    }

    /// <summary>Changes the escalation the run already carries instead of adding a second one (ET-457): the same
    /// dialog, filled in from what is registered.</summary>
    private async Task _ChangeAsync(EscalationEntryViewModel entry)
    {
        RunEscalationDto current = entry.Escalation;
        if (await _AskEscalationAsync(current) is not { } registration)
            return;

        Result result = await services.Dispatcher.Send(new ChangeRunEscalationCommand(entry.SourceRunId,
            current.EntryId, registration.SiteName, registration.DungeonId, registration.DestinationSystem,
            registration.DestinationSolarSystemId, registration.ExpiresAtUtc));
        if (!result.IsSuccess)
        {
            entry.ActionMessage = result.Messages.Count > 0 ? result.Messages[0].Text : "The escalation could not be changed.";
            return;
        }

        RaiseActivityCorrected();
    }

    /// <summary>The escalation dialog for this activity's site, empty or filled in from <paramref name="current"/>;
    /// null when the pilot cancelled or the screen has no dialogs or catalogue to offer.</summary>
    private async Task<EscalationRegistration?> _AskEscalationAsync(RunEscalationDto? current)
    {
        if (_detail is not { } detail || services.Services is not { } app
            || app.GetService<IDialogService>() is not { } dialogs || services.Sde is not { } sde)
            return null;

        IReadOnlyList<SdeSite> sourceSites = detail.SiteTypeId > 0
            ? [.. sde.SearchSites().Where(site => site.DungeonId == detail.SiteTypeId)]
            : [];
        EscalationDialogViewModel dialog = await EscalationDialogFactory.CreateAsync(sde, sourceSites, services.Dispatcher);
        if (current is not null)
            dialog.Prefill(current.SiteName, current.DungeonId, current.SystemName, current.ExpiresAtUtc);

        return await dialogs.ShowEscalationDialogAsync(dialog) ? dialog.Result : null;
    }

    private async Task _StartAsync(EscalationEntryViewModel entry)
    {
        if (services.Services is not { } app || app.GetService<IDialogService>() is not { } dialogs
            || _detail?.Runs.FirstOrDefault(run => run.RunId == entry.SourceRunId) is not { } source)
            return;

        string characterName = _nameOf(source.CharacterId);
        entry.ActionMessage = await new EscalationRunStarter(services.Dispatcher, dialogs, app)
            .StartAsync(entry.SourceRunId, source.CharacterId, characterName, entry.Escalation);
    }

    private async Task _SetOutcomeAsync(EscalationEntryViewModel entry, EscalationOutcome? outcome)
    {
        Result result = await services.Dispatcher.Send(
            new SetEscalationOutcomeCommand(entry.SourceRunId, entry.Escalation.EntryId, outcome));
        if (!result.IsSuccess)
        {
            entry.ActionMessage = result.Messages.Count > 0 ? result.Messages[0].Text : "The escalation could not be changed.";
            return;
        }

        RaiseActivityCorrected();
    }

    /// <summary>The link to the run that flew the escalation: its own activity, opened on the detail screen.</summary>
    private async Task _OpenCompletedRunAsync(EscalationEntryViewModel entry)
    {
        if (entry.Escalation.CompletedByRunId is not { } runId || services.Services is not { } app
            || app.GetService<IDialogService>() is not { } dialogs)
            return;

        Result<Guid?> found = await services.Dispatcher.Query(new FindActivitySummaryIdQuery(null, runId));
        if (found.Value is not { } activitySummaryId)
        {
            entry.ActionMessage = "The escalation run is no longer in this store.";
            return;
        }

        dialogs.ShowActivityDetail(new ActivityDetailViewModel(services.Dispatcher, activitySummaryId,
            appraisal: services.Appraisal, nameOf: services.NameOf, esi: services.Esi, locations: services.Locations,
            sde: services.Sde, portraits: services.Portraits, images: services.Images,
            ownCharacterIds: services.OwnCharacterIds, dialogs: dialogs, runChanges: app.GetService<RunChangeFeed>(),
            services: app), activitySummaryId);
    }

    /// <summary>
    /// The jump count to the escalation's destination, read fresh at display time and never stored (ET-127) — an
    /// escalation typed offline stays fully usable; only this one line needs ESI, through the existing ESI client and
    /// its own disk-level cache. The empty text carries why whenever the count itself is null, the same "empty is a
    /// state, not silence" rule as every other empty text on this screen — a hidden JUMPS row would read as "this
    /// escalation has no destination", which is a different fact from "the count could not be read".
    ///
    /// Origin is the character's current location rather than the run's own system: ET-124 never established which
    /// of the two the Agency counts from, and the ticket's own escape hatch for that unknown is to read from
    /// wherever the pilot actually is right now and label the line accordingly (AC-3).
    /// </summary>
    private async Task<(string? Text, string? EmptyText)> _JumpsAsync(
        ActivityDetailDto detail, int destinationSystemId, CancellationToken cancellationToken)
    {
        if (services.Esi is null || services.Locations is null)
            return (null, "Jump count not available: no ESI connection.");

        if (detail.Runs.FirstOrDefault()?.CharacterId is not { } characterId)
            return (null, "Jump count not available: no character recorded on this run.");

        var location = await services.Locations.GetLocationAsync(checked((int)characterId), cancellationToken);
        if (location is not { IsSuccess: true, Value: { } here })
            return (null, "Jump count not available: the pilot's current location could not be read.");

        var route = await services.Esi.GetAsync<int[]>($"/route/{here.SolarSystemId}/{destinationSystemId}/",
            cancellationToken: cancellationToken, expectedNotFound: true);
        return route is { IsSuccess: true, Value.Length: > 0 }
            ? ($"{OpsecText.Mark((route.Value.Length - 1).ToString(CultureInfo.InvariantCulture))} jumps from here", null)
            : (null, "Jump count not available: no stargate route to this system.");
    }
}
