namespace EveUtils.Shared.Modules.Fittings.Events;

/// <summary>Payload carried by <see cref="FitDeletedEvent"/>: the server-side id of the removed shared fit.</summary>
public sealed record FitDeletedPayload(int ServerFitId);
