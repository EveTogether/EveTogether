namespace EveUtils.Server.Auth;

/// <summary>Server-wide persisted toggles read via <c>IPermissionToggleStore</c> (default-allow = enabled).</summary>
public static class ServerToggles
{
    /// <summary>When enabled (default), pairing is restricted to the allowed-list; when disabled the server runs
    /// in <b>public mode</b> — anyone who completes the ESI auth-flow can pair. The auth-flow itself
    /// stays mandatory either way.</summary>
    public const string AllowedListEnabled = "auth.allowedlist.enabled";
}
