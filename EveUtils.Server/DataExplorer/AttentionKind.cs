namespace EveUtils.Server.DataExplorer;

/// <summary>Declaration order is the order the list is shown in within one severity.</summary>
public enum AttentionKind
{
    EsiRefreshFailing = 0,
    ReferencesUnpairedCharacter = 1,
    FleetCompositionMissing = 2,
    CompositionFitMissing = 3,
    SessionPastIdleLifetime = 4,
    FleetSilent = 5,
    FleetArchived = 6,
    CharacterNeverConnected = 7,
}
