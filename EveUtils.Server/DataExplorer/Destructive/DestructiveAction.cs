using System.Security.Claims;
using EveUtils.Shared.Messaging;

namespace EveUtils.Server.DataExplorer.Destructive;

/// <summary>An action that removes or ends a record, with everything the confirm needs to say before it runs.</summary>
public sealed class DestructiveAction
{
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required string ButtonLabel { get; init; }
    public required string Question { get; init; }
    public required IReadOnlyList<Consequence> Consequences { get; init; }
    public required string ConfirmLabel { get; init; }
    public DestructiveTone Tone { get; init; } = DestructiveTone.Irreversible;

    /// <summary>Set for the actions that must be confirmed by typing the record's name; null for a two-step confirm.</summary>
    public string? NameToType { get; init; }

    /// <summary>Runs as the given admin. The service behind it checks the permission again, whatever the page offered.</summary>
    public required Func<ClaimsPrincipal, Task<Result>> RunAsync { get; init; }

    public ConfirmTier Tier => NameToType is null ? ConfirmTier.TwoStep : ConfirmTier.TypeName;
}
