namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>Soft-delete lifecycle of a fleet. Archived rows are hard-deleted by the cleanup.</summary>
public enum FleetState
{
    Active = 0,
    Archived = 1
}
