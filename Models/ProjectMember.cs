using System.ComponentModel.DataAnnotations;

namespace QaDocBackend.Models;

public static class ProjectRoles
{
    public const string Viewer = "Viewer";
    public const string Contributor = "Contributor";
    public const string Manager = "Manager";

    public static readonly string[] All = [Viewer, Contributor, Manager];

    public static bool IsValid(string? role) => All.Contains(role, StringComparer.Ordinal);
}

/// <summary>
/// What the current user may do in one project. Ordered, so a check is a comparison:
/// <c>access &gt;= ProjectAccess.Contributor</c>.
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
    public bool IsActive { get; set; }
    public DateTime AddedAt { get; set; }
}

/// <summary>Body for adding a member or changing their role.</summary>
public class SaveProjectMemberRequest
{
    public int UserId { get; set; }

    [Required]
    public string Role { get; set; } = ProjectRoles.Viewer;
}
