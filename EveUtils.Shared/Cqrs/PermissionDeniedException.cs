namespace EveUtils.Shared.Cqrs;

/// <summary>Thrown by the dispatcher gate when the current principal lacks a required permission.</summary>
public sealed class PermissionDeniedException(string code) : Exception($"Permission '{code}' is required.")
{
    public string Code { get; } = code;
}
