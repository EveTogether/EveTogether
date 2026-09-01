using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Clipboard;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class AbyssalLootCaptureTests
{
    [AvaloniaFact]
    public async Task InventoryWithKnownEveTypes_OffersLoot_AndSuppressesAnOpenDuplicate()
    {
        using var env = await Env.StartAsync();
        const string text = "Rifter\t1\r\nDamage Control II\t2";

        env.Copy(text);
        env.Copy(text);

        var offer = Assert.Single(env.Toasts.ActionToasts);
        Assert.Equal("Loot copied", offer.Title);
        Assert.Contains("2 EVE item type(s)", offer.Message);
        Assert.Equal(["Close"], Array.ConvertAll(offer.Actions.ToArray(), action => action.Label));

        offer.Actions[0].Run();
        env.Copy(text);
        Assert.Equal(2, env.Toasts.ActionToasts.Count);
    }

    [Theory]
    [InlineData("Budget rent\t1200\r\nCloud storage\t80")]
    [InlineData("Product name\t19\r\nAnnual subscription\t12")]
    [InlineData("Alice\t1\r\nBob\t2")]
    public async Task InventoryWithoutEveTypes_ExplainsWhyItIsRejected(string text)
    {
        using var env = await Env.StartAsync();

        env.Copy(text);

        Assert.Empty(env.Toasts.ActionToasts);
        var rejection = Assert.Single(env.Toasts.Toasts);
        Assert.Equal("Loot not recognised", rejection.Title);
        Assert.Contains("None of the 2 copied names is a known item type", rejection.Message);
    }

    [AvaloniaFact]
    public async Task InventoryWithOneKnownType_OffersLootAndNamesUnresolvedRows()
    {
        using var env = await Env.StartAsync();

        env.Copy("Rifter\t1\r\nBudget rent\t2");

        var offer = Assert.Single(env.Toasts.ActionToasts);
        Assert.Contains("1 EVE item type(s)", offer.Message);
        Assert.Contains("1 name(s) were not recognised", offer.Message);
    }

    [AvaloniaFact]
    public async Task FitCapture_IsNotOfferedAsLoot()
    {
        using var env = await Env.StartAsync();

        env.Copy("[Rifter, Solo]\r\nDamage Control II");

        Assert.Empty(env.Toasts.ActionToasts);
    }

    private sealed class Env : IDisposable
    {
        private readonly TestClientInstance _instance;
        private readonly ClipboardWatchService _watch;
        private readonly AbyssalLootCapture _capture;
        private readonly FakeClipboardChangeSource _source;

        public RecordingToastService Toasts { get; } = new();

        private Env(TestClientInstance instance, ClipboardWatchService watch, FakeClipboardChangeSource source)
        {
            _instance = instance;
            _watch = watch;
            _source = source;
            _capture = new AbyssalLootCapture(watch, Toasts, FakeSdeAccessor.WithSampleFit());
        }

        public static async Task<Env> StartAsync()
        {
            var source = new FakeClipboardChangeSource();
            var instance = TestClientInstance.Create();
            var watch = new ClipboardWatchService(new RecordingDialogService(), instance.Services,
                NullLogger<ClipboardWatchService>.Instance, source);
            var env = new Env(instance, watch, source);
            await watch.SetEnabledAsync(true);
            return env;
        }

        public void Copy(string text)
        {
            _source.ClipboardText = text;
            _source.RaiseChanged();
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            _capture.Dispose();
            _watch.Dispose();
            _instance.Dispose();
        }
    }

    private sealed class FakeClipboardChangeSource : IClipboardChangeSource
    {
        public string? ClipboardText { get; set; }

        public bool IsSupported => true;

        public event Action? Changed;

        public event Action? SupportChanged
        {
            add { }
            remove { }
        }

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }

        public Task<string?> ReadTextAsync() => Task.FromResult(ClipboardText);

        public void RaiseChanged() => Changed?.Invoke();
    }
}
