using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>ACTIVITY on the detail screen: which site, where, and on which fit. The agent and level a mission was
/// handed out by live in MISSION instead (ET-237) — the section that also shows what it handed out.</summary>
public sealed partial class ActivityDetailSectionViewModel(ISdeAccessor? sde)
    : RunDetailSection(RunSectionId.Activity, "ACTIVITY")
{
    [ObservableProperty] private string _siteText = string.Empty;
    [ObservableProperty] private string _locationText = string.Empty;
    [ObservableProperty] private string _signatureText = string.Empty;
    [ObservableProperty] private bool _isSignatureShown;
    [ObservableProperty] private string _fitText = string.Empty;
    [ObservableProperty] private string? _objectivesText;
    [ObservableProperty] private bool _hasObjectives;

    public override bool HasContent => true;

    public override void Apply(RunDetailSectionInput input)
    {
        ActivityDetailDto detail = input.Detail;
        ActivityRunDetailDto? source = detail.Runs.FirstOrDefault();
        // An abyssal has no site to record at all — this reads what filament opened it (ET-241), or the type's own
        // honest name while that is unknown, instead of a line about a site that was never going to exist.
        SiteText = detail.SiteName
            ?? (input.RunType.Space is RunSpace.AbyssalPocket
                ? AbyssalFilamentName.From(detail.Parameters
                    .FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.AbyssalFilament)?.TypedValue)
                : "site not recorded");
        LocationText = _LocationText(detail.SolarSystemId);
        SignatureText = source?.Signature ?? string.Empty;
        IsSignatureShown = !string.IsNullOrWhiteSpace(source?.Signature);
        FitText = source?.FitNameSnapshot ?? "not recognised";

        // Mission counters, and only those: the reward forms belong under MISSION, the escalation under its own
        // section. They stay empty until somebody types them — nothing plausible is filled in for them.
        string[] objectives = [.. detail.Parameters
            .Where(parameter => parameter.ParameterKey is RunParameterKey.Smugglers or RunParameterKey.Civilians)
            .Select(parameter => $"{parameter.ParameterKey.ToString().ToLowerInvariant()} {parameter.TypedValue}")];
        HasObjectives = objectives.Length > 0;
        ObjectivesText = HasObjectives ? string.Join(" · ", objectives) : null;

        HeaderSummary = $"{input.RunType.Name} · {LocationText}";
    }

    /// <summary>The system a run was on, named through the local SDE (ET-213) — never ESI, and never the bare id
    /// the store carries. The same reading the run window already gives live: a plain name, no security status,
    /// because the window itself does not show one either. A stored id the SDE does not carry — an older build, a
    /// boundary case — falls back to the id itself rather than a blank line or an error.</summary>
    private string _LocationText(int? solarSystemId) =>
        solarSystemId is not { } id
            ? "not recorded"
            : sde?.GetSolarSystem(id)?.Name ?? $"system {id}";
}
