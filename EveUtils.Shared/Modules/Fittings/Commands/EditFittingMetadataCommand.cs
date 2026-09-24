using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fittings.Commands;

/// <summary>Sets a local fit's name, description and tags. Its modules and content hash stay as they are, so an edit
/// never changes which fit it is.</summary>
public sealed record EditFittingMetadataCommand(int FittingId, string Name, string? Description, string? Tags) : ICommand<Result>;
