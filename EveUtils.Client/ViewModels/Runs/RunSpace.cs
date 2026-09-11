namespace EveUtils.Client.ViewModels.Runs;

/// <summary>Where a run type is flown, as far as the run window has to care (ET-236). An abyssal pocket is the one
/// place that changes the run itself: its clock counts down from the pocket's limit, ESI seeing the pilot cross in or
/// out starts and stops it, it has no location and pays no bounty, and the filament brings a tier and a weather.</summary>
public enum RunSpace
{
    KnownSpace,
    AbyssalPocket
}
