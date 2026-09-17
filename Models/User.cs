using System.ComponentModel.DataAnnotations;

namespace QaDocBackend.Models;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Member = "Member";
}

public class User
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = Roles.Member;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

/// <summary>Internal row including secrets; never returned by the API.</summary>
public class UserRecord : User
{
    public string PasswordHash { get; set; } = string.Empty;
    public int TokenVersion { get; set; }
}

/// <summary>Minimal public view of a user, used by pickers.</summary>
public record UserOption(int UserId, string DisplayName, string Username);

public class LoginRequest
{
    [Required] public string Username { get; set; } = string.Empty;
    [Required] public string Password { get; set; } = string.Empty;
}

public record LoginResponse(string Token, DateTime ExpiresAt, User User);

public record AuthStatus(bool NeedsSetup);

public class CreateUserRequest
{
    [Required, StringLength(50, MinimumLength = 3)]
    [RegularExpression(@"^[A-Za-z0-9._-]+$", ErrorMessage = "Username may contain letters, digits, dot, dash and underscore only.")]
    public string Username { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 1)]
    public string DisplayName { get; set; } = string.Empty;

    [Required, StringLength(128, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters.")]
    public string Password { get; set; } = string.Empty;

    [AllowedValues(Roles.Admin, Roles.Member, ErrorMessage = "Role must be Admin or Member.")]
    public string Role { get; set; } = Roles.Member;
}

public class UpdateUserRequest
{
    [Required, StringLength(100, MinimumLength = 1)]
    public string DisplayName { get; set; } = string.Empty;

    [AllowedValues(Roles.Admin, Roles.Member, ErrorMessage = "Role must be Admin or Member.")]
    public string Role { get; set; } = Roles.Member;

    public bool IsActive { get; set; } = true;
}

public class ResetPasswordRequest
{
    [Required, StringLength(128, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters.")]
    public string NewPassword { get; set; } = string.Empty;
}

public class ChangePasswordRequest
{
    [Required] public string CurrentPassword { get; set; } = string.Empty;

    [Required, StringLength(128, MinimumLength = 8, ErrorMessage = "New password must be at least 8 characters.")]
    public string NewPassword { get; set; } = string.Empty;
}
