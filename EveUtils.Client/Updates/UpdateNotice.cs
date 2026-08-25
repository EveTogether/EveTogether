using System.Linq;
using EveUtils.Shared.Messaging;

namespace EveUtils.Client.Updates;

/// <summary>
/// Turns a finished update check into what the user is told, from the result's message code alone. Pure, so the
/// rule that a failed check is never reported as "up to date" is testable without a feed.
/// </summary>
public static class UpdateNotice
{
    /// <summary>
    /// Classifies a check. A success carrying a release is an offer, a success carrying nothing is genuinely current,
    /// and a failure is split on its code — never on its text.
    /// </summary>
    public static UpdateNoticeKind Classify(Result<AppRelease?> check)
    {
        if (check.IsSuccess)
            return check.Value is null ? UpdateNoticeKind.UpToDate : UpdateNoticeKind.Available;

        return check.Messages.Any(message => message.Code == MessageCodes.UpdateNotInstalled)
            ? UpdateNoticeKind.NotInstalled
            : UpdateNoticeKind.Failed;
    }

    /// <summary>
    /// The status-bar line for a check that ran on startup, or null when there is nothing worth saying there.
    /// </summary>
    public static string? StartupStatus(Result<AppRelease?> check, string installedVersion) => Classify(check) switch
    {
        UpdateNoticeKind.Available => $"Update available: v{check.Value!.Version}",
        UpdateNoticeKind.UpToDate => $"You're on the latest version ({installedVersion}).",
        // The feed's own messages already read as sentences that say the check failed, so they are not prefixed again.
        UpdateNoticeKind.Failed => Reason(check),
        // A copy the installer never placed can't act on this, so the startup check stays silent and About explains it.
        _ => null,
    };

    /// <summary>
    /// The message a failed call carries, preferring the error over any warning beside it.
    /// </summary>
    public static string Reason(Result check) =>
        check.Messages.FirstOrDefault(message => message.Severity is MessageSeverity.Error)?.Text
        ?? check.Messages.FirstOrDefault()?.Text
        ?? "The update check failed.";
}
