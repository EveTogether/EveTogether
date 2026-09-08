using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>Every run that is running right now, one per character — what the RUNNING band needs (ET-203) to fill a
/// lane per local character, and a question <see cref="GetRunningRunQuery"/> cannot answer for it: that query (and
/// the <c>RunningRunLookup</c> it asks) is deliberately "exactly one for the whole app, or nothing", built for a
/// window that must adopt the one stored run or none — and it answers nothing at all the moment a second character
/// is running, or a stopped-and-never-saved run is still open. One lane going quiet then made every lane say
/// "nothing running", including the one that plainly was. A band with a lane per character asks a different
/// question — which run is running for THIS character — so it reads the table directly instead of stretching a
/// lookup built, and already burned once, for a narrower job.</summary>
public sealed record GetRunningRunsQuery : IQuery<Result<IReadOnlyList<RunningRunDto>>>;
