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
        if (await ValidateAsync(request, folder.ProjectId, currentAssignee: null) is { } problem) return problem;

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
        if (await ValidateAsync(request, existing.ProjectId, existing.AssignedToUserId) is { } problem) return problem;

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

        var comment = await tickets.AddCommentAsync(id, request.Text, User.GetUserId());
        return comment == null ? NotFoundTicket(id) : Ok(comment);
    }

    private NotFoundObjectResult NotFoundTicket(int id) => NotFound(new { message = $"Ticket #{id} not found." });

    /// <summary>Checks title and assignee, and normalises tags (trimmed, no blanks, case-insensitive distinct).</summary>
    private async Task<ActionResult?> ValidateAsync(SaveTicketRequest request, int projectId, int? currentAssignee)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return ValidationProblem(detail: "Title is required.");

        // A ticket can only be given to somebody who actually works on the project: Contributor or
        // Manager, or a global Admin. Keeping an assignee who has since lost access or been
        // deactivated is allowed, so old tickets stay editable; newly assigning one is not.
        if (request.AssignedToUserId is int assignee && assignee != currentAssignee)
        {
            if (!await users.IsActiveUserAsync(assignee))
                return ValidationProblem(detail: "Assigned To must be an active user.");
            if (await access.GetForUserAsync(assignee, projectId) < ProjectAccess.Contributor)
                return ValidationProblem(detail: "Assigned To must be a Contributor or Manager on this project.");
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
