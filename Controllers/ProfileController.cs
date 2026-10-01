using Microsoft.AspNetCore.Mvc;
using QaDocBackend.Infrastructure;
using QaDocBackend.Models;
using QaDocBackend.Repositories;

namespace QaDocBackend.Controllers;

/// <summary>
/// Your own profile: contact details and picture. Every action works on the signed-in user, never
/// on an id from the request, so nobody can edit someone else's. Guests are turned away before
/// any of this runs (GuestReadOnlyFilter), and they have no row to edit anyway.
/// </summary>
[ApiController]
[Route("api/profile")]
public class ProfileController(IUserRepository users) : ControllerBase
{
    /// <summary>Saves your profile fields and returns your updated account.</summary>
    [HttpPut]
    public async Task<ActionResult<User>> Update([FromBody] UpdateProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return ValidationProblem(detail: "Display name is required.");

        int me = User.GetUserId();
        if (!await users.UpdateProfileAsync(me, request)) return NotFound();
        var saved = await users.GetByIdAsync(me);
        return saved == null ? NotFound() : Ok(AuthController.ToPublic(saved));
    }

    /// <summary>Stores a new profile picture (PNG, JPEG or WebP) and returns its version.</summary>
    [HttpPost("avatar")]
    [RequestSizeLimit(Avatars.MaxBytes + 64 * 1024)] // headroom for multipart framing
    public async Task<ActionResult> UploadAvatar(IFormFile? file)
    {
        if (file == null || file.Length == 0) return ValidationProblem(detail: "Choose a picture to upload.");
        if (file.Length > Avatars.MaxBytes)
            return ValidationProblem(detail: $"Pictures must be {Avatars.MaxBytes / (1024 * 1024)} MB or smaller.");

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);
        byte[] bytes = buffer.ToArray();
        // Judged by the bytes, not by what the upload claims to be.
        string? type = Avatars.Sniff(bytes);
        if (type == null) return ValidationProblem(detail: "Only PNG, JPEG and WebP pictures can be used.");

        int version = await users.SetAvatarAsync(User.GetUserId(), type, bytes);
        return Ok(new { avatarVersion = version });
    }

    /// <summary>Removes your picture; initials come back everywhere.</summary>
    [HttpDelete("avatar")]
    public async Task<ActionResult> DeleteAvatar() =>
        Ok(new { avatarVersion = await users.DeleteAvatarAsync(User.GetUserId()) });
}
