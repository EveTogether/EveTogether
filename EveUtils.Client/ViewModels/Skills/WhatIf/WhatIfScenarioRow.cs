namespace EveUtils.Client.ViewModels.Skills.WhatIf;

/// <summary>One WHEN line of the what-if pane: its number, name, what it assumes, the date the plan is done and what
/// it saves against "as the queue stands".</summary>
public sealed record WhatIfScenarioRow(int Number, string Name, string Detail, string DateText, string SavedText, bool IsMarked);
