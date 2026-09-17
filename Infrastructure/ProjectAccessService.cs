using System.Security.Claims;
using QaDocBackend.Models;
using QaDocBackend.Repositories;

namespace QaDocBackend.Infrastructure;

public interface IProjectAccessService
{
    /// <summary>What the signed-in user may do in this project. Global Admins always get Manager.</summary>
    Task<ProjectAccess> GetAsync(ClaimsPrincipal user, int projectId);
}

/// <summary>
/// Single place that answers "what may this user do here", so no controller has to remember the
/// rules. Admins bypass the membership table entirely; everyone else needs a ProjectMembers row.
/// </summary>
public class ProjectAccessService(IProjectMemberRepository members) : IProjectAccessService
{
    public async Task<ProjectAccess> GetAsync(ClaimsPrincipal user, int projectId)
    {
        if (user.IsInRole(Roles.Admin)) return ProjectAccess.Manager;

        return await members.GetRoleAsync(projectId, user.GetUserId()) switch
        {
            ProjectRoles.Manager => ProjectAccess.Manager,
            ProjectRoles.Contributor => ProjectAccess.Contributor,
            ProjectRoles.Viewer => ProjectAccess.Viewer,
            _ => ProjectAccess.None
        };
    }
}
