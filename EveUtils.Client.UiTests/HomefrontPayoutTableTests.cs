using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Enums;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-231's curve table, tested directly against Jithran's own counterproof and the acceptance criteria — no store,
/// no dispatcher, since the table is pure data plus a lookup.
/// </summary>
public sealed class HomefrontPayoutTableTests
{
    // Any moment on or after 23.02 (19 Mar 2026), where the only version in the table applies.
    private static readonly DateTime AfterMar19 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BeforeMar19 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── AC-1: Jithran's counterproof, exact to the ISK ──────────────────────────────────────────────

    [Theory]
    [InlineData("Raid", 5, 15_000_000)]
    [InlineData("Raid", 4, 12_000_000)]
    [InlineData("Raid", 6, 11_250_000)]
    [InlineData("Metaliminal Meteoroid", 5, 12_000_000)]
    public void TryGetExpected_MatchesJithransNumbers(string kind, int n, decimal expectedIsk)
    {
        (decimal Amount, string Version, DateTime EffectiveFromUtc)? result =
            HomefrontPayoutTable.TryGetExpected(kind, inSiteAtCompletion: true, n, HomefrontOutcome.Completed,
                completedWaveCount: null, AfterMar19);

        Assert.Equal(expectedIsk, result?.Amount);
    }

    // Every other combat kind on the 5-person curve shares Raid's own table (domain/homefronts.md §3.2).
    [Theory]
    [InlineData("Dread Assault")]
    [InlineData("Emergency Aid")]
    [InlineData("Suspicious Signal")]
    public void TryGetExpected_OtherFivePersonKinds_ShareRaidsCurve(string kind)
    {
        (decimal Amount, string Version, DateTime EffectiveFromUtc)? result =
            HomefrontPayoutTable.TryGetExpected(kind, true, 5, HomefrontOutcome.Completed, null, AfterMar19);

        Assert.Equal(15_000_000m, result?.Amount);
    }

    [Fact]
    public void TryGetExpected_ThreePersonCurve_MatchesTheTable()
    {
        (decimal Amount, string Version, DateTime EffectiveFromUtc)? result =
            HomefrontPayoutTable.TryGetExpected("Salvage Research", true, 3, HomefrontOutcome.Completed, null, AfterMar19);

        Assert.Equal(12_000_000m, result?.Amount);
    }

    // ── AC-2: the ET-230 example — the hauler outside the site gets nothing ─────────────────────────

    [Fact]
    public void TryGetExpected_NotInSite_IsNull()
    {
        (decimal Amount, string Version, DateTime EffectiveFromUtc)? result =
            HomefrontPayoutTable.TryGetExpected("Raid", inSiteAtCompletion: false, 5, HomefrontOutcome.Completed, null, AfterMar19);

        Assert.Null(result);
    }

    [Fact]
    public void TryGetExpected_InSiteAtCompletionNull_IsNull()
    {
        // Nobody decided this character's tick yet — not the same as "not in site".
        (decimal Amount, string Version, DateTime EffectiveFromUtc)? result =
            HomefrontPayoutTable.TryGetExpected("Raid", inSiteAtCompletion: null, 5, HomefrontOutcome.Completed, null, AfterMar19);

        Assert.Null(result);
    }

    // ── AC-3: without "completed", no expected payout ───────────────────────────────────────────────

    [Theory]
    [InlineData(HomefrontOutcome.Failed)]
    [InlineData(HomefrontOutcome.Unknown)]
    [InlineData(null)]
    public void TryGetExpected_WithoutCompletedOutcome_IsNull(HomefrontOutcome? outcome)
    {
        (decimal Amount, string Version, DateTime EffectiveFromUtc)? result =
            HomefrontPayoutTable.TryGetExpected("Raid", true, 5, outcome, null, AfterMar19);

        Assert.Null(result);
    }

    // ── AC-4: a run before 19 Mar 2026 uses the table that applied then — today, none is known ─────

    [Fact]
    public void TryGetExpected_BeforeTheTablesEffectiveDate_IsNull()
    {
        // No historical table is measured (domain/homefronts.md §1 flags the 2023/2024 figures as unmeasured), so a
        // run from before 23.02 gets no expected figure at all rather than today's table applied to a site it never
        // priced. The same moment one day later resolves once the version applies.
        (decimal Amount, string Version, DateTime EffectiveFromUtc)? beforeResult =
            HomefrontPayoutTable.TryGetExpected("Raid", true, 5, HomefrontOutcome.Completed, null, BeforeMar19);
        (decimal Amount, string Version, DateTime EffectiveFromUtc)? afterResult =
            HomefrontPayoutTable.TryGetExpected("Raid", true, 5, HomefrontOutcome.Completed, null,
                new DateTime(2026, 3, 19, 0, 0, 0, DateTimeKind.Utc));

        Assert.Null(beforeResult);
        Assert.Equal(15_000_000m, afterResult?.Amount);
    }

    // ── N beyond the table: never a guess ────────────────────────────────────────────────────────────

    [Fact]
    public void TryGetExpected_NBeyondTheThreePersonTable_IsNull()
    {
        // The 3-person table stops at N = 6 (domain/homefronts.md §3.2: "—" for 7-10).
        (decimal Amount, string Version, DateTime EffectiveFromUtc)? result =
            HomefrontPayoutTable.TryGetExpected("Salvage Research", true, 7, HomefrontOutcome.Completed, null, AfterMar19);

        Assert.Null(result);
    }

    // ── AAR: paid per wave, no HomefrontOutcome of its own ──────────────────────────────────────────

    [Fact]
    public void TryGetExpected_Aar_SumsCompletedWaveBands_AtNFive()
    {
        // Waves 1-3 at 5,000,000 each = 15,000,000 (domain/homefronts.md §3.2, the N = 5 band rates).
        (decimal Amount, string Version, DateTime EffectiveFromUtc)? threeWaves =
            HomefrontPayoutTable.TryGetExpected("Abyssal Artifact Recovery", true, 5, null, completedWaveCount: 3, AfterMar19);
        // All 9 waves must equal the table's own 9-wave total at N = 5.
        (decimal Amount, string Version, DateTime EffectiveFromUtc)? nineWaves =
            HomefrontPayoutTable.TryGetExpected("Abyssal Artifact Recovery", true, 5, null, completedWaveCount: 9, AfterMar19);

        Assert.Equal(15_000_000m, threeWaves?.Amount);
        Assert.Equal(67_500_000m, nineWaves?.Amount);
    }

    [Fact]
    public void TryGetExpected_Aar_NoWavesPaid_IsNull()
    {
        (decimal Amount, string Version, DateTime EffectiveFromUtc)? result =
            HomefrontPayoutTable.TryGetExpected("Abyssal Artifact Recovery", true, 5, null, completedWaveCount: 0, AfterMar19);

        Assert.Null(result);
    }

    // ── No wallet data anywhere: the table takes no journal figure of any kind ───────────────────────

    [Fact]
    public void CurveFor_UnknownKind_IsNull() => Assert.Null(HomefrontPayoutTable.CurveFor("Not A Homefront"));
}
