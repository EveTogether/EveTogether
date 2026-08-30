namespace EveUtils.Client.EveSettings;

/// <summary>
/// The standing instruction for the automatic sync (ET-60): which profile, who the source is, and who follows it.
/// The same choice the two blocks in the tool make by hand, only remembered — set it once and the automaton repeats
/// it whenever every EVE client is closed.
///
/// Off unless the user turned it on. A tool that overwrites settings files on its own is not something anybody may
/// meet by surprise, so <see cref="Enabled"/> starts false and only the "use what I have selected" button in the
/// tool ever sets it.
/// </summary>
public sealed record AutoSyncSettings
{
    public bool Enabled { get; init; }

    /// <summary>The install directory the profile lives under — stored so a run does not depend on what the tool
    /// happens to have open, or on the tool being open at all.</summary>
    public string InstallRoot { get; init; } = string.Empty;

    public string ProfileName { get; init; } = string.Empty;

    public long? CharacterSourceId { get; init; }

    public IReadOnlyList<long> CharacterTargetIds { get; init; } = [];

    public long? AccountSourceId { get; init; }

    public IReadOnlyList<long> AccountTargetIds { get; init; } = [];

    public bool HasCharacterRule => CharacterSourceId is not null && CharacterTargetIds.Count > 0;

    public bool HasAccountRule => AccountSourceId is not null && AccountTargetIds.Count > 0;

    /// <summary>True once there is something to run. <see cref="Enabled"/> without this does nothing.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ProfileName) && !string.IsNullOrWhiteSpace(InstallRoot) &&
        (HasCharacterRule || HasAccountRule);

    /// <summary>Nothing set: what the tool reads before the user ever configures it.</summary>
    public static AutoSyncSettings None { get; } = new();
}

/// <summary>What one automatic run did, kept so there is a record of an unattended tool having touched files —
/// when, what it copied, and which backup it left behind to undo it with.</summary>
public sealed record AutoSyncRun(
    DateTimeOffset AtUtc,
    string Summary,
    string BackupId,
    bool Failed);
