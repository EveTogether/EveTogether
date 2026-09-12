namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>How a homefront ended, said by the pilot or the fleet commander at STOP (ET-231) — never measured, since
/// nothing local reports it. Stored by number. Not used for Abyssal Artifact Recovery, whose outcome is
/// <see cref="Entities.Run.HomefrontCompletedWaveCount"/> instead: a site that fails mid-way still keeps whatever
/// waves it already paid.</summary>
public enum HomefrontOutcome
{
    /// <summary>The site paid out — the one outcome an expected payout is computed for.</summary>
    Completed = 0,

    /// <summary>The site was lost before paying. No expected payout.</summary>
    Failed = 1,

    /// <summary>Nobody said. No expected payout — the same as not having decided, but said out loud rather than left
    /// blank.</summary>
    Unknown = 2
}
