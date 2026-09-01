using System.Text.RegularExpressions;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

internal sealed partial class LinkRunToGroupCodeCommandHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : ICommandHandler<LinkRunToGroupCodeCommand, Result>
{
    public async Task<Result> Handle(LinkRunToGroupCodeCommand command, CancellationToken cancellationToken = default)
    {
        if (!GroupCodePattern().IsMatch(command.GroupCode))
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "A group code must use the format HF-7QK2.", "Runs"));

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Run? run = await db.Set<Run>().FirstOrDefaultAsync(candidate => candidate.Id == command.RunId, cancellationToken);
        if (run is null)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "The run no longer exists.", "Runs"));

        run.GroupCode = command.GroupCode;
        run.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    [GeneratedRegex("^[A-Z]{2}-[A-Z0-9]{4}$")]
    private static partial Regex GroupCodePattern();
}
