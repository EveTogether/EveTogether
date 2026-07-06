namespace EveUtils.Shared.Modules.AdminAuth.Services;

/// <summary>Admin-password policy: minimum 8 characters.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;

    public const string Requirement = "Password must be at least 8 characters.";

    public static bool IsValid(string? password) =>
        !string.IsNullOrEmpty(password) && password.Length >= MinLength;
}
