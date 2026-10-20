using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// The EXPECTED EARNINGS panel's side of the run window (ET-516): which activity the window is on, handed to the panel
/// whenever that changes. The panel owns everything else; the window only says where it stands.
/// </summary>
public sealed partial class ActivityWindowViewModel
{
    private (SiteTypeSource Source, int TypeId, string? Name, int? Level, int? Tier, string? Weather)? _earningsInputs;

    /// <summary>Null where no source of earnings is registered, so a window built without one has no panel.</summary>
    public ExpectedEarningsViewModel? ExpectedEarnings { get; private set; }

    private void _CreateExpectedEarnings() =>
        ExpectedEarnings = _services.GetService<IExpectedEarningsSource>() is { } source
            ? new ExpectedEarningsViewModel(source, _services.GetService<ILogger<ExpectedEarningsViewModel>>(),
                _services.GetService<ISharedServerPicks>(), _services.GetService<ILootTableOpener>())
            : null;

    /// <summary>Re-points the panel when the site, mission or pocket changed since the last tick. The key is looked up
    /// from the catalogue, so it is only worked out when one of its inputs moved.</summary>
    private void _RefreshExpectedEarnings()
    {
        if (ExpectedEarnings is not { } panel)
            return;

        SiteTypeSource source = _runSiteTypeSource ?? (RunSiteTypeId > 0 ? SiteTypeSource.Site : _SiteTypeSource());
        var inputs = (source, RunSiteTypeId, SignatureName, MissionLevel, TierIndex, Weather?.Name);
        if (inputs.Equals(_earningsInputs))
        {
            panel.UpdateFleetSize(FleetMemberCount);
            return;
        }

        _earningsInputs = inputs;
        _ = panel.TrackAsync(_EarningsKey(inputs), _ActingCharacterId(), GroupCode ?? RunId?.ToString());
    }

    // A freerun is no activity and a type with no key (mining) has nothing to compare: neither gets a panel.
    private ActivityStatsKey? _EarningsKey((SiteTypeSource Source, int TypeId, string? Name, int? Level, int? Tier, string? Weather) inputs)
    {
        if (RunType.ManualStart is ManualStartRequirement.GeneratedName)
            return null;

        (int, string)? abyssal = inputs.Tier is { } tier && inputs.Weather is { Length: > 0 } weather ? (tier, weather) : null;
        ISdeAccessor? sde = _services.GetService<ISdeAccessor>();
        return ActivityStatsKeys.For(Kind, inputs.Source, inputs.TypeId, inputs.Name, inputs.Level, abyssal,
            name => sde?.FindSitesByExactName(name) ?? Array.Empty<SdeSite>());
    }
}
