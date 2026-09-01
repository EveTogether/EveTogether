using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

public sealed class RunWireData
{
    public required Guid Id { get; init; }
    public required long CharacterId { get; init; }
    public string? GroupCode { get; init; }
    public required ActivityKind ActivityKind { get; init; }
    public required RunState State { get; init; }
    public required DateTime StartedAtUtc { get; init; }
    public DateTime? StoppedAtUtc { get; init; }
    public DateTime? SavedAtUtc { get; init; }
    public DateTime? DeletedAtUtc { get; init; }
    public required int SiteTypeId { get; init; }
    public string? SiteName { get; init; }
    public int? SolarSystemId { get; init; }
    public string? Signature { get; init; }
    public required RunRole Role { get; init; }
    public required bool IsPayoutEligible { get; init; }
    public string? FitContentHash { get; init; }
    public string? FitNameSnapshot { get; init; }
    public DateTime? LastPushedAtUtc { get; init; }
    public required int Revision { get; init; }
    public required IReadOnlyList<RunLootCaptureInput> LootCaptures { get; init; }
    public required IReadOnlyList<RunBountyEntryInput> BountyEntries { get; init; }
    public required IReadOnlyList<RunEnemyObservationInput> EnemyObservations { get; init; }
    public required IReadOnlyList<RunParameterInput> Parameters { get; init; }

    public static RunWireData FromEntity(Run run) => new()
    {
        Id = run.Id,
        CharacterId = run.CharacterId,
        GroupCode = run.GroupCode,
        ActivityKind = run.ActivityKind,
        State = run.State,
        StartedAtUtc = run.StartedAtUtc,
        StoppedAtUtc = run.StoppedAtUtc,
        SavedAtUtc = run.SavedAtUtc,
        DeletedAtUtc = run.DeletedAtUtc,
        SiteTypeId = run.SiteTypeId,
        SiteName = run.SiteName,
        SolarSystemId = run.SolarSystemId,
        Signature = run.Signature,
        Role = run.Role,
        IsPayoutEligible = run.IsPayoutEligible,
        FitContentHash = run.FitContentHash,
        FitNameSnapshot = run.FitNameSnapshot,
        LastPushedAtUtc = run.LastPushedAtUtc,
        Revision = run.Revision,
        LootCaptures = run.LootCaptures.Select(capture => new RunLootCaptureInput
        {
            CapturedAtUtc = capture.CapturedAtUtc,
            Source = capture.Source,
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
            EnemyTypeId = observation.EnemyTypeId,
            EnemyName = observation.EnemyName,
            Direction = observation.Direction,
            FirstObservedAtUtc = observation.FirstObservedAtUtc,
            LastObservedAtUtc = observation.LastObservedAtUtc
        }).ToList(),
        Parameters = run.Parameters.Select(parameter => new RunParameterInput
        {
            ParameterKey = parameter.ParameterKey,
            TypedValue = parameter.TypedValue,
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
            ActivityKind = ActivityKind,
            State = State,
            StartedAtUtc = StartedAtUtc,
            StoppedAtUtc = StoppedAtUtc,
            SavedAtUtc = SavedAtUtc,
            DeletedAtUtc = DeletedAtUtc,
            SiteTypeId = SiteTypeId,
            SiteName = SiteName,
            SolarSystemId = SolarSystemId,
            Signature = Signature,
            Role = Role,
            IsPayoutEligible = IsPayoutEligible,
            FitContentHash = FitContentHash,
            FitNameSnapshot = FitNameSnapshot,
            LastPushedAtUtc = LastPushedAtUtc,
            Revision = Revision
        };
        foreach (RunLootCaptureInput capture in LootCaptures)
        {
            var entity = new RunLootCapture { Id = Guid.CreateVersion7(), RunId = run.Id, CapturedAtUtc = capture.CapturedAtUtc, Source = capture.Source };
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
                EnemyTypeId = observation.EnemyTypeId,
                EnemyName = observation.EnemyName,
                Direction = observation.Direction,
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
                ObservedAtUtc = parameter.ObservedAtUtc
            });
        return run;
    }
}
