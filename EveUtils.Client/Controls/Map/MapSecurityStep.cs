using Avalonia.Media;

namespace EveUtils.Client.Controls.Map;

/// <summary>One entry of the legend's security scale.</summary>
public sealed record MapSecurityStep(string Text, IBrush Brush);
