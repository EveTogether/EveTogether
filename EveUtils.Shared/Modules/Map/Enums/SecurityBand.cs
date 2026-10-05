namespace EveUtils.Shared.Modules.Map.Enums;

/// <summary>
/// The security class a pilot reads off a system, decided on the displayed (rounded) security status the way the
/// game shows it: 0.5 and up is highsec, above 0.0 is lowsec, the rest nullsec.
/// </summary>
public enum SecurityBand
{
    High,
    Low,
    Null
}
