namespace EveUtils.Shared.Modules.ApiKeys.Services;

/// <summary>Scope codes an API key can carry. v1 has one: the API is read-only throughout, so this is an
/// on/off access gate rather than a read/write distinction.</summary>
public static class ApiKeyScopes
{
    public const string ReadAll = "read:all";

    public static readonly IReadOnlyList<string> All = [ReadAll];
}
