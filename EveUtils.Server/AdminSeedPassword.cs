namespace EveUtils.Server;

/// <summary>
/// Resolves the admin-panel bootstrap seed password (A4). Outside Development a missing configured password is a
/// fail-fast config error: never silently seed a known "admin" password in Production, which would leave the panel
/// open with admin/admin until the forced change.
/// </summary>
internal static class AdminSeedPassword
{
    public static string Resolve(string? configured, bool isDevelopment) =>
        configured
        ?? (isDevelopment
            ? "admin"
            : throw new InvalidOperationException("Server:AdminSeedPassword is not configured"));
}
