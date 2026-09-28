using System.ComponentModel.DataAnnotations;

namespace QaDocBackend.Models;

/// <summary>The roles a membership row can hold. Managing a project is not one of them; see <see cref="ProjectAccess.Manager"/>.</summary>
public static class ProjectRoles
{
    public const string Viewer = "Viewer";
    public const string Contributor = "Contributor";

    public static readonly string[] All = [Viewer, Contributor];

    public static bool IsValid(string? role) => All.Contains(role, StringComparer.Ordinal);
}

/// <summary>
/// What the current user may do in one project. Ordered, so a check is a comparison:
/// <c>access &gt;= ProjectAccess.Contributor</c>. Manager is never stored: it is what a Contributor
/// whose account role is Leader gets, and what an Admin gets everywhere.
/// </summary>
public enum ProjectAccess
{
    None = 0,
    Viewer = 1,
    Contributor = 2,
    Manager = 3
}

public class ProjectMember
{
    public int ProjectId { get; set; }
    public int UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = ProjectRoles.Viewer;
    /// <summary>Their account role; with Contributor above, Leader is what lets them manage the project.</summary>
    public string UserRole { get; set; } = Roles.Tester;
    public bool IsActive { get; set; }
    public DateTime AddedAt { get; set; }

    /// <summary>
    /// Whether this member manages the project: an active Leader holding Contributor. Admins manage
    /// every project whatever their row says, so they are left out of this on purpose -- it is what
    /// the "last one" check counts, and an Admin cannot keep a project from becoming Admin-only.
    /// </summary>
    public bool CanManage => IsActive && Role == ProjectRoles.Contributor && UserRole == Roles.Leader;
}

/// <summary>Body for adding a member or changing their role.</summary>
public class SaveProjectMemberRequest
{
    public int UserId { get; set; }

    [Required]
    public string Role { get; set; } = ProjectRoles.Viewer;
}
