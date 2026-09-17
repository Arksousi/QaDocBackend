using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using QaDocBackend.Infrastructure;
using QaDocBackend.Models;
using QaDocBackend.Repositories;

namespace QaDocBackend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(IUserRepository users, IPasswordHasher<UserRecord> hasher, TokenService tokens) : ControllerBase
{
    private const string InvalidLogin = "Invalid username or password.";

    /// <summary>Tells the UI whether to show the one-time "Create admin account" screen.</summary>
    [AllowAnonymous]
    [HttpGet("status")]
    public async Task<ActionResult<AuthStatus>> Status() => Ok(new AuthStatus(!await users.AnyUsersAsync()));

    /// <summary>Creates the first Admin. Only works while there are no users at all.</summary>
    [AllowAnonymous]
    [HttpPost("setup")]
    public async Task<ActionResult<LoginResponse>> Setup([FromBody] CreateUserRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return ValidationProblem(detail: "Display name is required.");

        request.Role = Roles.Admin;
        var hash = hasher.HashPassword(new UserRecord(), request.Password);
        int? id = await users.CreateFirstAdminAsync(request, hash);
        if (id == null)
            return Conflict(new ProblemDetails { Status = 409, Title = "Setup has already been completed. Sign in instead." });

        var user = (await users.GetByIdAsync(id.Value))!;
        return Ok(tokens.CreateToken(user));
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request)
    {
        var user = await users.GetByUsernameAsync(request.Username);
        if (user == null)
        {
            // Hash anyway so response time doesn't reveal whether the username exists.
            hasher.HashPassword(new UserRecord(), request.Password);
            return Unauthorized(new ProblemDetails { Status = 401, Title = InvalidLogin });
        }

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed || !user.IsActive)
            return Unauthorized(new ProblemDetails { Status = 401, Title = InvalidLogin });

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            await users.SetPasswordAsync(user.UserId, hasher.HashPassword(user, request.Password));

        user = (await users.GetByIdAsync(user.UserId))!;
        return Ok(tokens.CreateToken(user));
    }

    /// <summary>The signed-in user's profile (also used to restore a session on page load).</summary>
    [HttpGet("me")]
    public async Task<ActionResult<User>> Me()
    {
        var user = await users.GetByIdAsync(User.GetUserId());
        return user == null ? Unauthorized() : Ok(ToPublic(user));
    }

    /// <summary>Changes your own password. Returns a fresh token because the old one is revoked.</summary>
    [HttpPost("change-password")]
    public async Task<ActionResult<LoginResponse>> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var user = await users.GetByIdAsync(User.GetUserId());
        if (user == null) return Unauthorized();

        if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
            return ValidationProblem(detail: "Current password is incorrect.");

        await users.SetPasswordAsync(user.UserId, hasher.HashPassword(user, request.NewPassword));
        return Ok(tokens.CreateToken((await users.GetByIdAsync(user.UserId))!));
    }

    internal static User ToPublic(User u) => new()
    {
        UserId = u.UserId, Username = u.Username, DisplayName = u.DisplayName,
        Role = u.Role, IsActive = u.IsActive, CreatedAt = u.CreatedAt
    };
}
