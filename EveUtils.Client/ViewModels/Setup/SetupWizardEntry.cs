namespace EveUtils.Client.ViewModels.Setup;

/// <summary>Where the setup wizard was opened from; decides the welcome step, the title and the way out.</summary>
public enum SetupWizardEntry
{
    /// <summary>A fresh install, or Settings › General › Setup: welcome first, "Skip setup" to leave.</summary>
    FirstStart,

    /// <summary>The <c>+ ADD CHARACTER</c> button: straight to the character step, "Cancel" to leave.</summary>
    AddCharacter
}
