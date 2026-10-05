namespace EveUtils.Client.ViewModels.Setup;

/// <summary>The wizard's steps; Character → Server → Another loops once per character.</summary>
public enum SetupWizardStep
{
    Welcome,
    Character,
    Server,
    Another,
    Done
}
