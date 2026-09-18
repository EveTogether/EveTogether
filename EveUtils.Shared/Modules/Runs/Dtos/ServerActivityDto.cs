namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>One activity as a server tab shows it (ET-311): the row, and the detail its expanded runs and the pane
/// read — built together from the runs the server handed back, since nothing of it is stored here to read again.</summary>
public sealed record ServerActivityDto(ActivityOverviewRowDto Row, ActivityDetailDto Detail);
