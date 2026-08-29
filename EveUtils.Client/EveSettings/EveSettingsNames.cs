namespace EveUtils.Client.EveSettings;

/// <summary>
/// Turns the ids in a profile's file names into the names the user actually knows.
///
/// Characters resolve for free: the ones linked here come straight from the character registry, and any other id in
/// the folder — an alt that was never added to EVE Together — goes through the same public-ESI lookup the fleet
/// screens use, whose day-long cache keeps a second open offline and quiet.
///
/// Accounts are the hard half: EVE publishes no name for an account id, and nothing in the settings folder maps a
/// character to its account. So the user names an account once and we remember it
/// (<see cref="EveSettingsPreferences"/>), with the characters last written alongside it offered as a hint
/// (<see cref="EveSettingsLocator.AccountCharacterHints"/>) so there is something to recognise it by.
/// </summary>
public sealed class EveSettingsNames
{
    private readonly Dictionary<long, string> _characterNames = [];
    private readonly Dictionary<long, string> _accountNames = [];
    private readonly Dictionary<long, IReadOnlyList<long>> _accountHints;

    public EveSettingsNames(
        IReadOnlyDictionary<long, string> characterNames,
        IReadOnlyDictionary<long, string> accountNames,
        IReadOnlyDictionary<long, IReadOnlyList<long>> accountHints)
    {
        foreach (var (id, name) in characterNames)
            _characterNames[id] = name;
        foreach (var (id, name) in accountNames)
            _accountNames[id] = name;
        _accountHints = accountHints.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    /// <summary>The name to show for a file, whichever kind it is.</summary>
    public string DisplayName(EveSettingsFile file) => file.Kind == SettingsFileKind.Character
        ? CharacterName(file.Id)
        : AccountName(file.Id);

    public string CharacterName(long characterId) =>
        _characterNames.TryGetValue(characterId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : $"Character {characterId}";

    /// <summary>The name the user gave this account, or a placeholder that says it still needs one.</summary>
    public string AccountName(long accountId) =>
        _accountNames.TryGetValue(accountId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : UnnamedAccount;

    public bool HasAccountName(long accountId) =>
        _accountNames.TryGetValue(accountId, out var name) && !string.IsNullOrWhiteSpace(name);

    /// <summary>Characters last saved in the same client session as this account — a hint while naming it, not a
    /// fact. Empty when the write times could not tell them apart.</summary>
    public IReadOnlyList<string> AccountHint(long accountId) =>
        _accountHints.TryGetValue(accountId, out var characters)
            ? characters.Select(CharacterName).ToList()
            : [];

    public void SetAccountName(long accountId, string name) => _accountNames[accountId] = name;

    /// <summary>Every id-to-name pair, as the backup manifest wants it.</summary>
    public IReadOnlyDictionary<long, string> AsLookup()
    {
        var all = new Dictionary<long, string>(_characterNames);
        foreach (var (id, name) in _accountNames)
            all[id] = name;
        return all;
    }

    public const string UnnamedAccount = "Unnamed account";
}
