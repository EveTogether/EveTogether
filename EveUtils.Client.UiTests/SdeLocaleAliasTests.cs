using System;
using System.IO;
using System.IO.Compression;
using EveUtils.Shared.Modules.Fittings.Services.Parsers;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Sde.Import;
using EveUtils.Shared.Modules.Sde.Storage;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// the SDE import stores a locale alias per non-English type name so an EFT-fit pasted with localized (German/
/// French/…) names resolves to the same typeId, while display/export stay English (Type.nameEn). A cross-locale name
/// collision resolves to the canonical English type, not an arbitrary row. Built end-to-end through the real
/// <see cref="SdeSqliteBuilder"/> + <see cref="SqliteSdeAccessor"/> (schema → importer alias-write → index → lookup).
/// </summary>
public sealed class SdeLocaleAliasTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sde-alias-{Guid.NewGuid():N}");

    private SqliteSdeAccessor BuildStore(params string[] typeJsonLines)
    {
        Directory.CreateDirectory(_dir);
        var zipPath = Path.Combine(_dir, "sde.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        using (var entry = new StreamWriter(zip.CreateEntry("types.jsonl").Open()))
            foreach (var line in typeJsonLines)
                entry.WriteLine(line);

        var dbPath = Path.Combine(_dir, "sde.db");
        new SdeSqliteBuilder().Build(zipPath, dbPath,
            new SdeVersion(1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            progress: null, TestContext.Current.CancellationToken);
        return new SqliteSdeAccessor(dbPath);
    }

    [Fact]
    public void Import_StoresLocaleAliases_SoLocalizedNamesResolveToTheSameType()
    {
        var sde = BuildStore(
            """{"_key":587,"groupID":25,"name":{"en":"Rifter","de":"Wieselflink","fr":"Rifter VF"},"published":true,"mass":1,"volume":1,"capacity":1}""");

        Assert.True(sde.TryGetTypeId("Rifter", out var en));
        Assert.True(sde.TryGetTypeId("Wieselflink", out var de));     // German name → same type
        Assert.True(sde.TryGetTypeId("rifter vf", out var fr));        // case-insensitive locale alias
        Assert.Equal(587, en);
        Assert.Equal(587, de);
        Assert.Equal(587, fr);

        // Display/export stay English regardless of the localized import.
        Assert.Equal("Rifter", sde.GetType(587)!.Name);
    }

    [Fact]
    public void Import_CrossLocaleCollision_PrefersTheCanonicalEnglishType()
    {
        // "Foo" is type 100's English name AND type 200's German alias → the canonical English match must win.
        var sde = BuildStore(
            """{"_key":100,"groupID":1,"name":{"en":"Foo"},"published":true,"mass":1,"volume":1,"capacity":1}""",
            """{"_key":200,"groupID":1,"name":{"en":"Bar","de":"Foo"},"published":true,"mass":1,"volume":1,"capacity":1}""");

        Assert.True(sde.TryGetTypeId("Foo", out var typeId));
        Assert.Equal(100, typeId);
        Assert.True(sde.TryGetTypeId("Bar", out var bar));
        Assert.Equal(200, bar);
    }

    [Fact]
    public void EftImport_ResolvesLocalizedNames_AndExportsEnglish()
    {
        // A German EFT-fit end-to-end: a localized ship header + a localized cargo item. Both resolve via the locale
        // alias through the real EFT path, and re-exporting yields the canonical English names (import is
        // locale-agnostic; display/export stay English).
        var sde = BuildStore(
            """{"_key":587,"groupID":25,"name":{"en":"Rifter","de":"Wieselflink"},"published":true,"mass":1,"volume":1,"capacity":1}""",
            """{"_key":28668,"groupID":314,"name":{"en":"Nanite Repair Paste","de":"Nanit-Reparaturpaste"},"published":true,"mass":1,"volume":1,"capacity":1}""");

        var result = new FitTextImporter(sde).Import("[Wieselflink, My Fit]\n\nNanit-Reparaturpaste x10");

        Assert.True(result.Success, result.Error);
        Assert.Equal(587, result.Fit!.ShipTypeId);
        Assert.Contains(result.Fit.Items, i => i is { TypeId: 28668, Flag: "Cargo", Quantity: 10 });

        var eft = new FitExporter(sde).ToEft(result.Fit);
        Assert.Contains("[Rifter, My Fit]", eft);          // ship header → English
        Assert.Contains("Nanite Repair Paste x10", eft);   // cargo item → English
        Assert.DoesNotContain("Wieselflink", eft);
        Assert.DoesNotContain("Nanit-Reparaturpaste", eft);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort cleanup of the throwaway store
        }
    }
}
