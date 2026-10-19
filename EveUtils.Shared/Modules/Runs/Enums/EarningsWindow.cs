namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>Which time window the earnings figures were taken over (ET-513).</summary>
public enum EarningsWindow
{
    /// <summary>The policy's own window, 90 days by default.</summary>
    Recent,

    /// <summary>The longer fallback window, taken because the recent one held too few runs; shown as older data.</summary>
    Extended
}
