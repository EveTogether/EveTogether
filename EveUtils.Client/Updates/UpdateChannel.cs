namespace EveUtils.Client.Updates;

/// <summary>Which stream of builds this copy is willing to be told about.</summary>
public enum UpdateChannel
{
    /// <summary>Tagged releases only.</summary>
    Stable,

    /// <summary>The rolling nightly build of main. Opt-in, and it means what it says.</summary>
    Nightly,
}
