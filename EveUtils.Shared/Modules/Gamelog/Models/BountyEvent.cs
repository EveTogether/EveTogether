namespace EveUtils.Shared.Modules.Gamelog.Models;

/// <summary>A bounty payout line ("&lt;isk&gt; ISK added to next bounty payout") — one rat killed.</summary>
public sealed record BountyEvent(
    DateTime Timestamp,
    long Isk) : GameLogEvent(Timestamp);
