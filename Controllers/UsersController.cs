using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using QaDocBackend.Infrastructure;
using QaDocBackend.Models;
using QaDocBackend.Repositories;

namespace QaDocBackend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsersController(IUserRepository users, IPasswordHasher<UserRecord> hasher) : ControllerBase
{
    /// <summary>Active users for the "Assigned To" picker. Any signed-in user.</summary>
    [HttpGet("options")]
    public async Task<ActionResult<IEnumerable<UserOption>>> Options()
    {
        // Guests are strangers: the staff list is not part of the tour.
        if (User.IsGuest()) return Forbid();
        return Ok(await users.GetActiveOptionsAsync());
    }

    /// <summary>The Users Dashboard: everyone's load against their limit. Admins and Leaders, who plan who does what.</summary>
    [Authorize(Roles = Roles.Leads)]
    [HttpGet("workload")]
    public async Task<ActionResult<IEnumerable<UserWorkload>>> GetWorkload() => Ok(await users.GetWorkloadAsync());

    /// <summary>
    /// Changes only the ticket limit, so the dashboard can edit it in place without resending name,
    /// role and status. Admin only: a Leader sees the loads but does not set anyone's capacity.
    /// </summary>
    [Authorize(Roles = Roles.Admin)]
    [HttpPut("{id:int}/ticket-limit")]
    public async Task<ActionResult> SetTicketLimit(int id, [FromBody] SetTicketLimitRequest request) =>
        await users.SetTicketLimitAsync(id, request.TicketLimit)
            ? NoContent()
            : NotFound(new { message = $"User #{id} not found." });

    [Authorize(Roles = Roles.Admin)]
    [HttpGet]
    public async Task<ActionResult<IEnumerable<User>>> GetAll() =>
        Ok((await users.GetAllAsync()).Select(AuthController.ToPublic));

    [Authorize(Roles = Roles.Admin)]
    [HttpPost]
    public async Task<ActionResult> Create([FromBody] CreateUserRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return ValidationProblem(detail: "Display name is required.");
        if (await users.GetByUsernameAsync(request.Username) != null)
            return Conflict(new ProblemDetails { Status = 409, Title = $"Username “{request.Username.Trim()}” is already taken." });

        int id = await users.CreateAsync(request, hasher.HashPassword(new UserRecord(), request.Password));
        return Created($"/api/users/{id}", new { id });
    }

    [Authorize(Roles = Roles.Admin)]
    [HttpPut("{id:int}")]
    public async Task<ActionResult> Update(int id, [FromBody] UpdateUserRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return ValidationProblem(detail: "Display name is required.");

        var existing = await users.GetByIdAsync(id);
        if (existing == null) return NotFound(new { message = $"User #{id} not found." });

        // Never leave the system without an active Admin.
        bool losesAdmin = existing.Role == Roles.Admin && existing.IsActive
                          && (request.Role != Roles.Admin || !request.IsActive);
        if (losesAdmin && await users.CountActiveAdminsAsync() <= 1)
            return Conflict(new ProblemDetails { Status = 409, Title = "This is the last active Admin. Make another user Admin first." });

        await users.UpdateAsync(id, request);
        return NoContent();
    }

    /// <summary>Admin sets a temporary password; the user is signed out everywhere.</summary>
    [Authorize(Roles = Roles.Admin)]
    [HttpPost("{id:int}/reset-password")]
    public async Task<ActionResult> ResetPassword(int id, [FromBody] ResetPasswordRequest request)
    {
        var existing = await users.GetByIdAsync(id);
        if (existing == null) return NotFound(new { message = $"User #{id} not found." });
        if (id == User.GetUserId())
            return ValidationProblem(detail: "Use “Change password” to change your own password.");

        await users.SetPasswordAsync(id, hasher.HashPassword(existing, request.NewPassword));
        return NoContent();
    }
}
