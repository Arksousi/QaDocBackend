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

    // The profile they fill in themselves (PUT api/profile). All optional.
    public string? Email { get; set; }
    public string? JobTitle { get; set; }
    public string? Phone { get; set; }
    public string? Bio { get; set; }
    /// <summary>0 while they have no picture; goes up each time it changes, so a cached copy can be trusted until it does.</summary>
    public int AvatarVersion { get; set; }
}

/// <summary>Body for editing your own profile. Username and role stay with the Admin.</summary>
public class UpdateProfileRequest
{
    [Required, StringLength(100, MinimumLength = 1)]
    public string DisplayName { get; set; } = string.Empty;

    [EmailAddress(ErrorMessage = "Enter a valid email address, or leave it empty.")]
    [StringLength(254)]
    public string? Email { get; set; }

    [StringLength(100)]
    public string? JobTitle { get; set; }

    [StringLength(40)]
    [RegularExpression(@"^[0-9+()\-.\s]*$", ErrorMessage = "A phone number may contain digits, spaces and + ( ) - . only.")]
    public string? Phone { get; set; }

    [StringLength(500)]
    public string? Bio { get; set; }
}

/// <summary>Someone who has a picture, and which version of it is current.</summary>
public record AvatarVersion(int UserId, int Version);

/// <summary>A stored picture, as served.</summary>
public record AvatarContent(string ContentType, byte[] Content);

/// <summary>What a profile picture may be. The app crops and shrinks it to 256px before sending.</summary>
public static class Avatars
{
    public const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>
    /// The image type from the file's own first bytes, or null when it is not PNG, JPEG or WebP.
    /// Checked from the bytes, not the declared content type, which the client chooses.
    /// </summary>
    public static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }
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

/// <summary>
/// What an Admin or Leader sees when hovering someone's avatar: who they are, how loaded they are,
/// and where they work. Ticket counts cover every real project.
/// </summary>
public record UserCard(
    int UserId, string DisplayName, string Username, string Role, bool IsActive, DateTime CreatedAt,
    int? TicketLimit, int OpenTickets, int ClosedTickets, int TotalAssigned, List<UserCardProject> Projects,
    string? JobTitle = null, string? Email = null, int AvatarVersion = 0);

/// <summary>A project on the card, with the person's access there.</summary>
public record UserCardProject(int ProjectId, string ProjectCode, string ProjectName, string Role);

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
