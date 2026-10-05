using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.LocalApi.Dtos;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.LocalApi.Widgets;

/// <summary>
/// Saved widgets, one JSON settings row each (<c>localapi.widget.&lt;id&gt;</c>), plus the built-in presets from code.
/// The only writer of those keys: the Local API endpoints and the manager both go through here, so
/// <see cref="Changed"/> is the one signal an open widget page listens to. Built-in presets are read-only; a
/// customized preset is saved through <see cref="CreateAsync"/> as a copy with a new id.
/// </summary>
public sealed partial class WidgetStore(IServiceProvider services) : ISingletonService
{
    public const string SettingKeyPrefix = "localapi.widget.";

    // ponytail: one settings row per widget caps a config at the ClientSetting value width (4000); a module with its
    // own table if a preset ever needs more than a few dozen fields/options.
    private const int MaxStoredLength = 4000;
    private const int MaxNameLength = 64;
    private const int MaxEntries = 32;
    private const int MaxOptionValueLength = 64;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Raised after a saved widget was created, changed or deleted.</summary>
    public event Action<WidgetConfigChangedDto>? Changed;

    /// <summary>The built-in presets first, then the saved widgets.</summary>
    public async Task<IReadOnlyList<WidgetConfig>> ListAsync(CancellationToken cancellationToken = default) =>
        [.. WidgetPresets.All, .. await _ListSavedAsync(cancellationToken)];

    public async Task<WidgetConfig?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        WidgetPresets.Find(id) ?? (await _ListSavedAsync(cancellationToken)).FirstOrDefault(widget => widget.Id == id);

    /// <summary>Saves <paramref name="widget"/> as a new widget under a fresh id, whatever id it carried.</summary>
    public Task<Result<WidgetConfig>> CreateAsync(WidgetConfig widget, CancellationToken cancellationToken = default) =>
        _SaveAsync(widget with { Id = Guid.NewGuid().ToString("N") }, WidgetChangeKind.Created, cancellationToken);

    public async Task<Result<WidgetConfig>> UpdateAsync(WidgetConfig widget, CancellationToken cancellationToken = default)
    {
        if (WidgetPresets.IsBuiltIn(widget.Id))
            return Result<WidgetConfig>.Failure(_PresetReadOnly());
        if (await GetAsync(widget.Id, cancellationToken) is null)
            return Result<WidgetConfig>.Failure(_NotFound(widget.Id));

        return await _SaveAsync(widget, WidgetChangeKind.Updated, cancellationToken);
    }

    public async Task<Result> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        if (WidgetPresets.IsBuiltIn(id))
            return Result.Failure(_PresetReadOnly());
        if (await GetAsync(id, cancellationToken) is null)
            return Result.Failure(_NotFound(id));

        using var scope = services.CreateScope();
        var deleted = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send(new DeleteSettingCommand(SettingKeyPrefix + id), cancellationToken);
        if (deleted.IsSuccess)
            Changed?.Invoke(new WidgetConfigChangedDto(id, WidgetChangeKind.Deleted, null));
        return deleted;
    }

    private async Task<Result<WidgetConfig>> _SaveAsync(WidgetConfig widget, WidgetChangeKind change,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(widget, JsonOptions);
        if (_Validate(widget, json) is { } error)
            return Result<WidgetConfig>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed, error, "Widgets"));

        using var scope = services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send(new SetSettingCommand(SettingKeyPrefix + widget.Id, json), cancellationToken);
        if (!saved.IsSuccess)
            return Result<WidgetConfig>.Failure([.. saved.Messages]);

        Changed?.Invoke(new WidgetConfigChangedDto(widget.Id, change, widget));
        return Result<WidgetConfig>.Success(widget);
    }

    private async Task<IReadOnlyList<WidgetConfig>> _ListSavedAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Query(new GetSettingsQuery(), cancellationToken);
        return
        [
            .. settings
                .Where(setting => setting.Key.StartsWith(SettingKeyPrefix, StringComparison.Ordinal))
                .Select(setting => _Deserialize(setting.Value))
                .OfType<WidgetConfig>()
                .OrderBy(widget => widget.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }

    private static WidgetConfig? _Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<WidgetConfig>(json, JsonOptions); }
        catch (JsonException) { return null; } // a row this version cannot read stays out of the list instead of breaking it
    }

    private static string? _Validate(WidgetConfig widget, string json)
    {
        // A JSON body can set any of these to null, whatever the property types say.
        if (widget.Accent is null || widget.CharacterIds is null || widget.Fields is null || widget.Options is null)
            return "Accent, characterIds, fields and options cannot be null.";
        if (!Enum.IsDefined(widget.Preset)) return "Unknown preset.";
        if (string.IsNullOrWhiteSpace(widget.Name) || widget.Name.Length > MaxNameLength)
            return $"Name is required and at most {MaxNameLength} characters.";
        if (!_AccentPattern().IsMatch(widget.Accent)) return "Accent must be a colour like #4fc3f7.";
        if (!Enum.IsDefined(widget.Theme) || !Enum.IsDefined(widget.Background)) return "Unknown theme or background.";
        if (widget.PanelOpacity is < 0 or > 100) return "Panel opacity must be between 0 and 100.";
        if (widget.Scale is < 25 or > 400) return "Scale must be between 25 and 400 percent.";
        if (widget.CharacterIds.Count > MaxEntries || widget.Fields.Count > MaxEntries || widget.Options.Count > MaxEntries)
            return $"At most {MaxEntries} characters, fields and options each.";
        if (!widget.Fields.All(field => field is not null && _KeyPattern().IsMatch(field))
            || !widget.Options.Keys.All(_KeyPattern().IsMatch))
            return "Field and option keys are letters, digits, '.' and '-', starting with a letter.";
        if (widget.Options.Values.Any(value => value is null || value.Length > MaxOptionValueLength))
            return $"Option values are at most {MaxOptionValueLength} characters.";
        if (json.Length > MaxStoredLength) return "The widget configuration is too large.";
        return null;
    }

    private static ResultMessage _PresetReadOnly() => new(MessageSeverity.Error, MessageCodes.PresetReadOnly,
        "Built-in presets cannot be changed; save a copy instead.", "Widgets");

    private static ResultMessage _NotFound(string id) =>
        new(MessageSeverity.Error, MessageCodes.NotFound, $"Widget '{id}' was not found.", "Widgets");

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex _AccentPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9.-]{0,39}$")]
    private static partial Regex _KeyPattern();
}
