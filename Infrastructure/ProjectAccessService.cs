using System.Security.Claims;
using QaDocBackend.Models;
using QaDocBackend.Repositories;

namespace QaDocBackend.Infrastructure;

public interface IProjectAccessService
{
    /// <summary>What the signed-in user may do in this project. Global Admins always get Manager.</summary>
    Task<ProjectAccess> GetAsync(ClaimsPrincipal user, int projectId);

    /// <summary>
    /// The same answer for somebody else — used to check that a proposed assignee actually works on
    /// the project. A deactivated account gets None whatever its membership says.
    /// </summary>
    Task<ProjectAccess> GetForUserAsync(int userId, int projectId);
}

/// <summary>
/// Single place that answers "what may this user do here", so no controller has to remember the
/// rules. Admins bypass the membership table entirely; everyone else needs a ProjectMembers row.
/// </summary>
public class ProjectAccessService(IProjectMemberRepository members, IUserRepository users) : IProjectAccessService
{
    public async Task<ProjectAccess> GetAsync(ClaimsPrincipal user, int projectId)
    {
        if (user.IsInRole(Roles.Admin)) return ProjectAccess.Manager;
        return await FromMembershipAsync(user.GetUserId(), projectId);
    }

    public async Task<ProjectAccess> GetForUserAsync(int userId, int projectId)
    {
        var user = await users.GetByIdAsync(userId);
        if (user is not { IsActive: true }) return ProjectAccess.None;
        if (user.Role == Roles.Admin) return ProjectAccess.Manager;
        return await FromMembershipAsync(userId, projectId);
    }

    private async Task<ProjectAccess> FromMembershipAsync(int userId, int projectId) =>
        await members.GetRoleAsync(projectId, userId) switch
        {
            ProjectRoles.Manager => ProjectAccess.Manager,
            ProjectRoles.Contributor => ProjectAccess.Contributor,
            ProjectRoles.Viewer => ProjectAccess.Viewer,
            _ => ProjectAccess.None
        };
}
