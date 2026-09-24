using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Killmails;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Repositories;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.Killmails;

/// <summary>
/// Turns pasted killmail clipboard text into a provisional killmail (ET-340): parses it with
/// <see cref="KillmailTextParser"/>, resolves the victim's ship through the SDE (no ESI, ever), and stores one row
/// per own character the text names as victim or attacker. Refused with nothing stored when the text is not a
/// killmail, the ship is unknown, or none of the pilot's own characters are on it.
/// </summary>
public sealed class ProvisionalKillmailImporter(ISdeAccessor sde, ICharacterRegistry registry, IProvisionalKillmailRepository repository)
{
    public async Task<ProvisionalKillmailImportResult> ImportAsync(string text, CancellationToken cancellationToken = default)
    {
        KillmailTextCapture? capture = KillmailTextParser.Parse(text);
        if (capture is null)
        {
            return new ProvisionalKillmailImportResult(ProvisionalKillmailImportStatus.NotAKillmailText, 0,
                "That doesn't look like a killmail — paste the full \"Copy\" text from the killmail window.");
        }

        if (!sde.TryGetTypeId(capture.VictimShipName, out int shipTypeId))
        {
            return new ProvisionalKillmailImportResult(ProvisionalKillmailImportStatus.UnknownShip, 0,
                $"Unknown ship: {capture.VictimShipName}.");
        }

        HashSet<string> involvedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            capture.VictimName
        };
        foreach (KillmailTextAttacker attacker in capture.Attackers)
        {
            involvedNames.Add(attacker.Name);
        }

        IReadOnlyList<Character> characters = await registry.GetAllAsync(cancellationToken);
        List<int> ownCharacterIds = [.. characters
            .Where(character => involvedNames.Contains(character.Name))
            .Select(character => character.EsiCharacterId)
            .OfType<int>()];
        if (ownCharacterIds.Count == 0)
        {
            return new ProvisionalKillmailImportResult(ProvisionalKillmailImportStatus.NoOwnCharacter, 0,
                "None of your characters is on this killmail.");
        }

        DateTime nowUtc = DateTime.UtcNow;
        foreach (int characterId in ownCharacterIds)
        {
            await repository.AddAsync(new ProvisionalKillmail
            {
                Id = Guid.NewGuid(),
                CharacterId = characterId,
                KillmailTimeUtc = capture.KillmailTimeUtc,
                VictimName = capture.VictimName,
                VictimShipTypeId = shipTypeId,
                RawText = text,
                CreatedAtUtc = nowUtc
            }, cancellationToken);
        }

        return ProvisionalKillmailImportResult.Ok(ownCharacterIds.Count);
    }
}
