using System.Reflection;
using System.Security.Cryptography;
using EveUtils.Shared.Modules.ApiKeys.Services;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-118: the key format and the comparison that guards it. The constant-time check is the one thing here that
/// cannot be proven by behaviour — an ordinary string comparison returns exactly the same answers, only sooner
/// on a mismatch — so it is proven by what the compiled method actually calls.
/// </summary>
public class ApiKeySecurityTests
{
    [Fact]
    public void Generate_ProducesTheDocumentedFormat()
    {
        GeneratedApiKey generated = ApiKeySecurity.Generate();

        Assert.Equal($"evek_{generated.Prefix}_{generated.Secret}", generated.PlainText);
        Assert.True(ApiKeySecurity.TryParse(generated.PlainText, out var prefix, out var secret));
        Assert.Equal(generated.Prefix, prefix);
        Assert.Equal(generated.Secret, secret);
    }

    /// <summary>The prefix must not carry the separator, or a key splits into the wrong two halves.</summary>
    [Fact]
    public void Generate_PrefixNeverContainsTheSeparator()
    {
        for (var i = 0; i < 200; i++)
            Assert.DoesNotContain('_', ApiKeySecurity.Generate().Prefix);
    }

    [Fact]
    public void Generate_ProducesADifferentKeyEveryTime()
    {
        var keys = Enumerable.Range(0, 100).Select(_ => ApiKeySecurity.Generate().PlainText).ToHashSet();

        Assert.Equal(100, keys.Count);
    }

    [Fact]
    public void Verify_AcceptsTheSecretItHashed_AndRejectsAnyOther()
    {
        GeneratedApiKey generated = ApiKeySecurity.Generate();
        var hash = ApiKeySecurity.Hash(generated.Secret);

        Assert.True(ApiKeySecurity.Verify(generated.Secret, hash));
        Assert.False(ApiKeySecurity.Verify(ApiKeySecurity.Generate().Secret, hash));
        Assert.False(ApiKeySecurity.Verify(generated.Secret, string.Empty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nope")]
    [InlineData("evek_onlyprefix")]
    [InlineData("wrong_prefix_secret")]
    [InlineData("evek__secret")]
    [InlineData("evek_prefix_")]
    public void TryParse_RejectsAnythingThatIsNotThisKeyFormat(string? presented)
    {
        Assert.False(ApiKeySecurity.TryParse(presented, out _, out _));
    }

    /// <summary>The secret is base64url and may contain the separator itself; only the first two are separators.</summary>
    [Fact]
    public void TryParse_KeepsASecretThatContainsTheSeparator()
    {
        Assert.True(ApiKeySecurity.TryParse("evek_ab12cd34_se_cr_et", out var prefix, out var secret));

        Assert.Equal("ab12cd34", prefix);
        Assert.Equal("se_cr_et", secret);
    }

    /// <summary>
    /// Replacing <c>FixedTimeEquals</c> with a plain string comparison turns this red: the call disappears from
    /// the compiled method. Nothing about the returned values would have changed.
    /// </summary>
    [Fact]
    public void Verify_ComparesWithFixedTimeEquals()
    {
        IReadOnlyList<MethodBase> called = _CalledMethods(typeof(ApiKeySecurity), nameof(ApiKeySecurity.Verify));

        Assert.Contains(called, method =>
            method.DeclaringType == typeof(CryptographicOperations) &&
            method.Name == nameof(CryptographicOperations.FixedTimeEquals));
    }

    /// <summary>Every method the compiled body calls, read from its IL: opcode 0x28 (call) / 0x6F (callvirt)
    /// followed by a four-byte metadata token.</summary>
    private static IReadOnlyList<MethodBase> _CalledMethods(Type type, string methodName)
    {
        MethodInfo method = type.GetMethod(methodName)
            ?? throw new InvalidOperationException($"{type.Name}.{methodName} does not exist.");
        MethodBody body = method.GetMethodBody()
            ?? throw new InvalidOperationException($"{methodName} has no IL body.");
        var il = body.GetILAsByteArray()
            ?? throw new InvalidOperationException($"{methodName} has no readable IL.");

        Module module = type.Module;
        List<MethodBase> called = [];
        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] is not (0x28 or 0x6F))
                continue;

            try
            {
                MethodBase? resolved = module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                if (resolved is not null)
                    called.Add(resolved);
            }
            catch (ArgumentException)
            {
                // Not a method token — the byte was operand data that happened to look like a call opcode.
            }
        }

        return called;
    }
}
