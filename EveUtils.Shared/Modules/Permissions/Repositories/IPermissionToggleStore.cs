namespace EveUtils.Shared.Modules.Permissions.Repositories;

/// <summary>
/// Persisted on/off toggles per app-permission code. The admin panel flips these; the
/// <c>ToggleablePolicy</c> reads them. Default is enabled when a code has never been set. The
/// interface is synchronous because it is read on the (synchronous) policy hot path and from Blazor
/// property accessors; the EF-backed implementation keeps an in-memory cache to honour that.
/// </summary>
public interface IPermissionToggleStore
{
    bool IsEnabled(string code);

    void SetEnabled(string code, bool value);
}
