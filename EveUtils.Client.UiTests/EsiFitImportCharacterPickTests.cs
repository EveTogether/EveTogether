using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Fittings;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-143: "import fits from EVE" only asks which character when there is a choice to make. Driven through the
/// command the button is bound to, because the skip has to happen at the call point — the picker itself is fine.
/// </summary>
public class EsiFitImportCharacterPickTests
{
    private sealed class StubFittingEsiClient : IFittingEsiClient
    {
        public int? AskedFor { get; private set; }

        public Task<IReadOnlyList<EsiFitting>> GetFittingsAsync(int characterId, string accessToken,
            CancellationToken cancellationToken = default)
        {
            AskedFor = characterId;
            return Task.FromResult<IReadOnlyList<EsiFitting>>(
                [new EsiFitting(1, "Rifter PvP", "", 587, [])]);
        }

        public Task<int> PostFittingAsync(int characterId, string accessToken, EsiFittingWrite fitting,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteFittingAsync(int characterId, string accessToken, int fittingId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static CharacterViewModel Pilot(string name, int id, bool canRead = true, bool hasToken = true) =>
        new(new Character(name, id, canRead ? [FittingsScopeCatalog.ReadFittings] : []))
        {
            EsiTokenStatus = hasToken ? TokenStatus.Valid : TokenStatus.NoToken
        };

    private static async Task<(MainWindowViewModel Shell, RecordingDialogService Dialogs, StubFittingEsiClient Esi,
        TestClientInstance Instance)> ShellAsync(params CharacterViewModel[] characters)
    {
        var esi = new StubFittingEsiClient();
        var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IDialogService, RecordingDialogService>();
            services.AddSingleton<IFittingEsiClient>(esi);
        });

        var tokens = instance.Services.GetRequiredService<IPerCharacterTokenStore>();
        foreach (var c in characters.Where(c => c.HasEsiToken))
            await tokens.SaveAsync(c.CharacterId, new EsiTokenSet("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)));

        var shell = new MainWindowViewModel(instance.Services);
        shell.Characters.Clear();
        foreach (var c in characters) shell.Characters.Add(c);

        return (shell, (RecordingDialogService)instance.Services.GetRequiredService<IDialogService>(), esi, instance);
    }

    [AvaloniaFact]
    public async Task OneSelectableCharacter_SkipsThePicker_AndImportsForThatCharacter()
    {
        var (shell, dialogs, esi, instance) = await ShellAsync(Pilot("Jithran", 90250177));
        using var _ = instance;

        await shell.ImportFittingsCommand.ExecuteAsync(null);

        Assert.Null(dialogs.LastPrompt);                                   // no window with a single row and a button
        Assert.Equal(90250177, esi.AskedFor);                              // …and it went on with that character
        Assert.Equal("Rifter PvP", Assert.Single(dialogs.LastFittingsOffered!).Name); // straight to the fit choice
    }

    [AvaloniaFact]
    public async Task OneSelectableCharacterAmongGreyedOutOnes_StillSkipsThePicker()
    {
        // The greyed rows only explain why the others can't be picked; that explanation lives in the character
        // dialog's scope tooltip, so it is not worth a modal on every import.
        var (shell, dialogs, esi, instance) = await ShellAsync(
            Pilot("Jithran", 90250177),
            Pilot("Catbank", 90250178, canRead: false),
            Pilot("Alt Three", 90250179, hasToken: false));
        using var _ = instance;

        await shell.ImportFittingsCommand.ExecuteAsync(null);

        Assert.Null(dialogs.LastPrompt);
        Assert.Equal(90250177, esi.AskedFor);
    }

    [AvaloniaFact]
    public async Task TwoSelectableCharacters_StillAsk()
    {
        var (shell, dialogs, esi, instance) = await ShellAsync(
            Pilot("Jithran", 90250177), Pilot("Catbank", 90250178));
        using var _ = instance;
        dialogs.OnPickCharacter = (_, options) => Task.FromResult<int?>(options.Last().CharacterId);

        await shell.ImportFittingsCommand.ExecuteAsync(null);

        Assert.Equal("Import fits for which character?", dialogs.LastPrompt);
        Assert.Equal(90250178, esi.AskedFor);                              // the picked one, not the first
    }

    [AvaloniaFact]
    public async Task NoSelectableCharacter_SaysSo_InsteadOfADeadEndWindow()
    {
        // Before: a picker whose every row is disabled — Choose does nothing and only Cancel closes it.
        var (shell, dialogs, esi, instance) = await ShellAsync(Pilot("Catbank", 90250178, canRead: false));
        using var _ = instance;

        await shell.ImportFittingsCommand.ExecuteAsync(null);

        Assert.Null(dialogs.LastPrompt);
        Assert.Null(esi.AskedFor);
        Assert.Contains("sign in", shell.FittingsStatus, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("Import cancelled.", shell.FittingsStatus);
    }
}
