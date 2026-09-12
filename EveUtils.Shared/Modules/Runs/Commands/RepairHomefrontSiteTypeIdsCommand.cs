using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// One-time repair for ET-228: the run window used to discard a homefront's dungeon id outright
/// (<c>ActivityWindowViewModel</c> always wrote <c>SiteTypeId: 0</c>), so every homefront run started before that fix
/// reads back as an ordinary site. Run at startup like <c>RebuildActivitySummariesCommand</c>'s own OnlyWhenOutdated
/// check.
///
/// Matches a run's own <c>SiteName</c> against the SDE's archetype-70 site names, read live
/// (<c>ISdeAccessor.SearchSites(archetypeId: 70)</c>) rather than hardcoded here — the 24 names are CCP content the
/// installed SDE build is the one source for, and domain/homefronts.md measured all 24 unique across the whole
/// catalogue. Only an exact name repairs a run; nothing is guessed, and a run whose id is already correct, or whose
/// name matches nothing, is left untouched. A repaired run that was already published is marked
/// <see cref="Enums.RunSyncState.Outdated"/> (ET-215's own rule) so the correction reaches the server too, the next
/// time the pilot publishes.
///
/// ET-261: also repairs a run recorded as <see cref="Enums.SiteTypeSource.Uncatalogued"/> — started before the
/// catalogue (or the SDE build behind it) carried the site at all — and sets its source to
/// <see cref="Enums.SiteTypeSource.Site"/> once an exact name proves it. Must run again once the SDE becomes
/// available after a schema-version import (<c>MainWindowViewModel.RunSdeImportPopupAsync</c>): the startup call
/// below returns 0 outright while <c>ISdeAccessor.IsAvailable</c> is still false, which it is for the whole first
/// run after a bump — repairing nothing until the pilot restarts a second time otherwise.
/// </summary>
public sealed record RepairHomefrontSiteTypeIdsCommand : ICommand<Result<int>>;
