namespace EveUtils.Shared.Modules.Fittings.Enums;

/// <summary>What happened to a fit library, carried by <c>FittingsChangedEvent</c>. Local only, so new values may be
/// added freely.</summary>
public enum FittingsChangeKind
{
    Imported,

    /// <summary>A fit was removed from the local library (ET-383).</summary>
    Removed,

    /// <summary>A local fit's name, description or tags were edited (ET-383).</summary>
    Edited,

    /// <summary>A fit was added to a server's shared library (ET-383); the server's relay tells every client.</summary>
    Shared,

    /// <summary>A fit was removed from a server's shared library (ET-383); the server's relay tells every client.</summary>
    SharedRemoved
}
