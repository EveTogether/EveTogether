using EveUtils.Shared.Modules.Settings.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Settings;

/// <summary>
/// Client-only module (local app settings). The entity lives in <c>Shared</c> because the EF model must be
/// reachable from the migration plumbing; only the <c>ClientDbContext</c> applies this config. Handlers and
/// repository are auto-registered via <c>AddSharedServices</c>, so this module no longer has a
/// DI registration method — only <see cref="ConfigureModel"/>.
/// </summary>
public static class SettingsModule
{
    public static void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfiguration(new ClientSettingConfiguration());
}
