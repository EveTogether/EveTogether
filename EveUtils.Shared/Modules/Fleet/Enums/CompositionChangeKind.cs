namespace EveUtils.Shared.Modules.Fleet.Enums;

/// <summary>What happened to a composition, carried by <c>CompositionChangedEvent</c>. Any change to its roles or
/// entries counts as <see cref="Edited"/>. This travels the wire between a client and a server that update separately,
/// so new values are only ever appended — the existing ones keep the numbers they already have.</summary>
public enum CompositionChangeKind
{
    Created,
    Edited,
    Deleted
}
