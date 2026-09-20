using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QaDocBackend.Infrastructure;
using QaDocBackend.Models;
using QaDocBackend.Repositories;

namespace QaDocBackend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProjectsController(
    IProjectRepository projects,
    ITicketRepository tickets,
    IProjectMemberRepository members,
    IUserRepository users,
    IProjectAccessService access) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Project>>> GetAll() =>
        Ok(await projects.GetAllAsync(User.GetUserId(), User.IsInRole(Roles.Admin)));

    /// <summary>Projects with the most recent ticket activity (Project Menu).</summary>
    [HttpGet("recent")]
    public async Task<ActionResult<IEnumerable<Project>>> GetRecent([FromQuery] int top = 5) =>
        Ok(await projects.GetRecentAsync(Math.Clamp(top, 1, 50), User.GetUserId(), User.IsInRole(Roles.Admin)));

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Project>> GetById(int id)
    {
        var level = await access.GetAsync(User, id);
        if (level == ProjectAccess.None) return NotFoundProject(id);

        var project = await projects.GetByIdAsync(id);
        if (project == null) return NotFoundProject(id);

        project.MyRole = level.ToString();
        return Ok(project);
    }

    [HttpPost]
    public async Task<ActionResult> Create([FromBody] CreateProjectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectName))
            return ValidationProblem(detail: "Project name is required.");
        if (!Codes.IsValid(request.ProjectCode))
            return ValidationProblem(detail: $"Project code must be 1-{Codes.MaxLength} letters or digits, with no spaces.");

        int newId = await projects.CreateAsync(request, User.GetUserId());
        return CreatedAtAction(nameof(GetById), new { id = newId }, new { id = newId });
    }

    /// <summary>Deletes the project and every ticket in it. Admin only; there is no undo.</summary>
    [Authorize(Roles = Roles.Admin)]
    [HttpDelete("{id:int}")]
    public async Task<ActionResult> Delete(int id) =>
        await projects.DeleteAsync(id) ? NoContent() : NotFoundProject(id);

    /// <summary>Ticket Viewer list. Filters are optional; sorted by newest activity.</summary>
    [HttpGet("{id:int}/tickets")]
    public async Task<ActionResult<IEnumerable<Ticket>>> GetTickets(
        int id, [FromQuery] int? folderId, [FromQuery] string? search, [FromQuery] string? state,
        [FromQuery] string? type, [FromQuery] string? tag, [FromQuery] int? assignedTo)
    {
        if (await access.GetAsync(User, id) == ProjectAccess.None) return NotFoundProject(id);
        return Ok(await tickets.GetByProjectAsync(id, folderId, search, state, type, tag, assignedTo));
    }

    /// <summary>Tags already used in the project, for autocomplete.</summary>
    [HttpGet("{id:int}/suggestions")]
    public async Task<ActionResult<ProjectSuggestions>> GetSuggestions(int id)
    {
        if (await access.GetAsync(User, id) == ProjectAccess.None) return NotFoundProject(id);
        return Ok(await projects.GetSuggestionsAsync(id));
    }

    // ---------- Members ----------

    [HttpGet("{id:int}/members")]
    public async Task<ActionResult<IEnumerable<ProjectMember>>> GetMembers(int id)
    {
        if (await access.GetAsync(User, id) == ProjectAccess.None) return NotFoundProject(id);
        return Ok(await members.GetByProjectAsync(id));
    }

    /// <summary>Adds a member or changes their role. Admins anywhere; Managers on their own project.</summary>
    [HttpPut("{id:int}/members")]
    public async Task<ActionResult> SaveMember(int id, [FromBody] SaveProjectMemberRequest request)
    {
        var level = await access.GetAsync(User, id);
        if (level == ProjectAccess.None) return NotFoundProject(id);
        if (level < ProjectAccess.Manager) return Forbid();
        if (await projects.GetByIdAsync(id) == null) return NotFoundProject(id);

        if (!ProjectRoles.IsValid(request.Role))
            return ValidationProblem(detail: $"Role must be one of: {string.Join(", ", ProjectRoles.All)}.");
        if (!await users.IsActiveUserAsync(request.UserId))
            return ValidationProblem(detail: "Members must be active users.");

        await members.SaveAsync(id, request.UserId, request.Role);
        return NoContent();
    }

    /// <summary>
    /// Removes a member. A Manager may not remove themselves while they are the only one, which
    /// would leave the project reachable by Admins alone.
    /// </summary>
    [HttpDelete("{id:int}/members/{userId:int}")]
    public async Task<ActionResult> RemoveMember(int id, int userId)
    {
        var level = await access.GetAsync(User, id);
        if (level == ProjectAccess.None) return NotFoundProject(id);
        if (level < ProjectAccess.Manager) return Forbid();

        var current = (await members.GetByProjectAsync(id)).ToList();
        if (current.All(m => m.UserId != userId))
            return NotFound(new { message = "That user is not a member of this project." });

        bool lastManager = current.Count(m => m.Role == ProjectRoles.Manager) == 1
            && current.Any(m => m.UserId == userId && m.Role == ProjectRoles.Manager);
        if (lastManager && !User.IsInRole(Roles.Admin))
            return ValidationProblem(detail: "Add another manager before removing the last one.");

        await members.RemoveAsync(id, userId);
        return NoContent();
    }

    /// <summary>A project the user cannot see is reported as missing, so membership is not leaked.</summary>
    private NotFoundObjectResult NotFoundProject(int id) => NotFound(new { message = $"Project #{id} not found." });
}
