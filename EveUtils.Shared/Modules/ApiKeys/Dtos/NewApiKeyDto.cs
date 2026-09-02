namespace EveUtils.Shared.Modules.ApiKeys.Dtos;

/// <summary>The one and only hand-over of a freshly created key's plaintext; it is not stored anywhere.</summary>
public sealed record NewApiKeyDto(int Id, string Prefix, string PlainText);
