namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>Where a run came from. Stored on the run rather than derived, so a clipboard run with no site name is
/// never mistaken for a manual one just because a field it happens to share with manual runs is empty (ET-163).</summary>
public enum RunOrigin
{
    // First and 0 on purpose: every run that existed before this column did was never Clipboard by measurement,
    // it just defaults to it — an unmigrated fact stays visibly unknown instead of quietly becoming a claim about
    // where it came from.
    Unknown,
    Clipboard,
    Manual,
    Fleet,
    Synchronized
}
