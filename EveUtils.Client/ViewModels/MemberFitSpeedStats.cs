namespace EveUtils.Client.ViewModels;

/// <summary>A member's assigned-fit speed figures for the fleet overview row (ET-40): max velocity with any fitted
/// propulsion module active — the same figure the fit detail window shows for this fit — warp speed in AU/s, and
/// align time. A null instance means no fit is assigned, its RawJson didn't parse, or the SDE is unavailable.</summary>
public sealed record MemberFitSpeedStats(double MaxVelocity, double WarpSpeed, double AlignTime);
