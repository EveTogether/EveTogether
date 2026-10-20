namespace EveUtils.Client.Runs;

/// <summary>Whose runs the expected earnings of an activity are taken from (ET-516).</summary>
public enum EarningsBasis
{
    /// <summary>The pilot's own saved runs.</summary>
    YourRuns,

    /// <summary>The outcomes every pilot sharing with a server handed to it.</summary>
    AllPilots
}
