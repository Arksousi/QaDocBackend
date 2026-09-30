using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QaDocBackend.Infrastructure;
using QaDocBackend.Models;
using QaDocBackend.Repositories;

namespace QaDocBackend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TicketsController(
    ITicketRepository tickets,
    IFolderRepository folders,
    IUserRepository users,
    IProjectMemberRepository members,
    IProjectAccessService access) : ControllerBase
{
    private const int MaxTags = 20;
    private const int MaxTagLength = 50;

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Ticket>> GetById(int id)
    {
        var ticket = await tickets.GetByIdAsync(id);
        // A ticket in a project the user cannot see must look missing, not forbidden.
        if (ticket == null || await access.GetAsync(User, ticket.ProjectId) == ProjectAccess.None)
            return NotFoundTicket(id);
        return Ok(ticket);
    }

    [HttpPost]
    public async Task<ActionResult> Create([FromBody] SaveTicketRequest request)
    {
        // The folder decides the project, so a request cannot file a ticket into a project
        // the caller cannot reach by naming one project and a folder from another.
        var folder = await folders.GetByIdAsync(request.FolderId);
        if (folder == null) return ValidationProblem(detail: $"Folder #{request.FolderId} does not exist.");

        var level = await access.GetAsync(User, folder.ProjectId);
        if (level == ProjectAccess.None)
            return ValidationProblem(detail: $"Folder #{request.FolderId} does not exist.");
        if (level < ProjectAccess.Contributor) return Forbid();
        if (await ValidateAsync(request, folder.ProjectId, current: [], level) is { } problem) return problem;

        await JoinAssigneesAsync(request, folder.ProjectId, current: [], level);
        int newId = await tickets.CreateAsync(request, User.GetUserId());
        return CreatedAtAction(nameof(GetById), new { id = newId }, new { id = newId });
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult> Update(int id, [FromBody] SaveTicketRequest request)
    {
        var existing = await tickets.GetByIdAsync(id);
        if (existing == null) return NotFoundTicket(id);

        // Checked against the ticket's own project, so a body cannot move it somewhere else.
        var level = await access.GetAsync(User, existing.ProjectId);
        if (level == ProjectAccess.None) return NotFoundTicket(id);
        if (level < ProjectAccess.Contributor) return Forbid();
        var current = existing.Assignees.Select(a => a.UserId).ToList();
        if (await ValidateAsync(request, existing.ProjectId, current, level) is { } problem) return problem;

        await JoinAssigneesAsync(request, existing.ProjectId, current, level);
        await tickets.UpdateAsync(id, request, User.GetUserId());
        return NoContent();
    }

    [Authorize(Roles = Roles.Admin)]
    [HttpDelete("{id:int}")]
    public async Task<ActionResult> Delete(int id) =>
        await tickets.DeleteAsync(id) ? NoContent() : NotFoundTicket(id);

    /// <summary>Commenting needs only Viewer access: read-only members can still take part.</summary>
    [HttpPost("{id:int}/comments")]
    public async Task<ActionResult<TicketComment>> AddComment(int id, [FromBody] AddCommentRequest request)
    {
        var ticket = await tickets.GetByIdAsync(id);
        if (ticket == null || await access.GetAsync(User, ticket.ProjectId) == ProjectAccess.None)
            return NotFoundTicket(id);
        if (string.IsNullOrWhiteSpace(request.Text))
            return ValidationProblem(detail: "Comment text is required.");

        // Only people who work on the project can be mentioned: its Contributors, and Admins. Anyone
        // else is dropped rather than refused — a stale name in a draft should not lose the comment.
        // Mentioning yourself tells nobody anything.
        int author = User.GetUserId();
        var mentioned = new List<int>();
        foreach (int userId in (request.MentionedUserIds ?? []).Distinct().Where(u => u != author).Take(AddCommentRequest.MaxMentions))
        {
            if (await access.GetForUserAsync(userId, ticket.ProjectId) >= ProjectAccess.Contributor)
                mentioned.Add(userId);
        }

        var comment = await tickets.AddCommentAsync(id, request.Text, author, mentioned);
        return comment == null ? NotFoundTicket(id) : Ok(comment);
    }

    /// <summary>
    /// Assigning a ticket to someone outside the project, or to one of its Viewers, makes them a
    /// Contributor so they can work on it. Only for those who manage the project (a Leader who
    /// contributes, or an Admin) -- the same people who could have added them from Members.
    /// </summary>
    private async Task JoinAssigneesAsync(SaveTicketRequest request, int projectId, List<int> current, ProjectAccess level)
    {
        if (level < ProjectAccess.Manager) return;
        foreach (int assignee in (request.AssignedToUserIds ?? []).Except(current))
        {
            if (await access.GetForUserAsync(assignee, projectId) < ProjectAccess.Contributor)
                await members.SaveAsync(projectId, assignee, ProjectRoles.Contributor);
        }
    }

    private NotFoundObjectResult NotFoundTicket(int id) => NotFound(new { message = $"Ticket #{id} not found." });

    /// <summary>Checks title and assignees, and normalises tags (trimmed, no blanks, case-insensitive distinct).</summary>
    private async Task<ActionResult?> ValidateAsync(SaveTicketRequest request, int projectId, List<int> current, ProjectAccess level)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return ValidationProblem(detail: "Title is required.");

        // Not sent means unchanged (see SaveTicketRequest); on a new ticket, current is empty anyway.
        request.AssignedToUserIds = (request.AssignedToUserIds ?? current).Distinct().ToList();
        if (request.AssignedToUserIds.Count > SaveTicketRequest.MaxAssignees)
            return ValidationProblem(detail: $"A ticket can be assigned to at most {SaveTicketRequest.MaxAssignees} people.");

        // A ticket can only be given to somebody who actually works on the project: a Contributor,
        // or a global Admin. Only people being added are checked: someone already on it who has
        // since lost access or been deactivated stays, so old tickets remain editable.
        // Someone who manages the project may add anyone active: JoinAssigneesAsync makes them a
        // Contributor first, so the rule still holds once the save lands.
        foreach (int assignee in request.AssignedToUserIds.Except(current))
        {
            if (!await users.IsActiveUserAsync(assignee))
                return ValidationProblem(detail: "Everyone assigned must be an active user.");
            if (level < ProjectAccess.Manager && await access.GetForUserAsync(assignee, projectId) < ProjectAccess.Contributor)
                return ValidationProblem(detail: "Everyone assigned must be a Contributor on this project.");
        }

        request.Tags = (request.Tags ?? [])
            .Select(t => t?.Trim() ?? string.Empty)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (request.Tags.Any(t => t.Length > MaxTagLength))
            return ValidationProblem(detail: $"Tags can be at most {MaxTagLength} characters.");
        if (request.Tags.Count > MaxTags)
            return ValidationProblem(detail: $"A ticket can have at most {MaxTags} tags.");
        return null;
    }
}
