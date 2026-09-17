using Microsoft.AspNetCore.Mvc;
using QaDocBackend.Infrastructure;
using QaDocBackend.Models;
using QaDocBackend.Repositories;

namespace QaDocBackend.Controllers;

/// <summary>
/// Folders sit between a project and its tickets and own the middle segment of every ticket key.
/// They are addressed under their project, since that is what access is checked against.
/// </summary>
[ApiController]
[Route("api/projects/{projectId:int}/folders")]
public class FoldersController(
    IFolderRepository folders,
    IProjectRepository projects,
    IProjectAccessService access) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Folder>>> GetAll(int projectId)
    {
        if (await access.GetAsync(User, projectId) == ProjectAccess.None) return NotFoundProject(projectId);
        return Ok(await folders.GetByProjectAsync(projectId));
    }

    /// <summary>Managers organise a project's folders; Contributors only file tickets into them.</summary>
    [HttpPost]
    public async Task<ActionResult> Create(int projectId, [FromBody] SaveFolderRequest request)
    {
        if (await Denied(projectId) is { } denied) return denied;
        if (await projects.GetByIdAsync(projectId) == null) return NotFoundProject(projectId);
        if (Invalid(request) is { } problem) return problem;

        int newId = await folders.CreateAsync(projectId, request, User.GetUserId());
        return CreatedAtAction(nameof(GetAll), new { projectId }, new { id = newId });
    }

    [HttpPut("{folderId:int}")]
    public async Task<ActionResult> Update(int projectId, int folderId, [FromBody] SaveFolderRequest request)
    {
        if (await Denied(projectId) is { } denied) return denied;
        if (await Mismatched(projectId, folderId) is { } wrong) return wrong;
        if (Invalid(request) is { } problem) return problem;

        await folders.UpdateAsync(folderId, request);
        return NoContent();
    }

    /// <summary>
    /// Refuses while the folder still holds tickets: the cascade would take them all with it,
    /// and their keys cannot be reissued afterwards.
    /// </summary>
    [HttpDelete("{folderId:int}")]
    public async Task<ActionResult> Delete(int projectId, int folderId)
    {
        if (await Denied(projectId) is { } denied) return denied;
        if (await Mismatched(projectId, folderId) is { } wrong) return wrong;

        if (await folders.HasTicketsAsync(folderId))
            return ValidationProblem(detail: "Move or delete this folder's tickets before deleting it.");
        if ((await folders.GetByProjectAsync(projectId)).Count() <= 1)
            return ValidationProblem(detail: "A project needs at least one folder.");

        await folders.DeleteAsync(folderId);
        return NoContent();
    }

    /// <summary>Hidden from non-members, forbidden for members below Manager.</summary>
    private async Task<ActionResult?> Denied(int projectId)
    {
        var level = await access.GetAsync(User, projectId);
        if (level == ProjectAccess.None) return NotFoundProject(projectId);
        return level < ProjectAccess.Manager ? Forbid() : null;
    }

    /// <summary>Stops a folder in one project being edited through another project's URL.</summary>
    private async Task<ActionResult?> Mismatched(int projectId, int folderId)
    {
        var folder = await folders.GetByIdAsync(folderId);
        return folder == null || folder.ProjectId != projectId
            ? NotFound(new { message = $"Folder #{folderId} not found." })
            : null;
    }

    private ActionResult? Invalid(SaveFolderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FolderName))
            return ValidationProblem(detail: "Folder name is required.");
        return Codes.IsValid(request.FolderCode)
            ? null
            : ValidationProblem(detail: $"Folder code must be 1-{Codes.MaxLength} letters or digits, with no spaces.");
    }

    private NotFoundObjectResult NotFoundProject(int id) => NotFound(new { message = $"Project #{id} not found." });
}
