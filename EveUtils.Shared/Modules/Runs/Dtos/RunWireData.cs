using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

public sealed class RunWireData
{
    public required Guid Id { get; init; }
    public required long CharacterId { get; init; }
    public string? GroupCode { get; init; }
    public string? FormerGroupCode { get; init; }
    public required ActivityKind ActivityKind { get; init; }
    public required RunState State { get; init; }
    public required DateTime StartedAtUtc { get; init; }
    public DateTime? StoppedAtUtc { get; init; }
    public DateTime? SavedAtUtc { get; init; }

    /// <summary>Travels with the run so the "corrected or measured" verdict survives a sync (ET-98); dropping it
    /// here would lose on the wire exactly what the column was added to keep.</summary>
    public DateTime? TimesCorrectedAtUtc { get; init; }

    /// <summary>Travels for the same reason: a run the app saved by itself must still read as one after a sync
    /// (ET-179).</summary>
    public DateTime? AutoSavedAtUtc { get; init; }
    public DateTime? DeletedAtUtc { get; init; }
    public required int SiteTypeId { get; init; }
    public required SiteTypeSource SiteTypeSource { get; init; }
    public string? SiteName { get; init; }
    public int? SolarSystemId { get; init; }
    public string? Signature { get; init; }

    /// <summary>Travels with the run: dropping it here would lose on the wire exactly what the column was added to
    /// keep.</summary>
    public RunLootStrategy? LootStrategy { get; init; }
    public int? AgentId { get; init; }
    public int? MissionLevel { get; init; }
    public required RunRole Role { get; init; }
    public required bool IsParticipant { get; init; }
    public required bool IsPayoutEligible { get; init; }

    // The homefront attendance decision (ET-230) travels whole, so a member offline when the fleet commander corrected
    // it still adopts it from the commander's own runs on the next pull. None of it required: a payload from an older
    // client or server reads as "nobody decided", which is exactly what it is.
    public bool? InSiteAtCompletion { get; init; }
    public int? AttendanceCount { get; init; }
    public int? AttendanceNotOnRosterCount { get; init; }
    public AttendanceSource? AttendanceSource { get; init; }
    public long? AttendanceSetByCharacterId { get; init; }
    public DateTime? AttendanceSetAtUtc { get; init; }
    public int? FleetSizeAtStop { get; init; }
    public IReadOnlyList<RunAttendanceEntryInput> AttendanceEntries { get; init; } = [];

    public string? FitContentHash { get; init; }
    public string? FitNameSnapshot { get; init; }

    /// <summary>Travels for the same reason as <see cref="FitNameSnapshot"/> (ET-212): a fleetmate's run must still
    /// name its pilot after a sync even if that pilot is not logged in anywhere the receiving client can ask.</summary>
    public string? CharacterNameSnapshot { get; init; }

    /// <summary>Travels for the same reason as <see cref="CharacterNameSnapshot"/> (ET-226): the type a fleetmate's
    /// run resolves to must survive a sync too.</summary>
    public string? SignatureGroupSnapshot { get; init; }
    public DateTime? LastPushedAtUtc { get; init; }
    public required int Revision { get; init; }
    public required IReadOnlyList<RunLootCaptureWireData> LootCaptures { get; init; }
    public required IReadOnlyList<RunBountyEntryInput> BountyEntries { get; init; }
    public required IReadOnlyList<RunEnemyObservationInput> EnemyObservations { get; init; }
    public required IReadOnlyList<RunParameterInput> Parameters { get; init; }
    public required IReadOnlyList<RunMiningEntryInput> MiningEntries { get; init; }

