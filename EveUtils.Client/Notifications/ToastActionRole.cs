namespace EveUtils.Client.Notifications;

/// <summary>
/// What a <see cref="ToastAction"/> means for how long its toast stays up. Only a <see cref="Decision"/> makes the toast
/// a question that waits for an answer; the other two are conveniences on a toast that is just telling the user
/// something happened.
/// </summary>
public enum ToastActionRole
{
    Decision,
    Dismiss,
    Undo
}
