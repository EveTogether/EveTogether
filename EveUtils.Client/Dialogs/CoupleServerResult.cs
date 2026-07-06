namespace EveUtils.Client.Dialogs;

/// <summary>Result of the couple-server dialog: the server address plus an optional user label.</summary>
public sealed record CoupleServerResult(string Address, string? Label);