    public static RunWireData FromEntity(Run run) => new()
    {
        Id = run.Id,
        CharacterId = run.CharacterId,
        GroupCode = run.GroupCode,
        FormerGroupCode = run.FormerGroupCode,
        ActivityKind = run.ActivityKind,
        State = run.State,
        StartedAtUtc = run.StartedAtUtc,
        StoppedAtUtc = run.StoppedAtUtc,
        SavedAtUtc = run.SavedAtUtc,
        TimesCorrectedAtUtc = run.TimesCorrectedAtUtc,
        AutoSavedAtUtc = run.AutoSavedAtUtc,
        DeletedAtUtc = run.DeletedAtUtc,
        SiteTypeId = run.SiteTypeId,
        SiteTypeSource = run.SiteTypeSource,
        SiteName = run.SiteName,
        SolarSystemId = run.SolarSystemId,
        Signature = run.Signature,
        LootStrategy = run.LootStrategy,
        AgentId = run.AgentId,
        MissionLevel = run.MissionLevel,
        Role = run.Role,
        IsParticipant = run.IsParticipant,
        IsPayoutEligible = run.IsPayoutEligible,
        InSiteAtCompletion = run.InSiteAtCompletion,
        AttendanceCount = run.AttendanceCount,
        AttendanceNotOnRosterCount = run.AttendanceNotOnRosterCount,
        AttendanceSource = run.AttendanceSource,
        AttendanceSetByCharacterId = run.AttendanceSetByCharacterId,
        AttendanceSetAtUtc = run.AttendanceSetAtUtc,
        FleetSizeAtStop = run.FleetSizeAtStop,
        AttendanceEntries = run.AttendanceEntries.Select(entry => new RunAttendanceEntryInput
        {
            CharacterId = entry.CharacterId,
            CharacterName = entry.CharacterName,
            IsInSite = entry.IsInSite,
            IsExternal = entry.IsExternal,
            Reason = entry.Reason,
            ReasonAmount = entry.ReasonAmount
        }).ToList(),
        FitContentHash = run.FitContentHash,
        FitNameSnapshot = run.FitNameSnapshot,
        CharacterNameSnapshot = run.CharacterNameSnapshot,
        SignatureGroupSnapshot = run.SignatureGroupSnapshot,
        LastPushedAtUtc = run.LastPushedAtUtc,
        Revision = run.Revision,
        LootCaptures = run.LootCaptures.Select(capture => new RunLootCaptureWireData
        {
            CapturedAtUtc = capture.CapturedAtUtc,
            Source = capture.Source,
            Role = capture.Role,
            ContentHash = capture.ContentHash,
            IsExcluded = capture.IsExcluded,
            Entries = capture.Entries.Select(entry => new RunLootEntryInput
            {
                ItemTypeId = entry.ItemTypeId,
                Name = entry.Name,
                Quantity = entry.Quantity,
                Volume = entry.Volume,
                ClipboardPrice = entry.ClipboardPrice,
                LootKind = entry.LootKind
            }).ToList()
        }).ToList(),
        BountyEntries = run.BountyEntries.Select(entry => new RunBountyEntryInput { OccurredAtUtc = entry.OccurredAtUtc, Isk = entry.Isk }).ToList(),
        EnemyObservations = run.EnemyObservations.Select(observation => new RunEnemyObservationInput
        {
            Count = observation.Count,
            EnemyTypeId = observation.EnemyTypeId,
            EnemyName = observation.EnemyName,
            FirstObservedAtUtc = observation.FirstObservedAtUtc,
            LastObservedAtUtc = observation.LastObservedAtUtc
        }).ToList(),
        Parameters = run.Parameters.Select(parameter => new RunParameterInput
        {
            ParameterKey = parameter.ParameterKey,
            TypedValue = parameter.TypedValue,
            Amount = parameter.Amount,
            ItemTypeId = parameter.ItemTypeId,
            BonusWindowSeconds = parameter.BonusWindowSeconds,
            ObservedAtUtc = parameter.ObservedAtUtc
        }).ToList(),
        MiningEntries = run.MiningEntries.Select(entry => new RunMiningEntryInput
        {
            OreType = entry.OreType,
            Units = entry.Units,
            CriticalUnits = entry.CriticalUnits,
            ResidueUnits = entry.ResidueUnits,
            FirstObservedAtUtc = entry.FirstObservedAtUtc,
            LastObservedAtUtc = entry.LastObservedAtUtc
        }).ToList()
    };

