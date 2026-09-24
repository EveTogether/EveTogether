namespace EveUtils.Shared.Modules.Gamelog.Models;

/// <summary>
/// The client language a gamelog file is written in, read from its header. <see cref="Unknown"/> is a header this
/// build has no templates for — the file is tracked but none of its lines are read.
/// </summary>
public enum GamelogLanguage
{
    Unknown = 0,
    English,
    German,
    Russian,
    French,
    Spanish,
    Japanese,
    Chinese
}
