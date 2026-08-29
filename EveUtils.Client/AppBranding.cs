namespace EveUtils.Client;

/// <summary>
/// Brand text that is not derived from anything at runtime and is shown in more than one window.
/// </summary>
public static class AppBranding
{
    /// <summary>
    /// The release-stage badge next to the app name — the title bar and the About window both show it.
    /// It has already been "POC" once; keeping the word in one place is what stops the two badges from
    /// drifting apart the next time the stage changes.
    /// </summary>
    public const string ReleaseStage = "Beta";
}
