using Microsoft.AspNetCore.Mvc;
using QaDocBackend.Infrastructure;
using QaDocBackend.Models;
using QaDocBackend.Repositories;

namespace QaDocBackend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AttachmentsController(
    IAttachmentRepository attachments,
    IProjectRepository projects,
    IProjectAccessService access) : ControllerBase
{
    /// <summary>Uploads a video for a project. Contributor access or better.</summary>
    [HttpPost]
    [RequestSizeLimit(Attachments.MaxBytes + 1024 * 1024)] // headroom for multipart framing
    public async Task<ActionResult<Attachment>> Upload([FromForm] int projectId, IFormFile? file)
    {
        if (file == null || file.Length == 0)
            return ValidationProblem(detail: "Choose a file to upload.");

        var level = await access.GetAsync(User, projectId);
        if (level == ProjectAccess.None || await projects.GetByIdAsync(projectId) == null)
            return ValidationProblem(detail: $"Project #{projectId} does not exist.");
        if (level < ProjectAccess.Contributor) return Forbid();

        if (file.Length > Attachments.MaxBytes)
            return ValidationProblem(detail: $"Videos must be {Attachments.MaxBytes / (1024 * 1024)} MB or smaller.");
        if (!Attachments.IsAllowed(file.ContentType))
            return ValidationProblem(detail: "Only MP4, WebM, Ogg and QuickTime videos can be attached.");

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);

        var saved = await attachments.CreateAsync(
            projectId, SafeName(file.FileName), file.ContentType, buffer.ToArray(), User.GetUserId());
        return Ok(saved);
    }

    /// <summary>
    /// Streams an attachment. Range processing is enabled so the browser can seek within a video
    /// instead of downloading the whole file first.
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult> Download(int id)
    {
        var file = await attachments.GetContentAsync(id);
        // Hidden rather than forbidden, matching how invisible projects and tickets behave.
        if (file == null || await access.GetAsync(User, file.ProjectId) == ProjectAccess.None)
            return NotFound(new { message = $"Attachment #{id} not found." });

        return File(file.Content, file.ContentType, enableRangeProcessing: true);
    }

    /// <summary>Keeps the stored name to its last path segment, so nothing looks like a path.</summary>
    private static string SafeName(string? name)
    {
        string trimmed = Path.GetFileName(name ?? string.Empty).Trim();
        return string.IsNullOrEmpty(trimmed) ? "video" : trimmed[..Math.Min(trimmed.Length, 255)];
    }
}
