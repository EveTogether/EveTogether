namespace EveUtils.Client.Fleet;

/// <summary>
/// Whether a roster member takes part in <i>this</i> fleet — one of the three axes the overview keeps apart
/// (ET-170): online/offline is presence, linked/elsewhere is participation, sharing is a setting.
/// </summary>
public enum FleetMemberLinkState
{
    /// <summary>The fleet is started and this is the one active fleet the member counts for.</summary>
    Linked,

    /// <summary>The fleet is started, the member is on its roster, but they count for a fleet that started earlier.</summary>
    ElsewhereActive,

    /// <summary>The fleet is standing by; nobody is linked until it starts.</summary>
    StandingBy,

    /// <summary>An external pilot: on the roster on trust, with no client that could ever be linked.</summary>
    NoClient,

    /// <summary>The fleet is finished.</summary>
    Finished,
}
