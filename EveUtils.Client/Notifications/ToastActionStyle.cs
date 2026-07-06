namespace EveUtils.Client.Notifications;

/// <summary>
/// Visual intent of a <see cref="ToastAction"/> button, mapped onto the themed button classes:
/// <see cref="Affirmative"/> → green ("good"), <see cref="Destructive"/> → red ("danger"),
/// <see cref="Default"/> → the neutral accent button.
/// </summary>
public enum ToastActionStyle
{
    Default,
    Affirmative,
    Destructive
}
