using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.ApiKeys.Commands;
using EveUtils.Shared.Modules.ApiKeys.Dtos;
using EveUtils.Shared.Modules.ApiKeys.Queries;
using EveUtils.Shared.Modules.ApiKeys.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace EveUtils.Server.Components.Pages;

public partial class ApiKeys : ComponentBase
{
    [Inject] private IDispatcher Dispatcher { get; set; } = default!;
    [CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }

    private IReadOnlyList<ApiKeyDto> _keys = [];
    private NewApiKeyDto? _created;
    private string _label = string.Empty;
    private DateTime? _expiresOn;
    private string? _error;

    /// <summary>What the key does right now, which "active" alone stops saying the moment an expiry passes.</summary>
    private static string Status(ApiKeyDto key) =>
        !key.IsActive ? "revoked" : key.ExpiresAt <= DateTimeOffset.UtcNow ? "expired" : "active";

    protected override async Task OnInitializedAsync() => await _LoadAsync();

    private async Task _LoadAsync() => _keys = await Dispatcher.Query(new ListApiKeysQuery());

    private async Task CreateAsync()
    {
        _error = null;
        var createdBy = AuthState is null ? "unknown" : (await AuthState).User.Identity?.Name ?? "unknown";

        // The picked date is the last day the key works, so it expires at the end of it rather than at its start.
        DateTimeOffset? expiresAt = _expiresOn is { } on ? new DateTimeOffset(on.Date.AddDays(1), TimeSpan.Zero) : null;

        Result<NewApiKeyDto> result = await Dispatcher.Send(
            new CreateApiKeyCommand(_label, ApiKeyScopes.All, createdBy, ExpiresAt: expiresAt));
        if (!result.IsSuccess || result.Value is not { } created)
        {
            _error = string.Join(" ", result.Messages.Select(m => m.Text));
            return;
        }

        _created = created;
        _label = string.Empty;
        _expiresOn = null;
        await _LoadAsync();
    }

    private void Dismiss() => _created = null;

    private async Task RevokeAsync(int id) => await _MutateAsync(new RevokeApiKeyCommand(id));

    private async Task DeleteAsync(int id) => await _MutateAsync(new DeleteApiKeyCommand(id));

    private async Task _MutateAsync(ICommand<Result> command)
    {
        Result result = await Dispatcher.Send(command);
        _error = result.IsSuccess ? null : string.Join(" ", result.Messages.Select(m => m.Text));
        await _LoadAsync();
    }
}
