using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Esi;
using EveUtils.Client.Imaging;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Sde;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// A section of the activity detail screen (ET-236): the unfolded brother of a run window section, reading one saved
/// activity instead of a running one. Every section starts open — the screen is there to be read, not folded.
///
/// Two rules decide whether it is drawn, and the screen applies them, not the section (ET-162): a section its type
/// claims is always drawn, and when it is empty it says what was not measured; a section its type does not claim is
/// drawn only when <see cref="HasContent"/>, and otherwise named with <see cref="AbsentReason"/>.
/// </summary>
public abstract class RunDetailSection : ActivitySection
{
    protected RunDetailSection(RunSectionId id, string title) : base(id, title) => IsExpanded = true;

    /// <summary>Whether the activity has anything for this section, whatever its type claims.</summary>
    public virtual bool HasContent => false;

    /// <summary>The window section's own docstring explains this (<see cref="RunWindowSection.ExpiredBonusIsk"/>):
    /// what this section's reward data states that must not count towards TOTAL ISK any more.</summary>
    public virtual decimal ExpiredBonusIsk => 0m;

    /// <summary>Raised when the section changed the stored activity itself — a loot correction — so the screen reads
    /// the figures it is built from again.</summary>
    public event Action? ActivityCorrected;

    /// <summary>Put the section's text right for the activity as it was just read. Reads nothing itself.</summary>
    public abstract void Apply(RunDetailSectionInput input);

    /// <summary>Whatever the section reads beyond the activity itself — a store read, a live ESI answer. On a
    /// <paramref name="followUp"/> (the activity changed somewhere else) a live answer is only asked again when what
    /// it depends on changed.</summary>
    public virtual Task LoadAsync(RunDetailSectionInput input, bool followUp, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>The one line the screen shows for this section when the type does not claim it and there is nothing
    /// in it; null to leave it unmentioned.</summary>
    public virtual string? AbsentReason(string noun) => null;

    protected void RaiseActivityCorrected() => ActivityCorrected?.Invoke();
}

/// <summary>One read of the activity, as every detail section receives it.</summary>
/// <param name="NameOf">A character's name for this activity — the recorded one first (ET-212), then the live lookup
/// the screen was given, then the bare id.</param>
public sealed record RunDetailSectionInput(ActivityDetailDto Detail, RunTypeDefinition RunType, Func<long, string> NameOf);

/// <summary>What the detail screen was given to build its sections with. Every one but the dispatcher is optional, the
/// same "no service, no action" rule the screen itself follows.</summary>
public sealed record RunDetailSectionServices(
    CqrsDispatcher Dispatcher,
    IAppraisalProvider? Appraisal,
    Func<long, string>? NameOf,
    IEsiClient? Esi,
    IEsiLocationClient? Locations,
    ISdeAccessor? Sde,
    ICharacterPortraitProvider? Portraits,
    ITypeImageProvider? Images,
    IReadOnlySet<long>? OwnCharacterIds);
