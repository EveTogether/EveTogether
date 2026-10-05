namespace EveUtils.Shared.Modules.Fleet.Metrics;

/// <summary>Why a fleet member reads the way they do (ET-440). One reason per member, so a row never says "not sharing
/// a system" for a pilot whose client is simply closed.</summary>
public enum FleetMemberStatusReason
{
    /// <summary>In game, with a system to show.</summary>
    InSystem = 0,

    /// <summary>Reporting and sharing their location, but no system has been read yet.</summary>
    NoSystemYet = 1,

    /// <summary>Reporting, and keeping their location to themselves.</summary>
    LocationWithheld = 2,

    /// <summary>Their client says EVE is not running for this character.</summary>
    NotInGame = 3,

    /// <summary>Their client was heard from and has gone quiet — EVE Together is closed or cut off.</summary>
    Silent = 4,

    /// <summary>The server says this character has no connection to it.</summary>
    NotConnected = 5,

    /// <summary>On the roster, never heard from in this fleet's stream, and the server cannot say more.</summary>
    NeverHeard = 6,

    /// <summary>Reporting, but from a client too old to say what it shares — a missing system cannot be explained.</summary>
    OldClient = 7,
}

/// <summary>What one member's row says, and from what. Pure, so every screen and the tests read the same verdict.</summary>
public static class FleetMemberStatus
{
    /// <param name="isConnected">The server's word on whether the character has a connection; null from a server too old
    /// to say.</param>
    /// <param name="lastHeardAt">When this client last received a sample of theirs in this fleet; null if never.</param>
    /// <param name="presence">The <see cref="FleetMemberPresence.Read"/> verdict.</param>
    /// <param name="shares">Their last <see cref="MetricKind.Shares"/> manifest; null when they never sent one.</param>
    /// <param name="system">The system they last shared, if any.</param>
    public static FleetMemberStatusReason Read(
        bool? isConnected,
        DateTimeOffset? lastHeardAt,
        FleetMemberPresenceState presence,
        SharedMetrics? shares,
        string? system,
        DateTimeOffset now)
    {
        if (lastHeardAt is null || FleetMemberPresence.IsSilent(lastHeardAt, now))
            return isConnected is false
                ? FleetMemberStatusReason.NotConnected
                : lastHeardAt is null ? FleetMemberStatusReason.NeverHeard : FleetMemberStatusReason.Silent;

        if (presence is FleetMemberPresenceState.Offline)
            return FleetMemberStatusReason.NotInGame;

        if (system is not null)
            return FleetMemberStatusReason.InSystem;

        return shares switch
        {
            null => FleetMemberStatusReason.OldClient,
            { } offered when !offered.HasFlag(SharedMetrics.Location) => FleetMemberStatusReason.LocationWithheld,
            _ => FleetMemberStatusReason.NoSystemYet,
        };
    }
}