    public Run ToEntity()
    {
        var run = new Run
        {
            Id = Id,
            CharacterId = CharacterId,
            GroupCode = GroupCode,
            FormerGroupCode = FormerGroupCode,
            ActivityKind = ActivityKind,
            State = State,
            StartedAtUtc = StartedAtUtc,
            StoppedAtUtc = StoppedAtUtc,
            SavedAtUtc = SavedAtUtc,
            TimesCorrectedAtUtc = TimesCorrectedAtUtc,
            AutoSavedAtUtc = AutoSavedAtUtc,
            DeletedAtUtc = DeletedAtUtc,
            SiteTypeId = SiteTypeId,
            SiteTypeSource = SiteTypeSource,
            SiteName = SiteName,
            SolarSystemId = SolarSystemId,
            Signature = Signature,
            LootStrategy = LootStrategy,
            AgentId = AgentId,
            MissionLevel = MissionLevel,
            Role = Role,
            IsParticipant = IsParticipant,
            IsPayoutEligible = IsPayoutEligible,
            InSiteAtCompletion = InSiteAtCompletion,
            AttendanceCount = AttendanceCount,
            AttendanceNotOnRosterCount = AttendanceNotOnRosterCount,
            AttendanceSource = AttendanceSource,
            AttendanceSetByCharacterId = AttendanceSetByCharacterId,
            AttendanceSetAtUtc = AttendanceSetAtUtc,
            FleetSizeAtStop = FleetSizeAtStop,
            FitContentHash = FitContentHash,
            FitNameSnapshot = FitNameSnapshot,
            CharacterNameSnapshot = CharacterNameSnapshot,
            SignatureGroupSnapshot = SignatureGroupSnapshot,
            LastPushedAtUtc = LastPushedAtUtc,
            Revision = Revision
        };
        foreach (RunLootCaptureWireData capture in LootCaptures)
        {
            var entity = new RunLootCapture
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                CapturedAtUtc = capture.CapturedAtUtc,
                Source = capture.Source,
                Role = capture.Role,
                ContentHash = capture.ContentHash,
                IsExcluded = capture.IsExcluded
            };
            foreach (RunLootEntryInput entry in capture.Entries)
                entity.Entries.Add(new RunLootEntry
                {
                    Id = Guid.CreateVersion7(),
                    RunLootCaptureId = entity.Id,
                    ItemTypeId = entry.ItemTypeId,
                    Name = entry.Name,
                    Quantity = entry.Quantity,
                    Volume = entry.Volume,
                    ClipboardPrice = entry.ClipboardPrice,
                    LootKind = entry.LootKind
                });
            run.LootCaptures.Add(entity);
        }
        foreach (RunBountyEntryInput entry in BountyEntries)
            run.BountyEntries.Add(new RunBountyEntry { Id = Guid.CreateVersion7(), RunId = run.Id, OccurredAtUtc = entry.OccurredAtUtc, Isk = entry.Isk });
        foreach (RunEnemyObservationInput observation in EnemyObservations)
            run.EnemyObservations.Add(new RunEnemyObservation
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                Count = observation.Count,
                EnemyTypeId = observation.EnemyTypeId,
                EnemyName = observation.EnemyName,
                FirstObservedAtUtc = observation.FirstObservedAtUtc,
                LastObservedAtUtc = observation.LastObservedAtUtc
            });
        foreach (RunParameterInput parameter in Parameters)
            run.Parameters.Add(new RunParameter
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                ParameterKey = parameter.ParameterKey,
                TypedValue = parameter.TypedValue,
                Amount = parameter.Amount,
                ItemTypeId = parameter.ItemTypeId,
                BonusWindowSeconds = parameter.BonusWindowSeconds,
                ObservedAtUtc = parameter.ObservedAtUtc
            });
        foreach (RunMiningEntryInput entry in MiningEntries)
            run.MiningEntries.Add(new RunMiningEntry
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                OreType = entry.OreType,
                Units = entry.Units,
                CriticalUnits = entry.CriticalUnits,
                ResidueUnits = entry.ResidueUnits,
                FirstObservedAtUtc = entry.FirstObservedAtUtc,
                LastObservedAtUtc = entry.LastObservedAtUtc
            });
        foreach (RunAttendanceEntryInput entry in AttendanceEntries)
            run.AttendanceEntries.Add(new RunAttendanceEntry
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                CharacterId = entry.CharacterId,
                CharacterName = entry.CharacterName,
                IsInSite = entry.IsInSite,
                IsExternal = entry.IsExternal,
                Reason = entry.Reason,
                ReasonAmount = entry.ReasonAmount
            });
        return run;
    }
}
