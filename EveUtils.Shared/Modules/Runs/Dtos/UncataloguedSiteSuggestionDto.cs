namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <param name="SignatureGroup">The scanner's group text the most recent run of this name recorded ("Relic Site", …).</param>
public sealed record UncataloguedSiteSuggestionDto(string SiteName, string SignatureGroup);
