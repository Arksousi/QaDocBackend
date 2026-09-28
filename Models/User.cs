using System.ComponentModel.DataAnnotations;

namespace QaDocBackend.Models;

/// <summary>
/// What someone is across the whole app. Admin manages users and deletes projects and tickets.
/// Leader may create projects and sees the Leader Dashboard for the projects they belong to, but gets
/// nothing extra inside a project. Developer and Tester are the same to the API. What anyone but
/// an Admin may do on a given project comes from <see cref="ProjectRoles"/>.
/// </summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Leader = "Leader";
    public const string Developer = "Developer";
    public const string Tester = "Tester";

    /// <summary>For [Authorize(Roles = ...)]: who may create projects and open the Leader Dashboard.</summary>
    public const string Leads = Admin + "," + Leader;

    public const string Message = "Role must be Admin, Leader, Developer or Tester.";
}

public class User
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = Roles.Tester;
    public bool IsActive { get; set; } = true;
    /// <summary>True only for the invented guest of a "Continue as a guest" session; never stored.</summary>
    public bool IsGuest { get; set; }
    /// <summary>How many unfinished tickets they should hold at once, across all projects; null for no limit.</summary>
    public int? TicketLimit { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Internal row including secrets; never returned by the API.</summary>
public class UserRecord : User
{
    public string PasswordHash { get; set; } = string.Empty;
    public int TokenVersion { get; set; }
}

/// <summary>
/// Minimal public view of a user, used by pickers. Carries their load so "Assigned To" can warn
/// before handing a ticket to someone already at their limit.
/// </summary>
public record UserOption(int UserId, string DisplayName, string Username, int OpenTickets = 0, int? TicketLimit = null);

public class LoginRequest
{
    [Required] public string Username { get; set; } = string.Empty;
    [Required] public string Password { get; set; } = string.Empty;
}

/// <summary>One row of the Users Dashboard: an active person's load against their limit.</summary>
public record UserWorkload(int UserId, string DisplayName, string Username, string Role, int OpenTickets, int? TicketLimit);

/// <summary>Body for changing only someone's ticket limit, from the Users Dashboard.</summary>
public class SetTicketLimitRequest
{
    [Range(1, 100, ErrorMessage = "Ticket limit must be between 1 and 100, or left empty.")]
    public int? TicketLimit { get; set; }
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

    [AllowedValues(Roles.Admin, Roles.Leader, Roles.Developer, Roles.Tester, ErrorMessage = Roles.Message)]
    public string Role { get; set; } = Roles.Tester;

    /// <summary>Unfinished tickets they should hold at once, across all projects; null for no limit.</summary>
    [Range(1, 100, ErrorMessage = "Ticket limit must be between 1 and 100, or left empty.")]
    public int? TicketLimit { get; set; }
}

public class UpdateUserRequest
{
    [Required, StringLength(100, MinimumLength = 1)]
    public string DisplayName { get; set; } = string.Empty;

    [AllowedValues(Roles.Admin, Roles.Leader, Roles.Developer, Roles.Tester, ErrorMessage = Roles.Message)]
    public string Role { get; set; } = Roles.Tester;

    [Range(1, 100, ErrorMessage = "Ticket limit must be between 1 and 100, or left empty.")]
    public int? TicketLimit { get; set; }

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
