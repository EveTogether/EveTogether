namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>A fitting resource that is over budget: used exceeds available. Drives the in-game-style
/// "FITTING ALERT — {resource} overloaded" banner (a later UI pass).</summary>
public sealed record ResourceOverload(FitResource Resource, double Used, double Available);
