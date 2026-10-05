namespace EveUtils.Client.ViewModels.Skills;

/// <summary>One "where the time goes" bar on OPTIMISE: a primary/secondary pair, its share of the total training time
/// (0-1, the bar's length) and that time at today's attributes.</summary>
public sealed record AttributePairTimeRowViewModel(string AttributePairText, double Share, string TimeText);
