using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Calendar;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Week-start setting (ET-297): the default follows the system culture, Apply persists only a real override and
/// removes the override once the choice matches the default again (so a later culture change is still followed),
/// and Changed fires exactly once per real change.
/// </summary>
public class WeekStartServiceTests
{
    [Fact]
    public void SystemDefault_NlNL_IsMonday()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
        try
        {
            Assert.Equal(DayOfWeek.Monday, WeekStartService.SystemDefault());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void SystemDefault_EnUS_IsSunday()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            Assert.Equal(DayOfWeek.Sunday, WeekStartService.SystemDefault());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [AvaloniaFact]
    public async Task FreshStore_NlNL_DefaultsToMonday_WithNoSettingRow()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
        try
        {
            using var instance = TestClientInstance.Create();
            var settings = instance.Services.GetRequiredService<ISettingRepository>();
            var weekStart = new WeekStartService(settings);
            await weekStart.InitializeAsync();

            Assert.Equal(DayOfWeek.Monday, weekStart.FirstDay);
            Assert.DoesNotContain((await settings.ListAsync()), s => s.Key == WeekStartService.SettingKey);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [AvaloniaFact]
    public async Task Apply_NonDefault_Persists_Then_BackToDefault_RemovesTheOverride()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL"); // default = Monday
        try
        {
            using var instance = TestClientInstance.Create();
            var settings = instance.Services.GetRequiredService<ISettingRepository>();
            var weekStart = new WeekStartService(settings);
            await weekStart.InitializeAsync();

            weekStart.Apply(DayOfWeek.Sunday);
            Assert.Equal("sunday", await WaitForSettingValue(settings, WeekStartService.SettingKey));

            weekStart.Apply(DayOfWeek.Monday); // back to the nl-NL default
            await WaitForSettingRemoved(settings, WeekStartService.SettingKey);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [AvaloniaFact]
    public void Changed_FiresExactlyOnce_OnlyOnARealChange()
    {
        using var instance = TestClientInstance.Create();
        var settings = instance.Services.GetRequiredService<ISettingRepository>();
        var weekStart = new WeekStartService(settings);

        var fireCount = 0;
        DayOfWeek? lastValue = null;
        weekStart.Changed += d => { fireCount++; lastValue = d; };

        weekStart.Apply(weekStart.FirstDay); // no-op: identical value
        Assert.Equal(0, fireCount);

        var changedTo = weekStart.FirstDay == DayOfWeek.Sunday ? DayOfWeek.Monday : DayOfWeek.Sunday;
        weekStart.Apply(changedTo);
        Assert.Equal(1, fireCount);
        Assert.Equal(changedTo, lastValue);

        weekStart.Apply(changedTo); // no-op again: already applied
        Assert.Equal(1, fireCount);
    }

    private static async Task<string?> WaitForSettingValue(ISettingRepository settings, string key)
    {
        string? value = null;
        for (var i = 0; i < 50 && value is null; i++)
        {
            value = (await settings.ListAsync()).FirstOrDefault(s => s.Key == key)?.Value;
            if (value is null) await Task.Delay(20);
        }
        return value;
    }

    private static async Task WaitForSettingRemoved(ISettingRepository settings, string key)
    {
        for (var i = 0; i < 50; i++)
        {
            if (!(await settings.ListAsync()).Any(s => s.Key == key)) return;
            await Task.Delay(20);
        }
        Assert.DoesNotContain((await settings.ListAsync()), s => s.Key == key);
    }
}
