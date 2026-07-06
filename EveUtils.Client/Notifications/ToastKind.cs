namespace EveUtils.Client.Notifications;

/// <summary>Severity of a transient toast, mapped onto Avalonia's notification styling by the toast host.</summary>
public enum ToastKind
{
    Success,
    Information,
    Warning,
    Error,
}
