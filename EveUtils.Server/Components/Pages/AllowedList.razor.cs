using EveUtils.Server.Auth;
using EveUtils.Server.Permissions;
using EveUtils.Shared.Modules.Permissions.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using Microsoft.AspNetCore.Components;

namespace EveUtils.Server.Components.Pages;

public partial class AllowedList : ComponentBase
{
    [Inject] private IServerAuthRepository Repository { get; set; } = default!;
    [Inject] private IPermissionToggleStore Toggles { get; set; } = default!;

    private IReadOnlyList<AllowedCharacter> Items { get; set; } = [];
    private string NewName { get; set; } = string.Empty;

    /// <summary>When true (default) pairing is restricted to the allowed-list; false = public-server mode.</summary>
    private bool AllowedListEnabled
    {
        get => Toggles.IsEnabled(ServerToggles.AllowedListEnabled);
        set => Toggles.SetEnabled(ServerToggles.AllowedListEnabled, value);
    }

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync() => Items = await Repository.ListAllowedAsync();

    private async Task AddAsync()
    {
        if (string.IsNullOrWhiteSpace(NewName))
            return;

        await Repository.AddAllowedAsync(new AllowedCharacter { CharacterName = NewName.Trim(), Note = "panel" });
        NewName = string.Empty;
        await LoadAsync();
    }

    private async Task RemoveAsync(int id)
    {
        await Repository.RemoveAllowedAsync(id);
        await LoadAsync();
    }
}
