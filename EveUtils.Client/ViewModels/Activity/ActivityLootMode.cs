namespace EveUtils.Client.ViewModels.Activity;

/// <summary>Which way the LOOT section is worked, per activity kind. A window preference and nothing more: what a run
/// counts as loot is decided by the roles on its captures, so this only ever decides which controls are on screen.
/// Stored by name under <c>ui.activity.lootmode.{kind}</c>, never on the run.</summary>
public enum ActivityLootMode
{
    /// <summary>Copy out of EVE while you fly and every capture is loot. The way it has always worked, and the
    /// default — a pilot who never opens this row keeps exactly what he has.</summary>
    Clipboard,

    /// <summary>Paste the cargo hold you left with and the one you came back with; the loot is the difference.</summary>
    CargoDiff
}
