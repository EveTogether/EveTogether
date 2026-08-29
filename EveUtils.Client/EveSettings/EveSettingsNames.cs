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
/// (<see cref="EveSettingsPreferences"/>), and beside that name sit the characters the account is known to hold
/// (<see cref="EveSettingsLocator.DeriveAccountLinks"/>, remembered per account) — which is what makes an account
/// with no name yet recognisable at all.
/// </summary>
public sealed class EveSettingsNames
{
    private readonly Dictionary<long, string> _characterNames = [];
    private readonly Dictionary<long, string> _accountNames = [];
    private readonly Dictionary<long, AccountCharacterLink> _accountLinks;

    public EveSettingsNames(
        IReadOnlyDictionary<long, string> characterNames,
        IReadOnlyDictionary<long, string> accountNames,
        IReadOnlyDictionary<long, AccountCharacterLink> accountLinks)
    {
        foreach (var (id, name) in characterNames)
            _characterNames[id] = name;
        foreach (var (id, name) in accountNames)
            _accountNames[id] = name;
        _accountLinks = accountLinks.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    /// <summary>An empty set — the state the tool starts in, before a profile has been read.</summary>
    public static EveSettingsNames Empty { get; } = new(
        new Dictionary<long, string>(), new Dictionary<long, string>(), new Dictionary<long, AccountCharacterLink>());

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

    /// <summary>The characters known to sit on this account, by name. Empty when nothing could be established —
    /// showing nothing is deliberate, because the user overwrites files on the strength of this.</summary>
    public IReadOnlyList<string> AccountCharacters(long accountId) =>
        _accountLinks.TryGetValue(accountId, out var link)
            ? link.CharacterIds.Select(CharacterName).ToList()
            : [];

    /// <summary>Where this account's character list came from, or null when there is none.</summary>
    public AccountLinkOrigin? LinkOrigin(long accountId) =>
        _accountLinks.TryGetValue(accountId, out var link) ? link.Origin : null;

    public AccountCharacterLink? Link(long accountId) =>
        _accountLinks.TryGetValue(accountId, out var link) ? link : null;

    /// <summary>Every account link held, including accounts that are not in the profile on screen — what gets
    /// written back, so remembering one account never forgets another.</summary>
    public IReadOnlyList<AccountCharacterLink> AllLinks => _accountLinks.Values.ToList();

    public void SetAccountName(long accountId, string name) => _accountNames[accountId] = name;

    /// <summary>Records the characters the user says are on this account — outranks anything inferred.</summary>
    public void SetAccountCharacters(long accountId, IReadOnlyList<long> characterIds, DateTimeOffset now)
    {
        if (characterIds.Count == 0)
        {
            _accountLinks.Remove(accountId);
            return;
        }

        _accountLinks[accountId] = new AccountCharacterLink
        {
            AccountId = accountId,
            CharacterIds = characterIds.OrderBy(id => id).ToList(),
            Origin = AccountLinkOrigin.UserSet,
            EstablishedAtUtc = now
        };
    }

    /// <summary>Every character id we have a name for — what the "which characters are on this account?" picker
    /// offers, which is more than the selected profile holds when another profile knows a pilot this one does not.</summary>
    public IReadOnlyDictionary<long, string> CharacterNames => _characterNames;

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
