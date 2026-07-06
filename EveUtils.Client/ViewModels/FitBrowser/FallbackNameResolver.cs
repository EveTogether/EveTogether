namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>Name resolver used when no SDE store is available (and in tests): always renders <c>type {id}</c>.</summary>
public sealed class FallbackNameResolver : ISdeNameResolver
{
    public static FallbackNameResolver Instance { get; } = new();

    public string TypeName(int typeId) => $"type {typeId}";

    public string? GroupName(int typeId) => null;
}
