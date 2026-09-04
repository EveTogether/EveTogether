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
    public string? FitContentHash { get; init; }
    public string? FitNameSnapshot { get; init; }
    public DateTime? LastPushedAtUtc { get; init; }
    public required int Revision { get; init; }
    public required IReadOnlyList<RunLootCaptureWireData> LootCaptures { get; init; }
    public required IReadOnlyList<RunBountyEntryInput> BountyEntries { get; init; }
    public required IReadOnlyList<RunEnemyObservationInput> EnemyObservations { get; init; }
    public required IReadOnlyList<RunParameterInput> Parameters { get; init; }

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
        FitContentHash = run.FitContentHash,
        FitNameSnapshot = run.FitNameSnapshot,
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
            FitContentHash = FitContentHash,
            FitNameSnapshot = FitNameSnapshot,
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
        return run;
    }
}
