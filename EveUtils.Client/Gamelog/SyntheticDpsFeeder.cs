using System;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.Gamelog;

/// <summary>
/// Deterministic DPS feeder (no RNG) for testing the stream without a running EVE client. Drives
/// <c>GamelogClientService.AddHitAsync</c> on a fixed ramp, so each hit is recorded (owner-
/// stamped) and a CombatLoggedEvent is published (Both) — feeding the local DPS view and, once
/// attached, the server + the SignalR page.
/// </summary>
public sealed class SyntheticDpsFeeder(GamelogClientService gamelog) : ISingletonService
{
    private static readonly int[] Pattern = [120, 180, 240, 300, 260, 200, 140, 90, 60, 40];

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var index = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var amount = Pattern[index % Pattern.Length];
            var direction = index % 3 == 0 ? DamageDirection.Incoming : DamageDirection.Outgoing;
            await gamelog.AddHitAsync(direction, amount, "Guristas Scout", cancellationToken);
            index++;

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
