using EveUtils.Shared.Modules.Fleet.Enums;

namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>Payload of a composition change, so every open composition window refreshes live.</summary>
/// <param name="CompositionId">The composition that changed; <see cref="UnknownCompositionId"/> when the change was to
/// a role or entry whose composition the publisher could not resolve — receivers then treat it as touching any
/// composition of that source.</param>
/// <param name="IsClientOnly">True when the composition lives only in a client's local library.</param>
public sealed record CompositionChangePayload(
    long CompositionId,
    CompositionChangeKind Kind,
    bool IsClientOnly)
{
    public const long UnknownCompositionId = 0;

    public bool Concerns(long compositionId) =>
        CompositionId == UnknownCompositionId || CompositionId == compositionId;
}
