namespace EveUtils.Shared.Cqrs.Permissions;

/// <summary>
/// Declares the app-permission (capability) a command/query/event requires. The code follows
/// the <c>module.action</c> convention (e.g. <c>gamelog.view</c>). No attribute = no app-permission
/// required. Read by the dispatcher gate (and the event bus before remote delivery).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class RequiresPermissionAttribute(string code) : Attribute
{
    public string Code { get; } = code;
}
