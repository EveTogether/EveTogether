using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// One-time repair, originally for ET-228: the run window used to discard a homefront's dungeon id outright
/// (<c>ActivityWindowViewModel</c> always wrote <c>SiteTypeId: 0</c>), so every homefront run started before that fix
/// reads back as an ordinary site. ET-275 widened it from homefronts alone to every archetype: a run started before
/// <c>Run.SignatureGroupSnapshot</c> existed (ET-226) has the same 0, whatever kind of site it was — "Sansha Refuge",
/// "Desolate Site" and the rest, not only a homefront's 24 names. Run at startup like
/// <c>RebuildActivitySummariesCommand</c>'s own OnlyWhenOutdated check.
///
/// Matches a run's own <c>SiteName</c> against the SDE's whole site catalogue, read live
/// (<c>ISdeAccessor.SearchSites()</c>) rather than hardcoded here — CCP content the installed SDE build is the one
/// source for. Only a name with exactly one dungeon in the whole catalogue repairs a run: two dungeons sharing a
/// name (domain/homefronts.md measured 218 of 1014 English names doing exactly that) is proof of nothing about which
/// one this run was, so nothing is guessed and the run is left untouched — <see cref="RunTypeResolver"/>'s own
/// ET-275 archetype fallback is what such a name gets instead, without ever needing a dungeon id. A run whose id is
/// already correct, or whose name matches nothing at all, is likewise left untouched. A repaired run that was
/// already published is marked <see cref="Enums.RunSyncState.Outdated"/> (ET-215's own rule) so the correction
/// reaches the server too, the next time the pilot publishes.
///
/// ET-261: also repairs a run recorded as <see cref="Enums.SiteTypeSource.Uncatalogued"/> — started before the
/// catalogue (or the SDE build behind it) carried the site at all — and sets its source to
/// <see cref="Enums.SiteTypeSource.Site"/> once an exact name proves it. Must run again once the SDE becomes
/// available after a schema-version import (<c>MainWindowViewModel.RunSdeImportPopupAsync</c>): the startup call
/// below returns 0 outright while <c>ISdeAccessor.IsAvailable</c> is still false, which it is for the whole first
/// run after a bump — repairing nothing until the pilot restarts a second time otherwise.
/// </summary>
public sealed record RepairSiteTypeIdsCommand : ICommand<Result<int>>;
