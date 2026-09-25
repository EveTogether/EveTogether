namespace EveUtils.Client.ViewModels.Skills;

/// <summary>One OPTIMISE-tab row: the training time the queue's rows for one primary/secondary attribute pair take
/// under the best remap.</summary>
public sealed record AttributePairTimeRowViewModel(string AttributePairText, string TimeText);
