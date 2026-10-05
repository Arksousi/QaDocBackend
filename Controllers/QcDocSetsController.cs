using Microsoft.AspNetCore.Mvc;
using QaDocBackend.Infrastructure;
using QaDocBackend.Models;
using QaDocBackend.Repositories;

namespace QaDocBackend.Controllers;

[ApiController]
[Route("api")]
public class QcDocSetsController(
    IQcRepository qcRepo,
    IProjectRepository projects,
    IAttachmentRepository attachments,
    IProjectAccessService access,
    IQcJobService jobService,
    IQcGenerationRateLimiter rateLimiter,
    DocumentGeneratorSettings settings) : ControllerBase
{
    private const long MaxUploadBytes = 64 * 1024 * 1024;

    // ---------- DocSets ----------

    [HttpPost("projects/{projectId:int}/qc/docsets")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<ActionResult> Create(
        int projectId,
        [FromForm] CreateQcDocSetRequest request,
        IFormFile? logo,
        List<IFormFile>? images)
    {
        var level = await access.GetAsync(User, projectId);
        if (level == ProjectAccess.None) return NotFoundProject(projectId);
        if (level < ProjectAccess.Contributor) return Forbid();

        var project = await projects.GetByIdAsync(projectId);
        if (project == null) return NotFoundProject(projectId);

        var files = images ?? [];
        if (files.Count > settings.MaxImages)
            return ValidationProblem(detail: $"A docset takes at most {settings.MaxImages} screenshots (you sent {files.Count}).");

        // Process logo if provided
        int? logoAttachmentId = null;
        if (logo != null && logo.Length > 0)
        {
            if (logo.Length > settings.MaxImageBytes)
                return ValidationProblem(detail: $"Logo is {Mb(logo.Length)} MB; the limit is {Mb(settings.MaxImageBytes)} MB.");

            using var logoStream = new MemoryStream();
            await logo.CopyToAsync(logoStream);
            var logoBytes = logoStream.ToArray();
            string? logoType = Avatars.Sniff(logoBytes);
            if (logoType == null)
                return ValidationProblem(detail: "Logo is not a PNG, JPEG or WebP picture.");

            var savedLogo = await attachments.CreateAsync(
                projectId, SafeName(logo.FileName), logoType, logoBytes, User.GetUserId());
            logoAttachmentId = savedLogo.AttachmentId;
        }

        // Process ordered screenshots
        var storedScreens = new List<(int attachmentId, string? caption, int sortOrder)>();
        var captions = request.Captions ?? [];

        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            if (file.Length == 0) continue;
            if (file.Length > settings.MaxImageBytes)
                return ValidationProblem(detail:
                    $"Screenshot \"{SafeName(file.FileName)}\" is {Mb(file.Length)} MB; the limit is {Mb(settings.MaxImageBytes)} MB.");

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer);
            var bytes = buffer.ToArray();

            string? contentType = Avatars.Sniff(bytes);
            if (contentType == null)
                return ValidationProblem(detail: $"\"{SafeName(file.FileName)}\" is not a PNG, JPEG or WebP picture.");

            var saved = await attachments.CreateAsync(
                projectId, SafeName(file.FileName), contentType, bytes, User.GetUserId());

            string? caption = i < captions.Count && !string.IsNullOrWhiteSpace(captions[i])
                ? captions[i].Trim()
                : null;

            storedScreens.Add((saved.AttachmentId, caption, i + 1));
        }

        bool isDemo = await projects.IsDemoAsync(projectId) ?? false;
        int docSetId = await qcRepo.CreateDocSetAsync(
            projectId, request.Title, request.AppName, request.Description,
            request.Language, logoAttachmentId, isDemo, User.GetUserId(), storedScreens);

        return CreatedAtAction(nameof(GetById), new { id = docSetId }, new { id = docSetId });
    }

    [HttpGet("projects/{projectId:int}/qc/docsets")]
    public async Task<ActionResult<IEnumerable<QcDocSet>>> GetForProject(int projectId)
    {
        var level = await access.GetAsync(User, projectId);
        if (level == ProjectAccess.None) return NotFoundProject(projectId);

        var list = await qcRepo.GetByProjectAsync(projectId, onlyDemo: User.IsGuest());
        foreach (var ds in list) ds.MyRole = level.ToString();
        return Ok(list);
    }

    [HttpGet("qc/docsets/{id:int}")]
    public async Task<ActionResult<QcDocSet>> GetById(int id)
    {
        var docSet = await qcRepo.GetByIdAsync(id, onlyApprovedDocs: User.IsGuest());
        if (docSet == null) return NotFoundDocSet(id);

        var level = await access.GetAsync(User, docSet.ProjectId);
        if (level == ProjectAccess.None) return NotFoundDocSet(id);

        docSet.MyRole = level.ToString();
        return Ok(docSet);
    }

    [HttpPut("qc/docsets/{id:int}")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<ActionResult> Update(
        int id,
        [FromForm] UpdateQcDocSetRequest request,
        IFormFile? logo)
    {
        var docSet = await qcRepo.GetByIdAsync(id);
        if (docSet == null) return NotFoundDocSet(id);

        var level = await access.GetAsync(User, docSet.ProjectId);
        if (level == ProjectAccess.None) return NotFoundDocSet(id);
        if (level < ProjectAccess.Contributor) return Forbid();

        int? newLogoId = request.LogoAttachmentId;
        bool clearLogo = request.ClearLogo;

        if (logo != null && logo.Length > 0)
        {
            if (logo.Length > settings.MaxImageBytes)
                return ValidationProblem(detail: $"Logo is {Mb(logo.Length)} MB; the limit is {Mb(settings.MaxImageBytes)} MB.");

            using var logoStream = new MemoryStream();
            await logo.CopyToAsync(logoStream);
            var logoBytes = logoStream.ToArray();
            string? logoType = Avatars.Sniff(logoBytes);
            if (logoType == null)
                return ValidationProblem(detail: "Logo is not a PNG, JPEG or WebP picture.");

            var savedLogo = await attachments.CreateAsync(
                docSet.ProjectId, SafeName(logo.FileName), logoType, logoBytes, User.GetUserId());
            newLogoId = savedLogo.AttachmentId;
            clearLogo = false;
        }

        await qcRepo.UpdateDocSetAsync(
            id, request.Title, request.AppName, request.Description,
            request.Language, newLogoId, clearLogo);

        return NoContent();
    }

    [HttpDelete("qc/docsets/{id:int}")]
    public async Task<ActionResult> Delete(int id)
    {
        var docSet = await qcRepo.GetByIdAsync(id);
        if (docSet == null) return NotFoundDocSet(id);

        var level = await access.GetAsync(User, docSet.ProjectId);
        if (level == ProjectAccess.None) return NotFoundDocSet(id);
        if (level < ProjectAccess.Contributor) return Forbid();
        if (docSet.CreatedBy != User.GetUserId() && level < ProjectAccess.Manager) return Forbid();

        return await qcRepo.DeleteDocSetAsync(id) ? NoContent() : NotFoundDocSet(id);
    }

    // ---------- Screens ----------

    [HttpPut("qc/docsets/{id:int}/screens/order")]
    public async Task<ActionResult> ReorderScreens(int id, [FromBody] ReorderScreensRequest request)
    {
        var docSet = await qcRepo.GetByIdAsync(id);
        if (docSet == null) return NotFoundDocSet(id);

        var level = await access.GetAsync(User, docSet.ProjectId);
        if (level == ProjectAccess.None) return NotFoundDocSet(id);
        if (level < ProjectAccess.Contributor) return Forbid();

        await qcRepo.UpdateScreenOrderAndCaptionsAsync(id, request.Screens);
        return NoContent();
    }

    [HttpPost("qc/docsets/{id:int}/screens")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<ActionResult> AddScreens(
        int id,
        [FromForm] List<string>? captions,
        List<IFormFile>? images)
    {
        var docSet = await qcRepo.GetByIdAsync(id);
        if (docSet == null) return NotFoundDocSet(id);

        var level = await access.GetAsync(User, docSet.ProjectId);
        if (level == ProjectAccess.None) return NotFoundDocSet(id);
        if (level < ProjectAccess.Contributor) return Forbid();

        var files = images ?? [];
        if (files.Count == 0)
            return ValidationProblem(detail: "Select at least one screenshot to add.");

        if (docSet.Screens.Count + files.Count > settings.MaxImages)
            return ValidationProblem(detail:
                $"This docset already has {docSet.Screens.Count} screenshots; limit is {settings.MaxImages}.");

        var newScreens = new List<(int attachmentId, string? caption)>();
        var captionList = captions ?? [];

        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            if (file.Length == 0) continue;
            if (file.Length > settings.MaxImageBytes)
                return ValidationProblem(detail:
                    $"Screenshot \"{SafeName(file.FileName)}\" is {Mb(file.Length)} MB; the limit is {Mb(settings.MaxImageBytes)} MB.");

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer);
            var bytes = buffer.ToArray();

            string? contentType = Avatars.Sniff(bytes);
            if (contentType == null)
                return ValidationProblem(detail: $"\"{SafeName(file.FileName)}\" is not a PNG, JPEG or WebP picture.");

            var saved = await attachments.CreateAsync(
                docSet.ProjectId, SafeName(file.FileName), contentType, bytes, User.GetUserId());

            string? caption = i < captionList.Count && !string.IsNullOrWhiteSpace(captionList[i])
                ? captionList[i].Trim()
                : null;

            newScreens.Add((saved.AttachmentId, caption));
        }

        var added = await qcRepo.AddScreensAsync(id, newScreens);
        return Ok(added);
    }

    [HttpDelete("qc/docsets/{id:int}/screens/{screenId:int}")]
    public async Task<ActionResult> DeleteScreen(int id, int screenId)
    {
        var docSet = await qcRepo.GetByIdAsync(id);
        if (docSet == null) return NotFoundDocSet(id);

        var level = await access.GetAsync(User, docSet.ProjectId);
        if (level == ProjectAccess.None) return NotFoundDocSet(id);
        if (level < ProjectAccess.Contributor) return Forbid();

        return await qcRepo.DeleteScreenAsync(id, screenId) ? NoContent() : NotFound(new { message = "Screen not found." });
    }

    // ---------- Generation & Jobs ----------

    [HttpPost("qc/docsets/{id:int}/generate")]
    public async Task<ActionResult> Generate(int id, [FromBody] GenerateQcDocumentRequest? request)
    {
        var docSet = await qcRepo.GetByIdAsync(id);
        if (docSet == null) return NotFoundDocSet(id);

        var level = await access.GetAsync(User, docSet.ProjectId);
        if (level == ProjectAccess.None) return NotFoundDocSet(id);
        if (level < ProjectAccess.Contributor) return Forbid();

        if (!settings.Enabled)
            return ValidationProblem(detail:
                "AI generation is switched off by an administrator. Use Import / manual to paste documentation in.");

        var quota = rateLimiter.Consume(User.GetUserId());
        if (!quota.Allowed)
            return Problem(statusCode: StatusCodes.Status429TooManyRequests, detail:
                $"AI document generation ran {quota.Used} times in the last hour (limit {quota.Limit}). " +
                $"Try again in {Minutes(quota.RetryAfter)}, or use Import / manual.");

        if (docSet.Screens.Count == 0)
            return ValidationProblem(detail: "Add at least one screenshot before generating.");

        string kind = QcDocumentKinds.Normalise(request?.Kind);
        bool reRead = request?.ReReadScreens ?? false;

        string jobId = jobService.StartGenerationJob(id, kind, reRead, User.GetUserId());
        return Accepted(new { jobId });
    }

    [HttpGet("qc/jobs/{jobId}")]
    public ActionResult<QcJobStatus> GetJob(string jobId)
    {
        var status = jobService.GetJobStatus(jobId);
        return status != null ? Ok(status) : NotFound(new { message = "Job not found." });
    }

    // ---------- Documents ----------

    [HttpGet("qc/documents/{id:int}")]
    public async Task<ActionResult<QcDocument>> GetDocument(int id)
    {
        var doc = await qcRepo.GetDocumentByIdAsync(id);
        if (doc == null) return NotFoundDocument(id);

        var docSet = await qcRepo.GetByIdAsync(doc.DocSetId);
        if (docSet == null) return NotFoundDocument(id);

        var level = await access.GetAsync(User, docSet.ProjectId);
        if (level == ProjectAccess.None) return NotFoundDocument(id);

        if (User.IsGuest() && doc.Status != QcDocumentStatuses.Approved)
            return NotFoundDocument(id);

        return Ok(doc);
    }

    [HttpPut("qc/documents/{id:int}")]
    public async Task<ActionResult> UpdateDocument(int id, [FromBody] UpdateQcDocumentRequest request)
    {
        var doc = await qcRepo.GetDocumentByIdAsync(id);
        if (doc == null) return NotFoundDocument(id);

        var docSet = await qcRepo.GetByIdAsync(doc.DocSetId);
        if (docSet == null) return NotFoundDocument(id);

        var level = await access.GetAsync(User, docSet.ProjectId);
        if (level == ProjectAccess.None) return NotFoundDocument(id);
        if (level < ProjectAccess.Contributor) return Forbid();

        await qcRepo.UpdateDocumentAsync(id, request.Markdown, request.Status);
        return NoContent();
    }

    [HttpPost("qc/docsets/{id:int}/import")]
    public async Task<ActionResult<QcDocument>> Import(int id, [FromBody] ImportQcDocumentRequest request)
    {
        var docSet = await qcRepo.GetByIdAsync(id);
        if (docSet == null) return NotFoundDocSet(id);

        var level = await access.GetAsync(User, docSet.ProjectId);
        if (level == ProjectAccess.None) return NotFoundDocSet(id);
        if (level < ProjectAccess.Contributor) return Forbid();

        string kind = QcDocumentKinds.Normalise(request.Kind);
        string markdown = request.Markdown.Trim();
        if (string.IsNullOrWhiteSpace(markdown))
            return ValidationProblem(detail: "The markdown document is empty.");

        var created = await qcRepo.CreateDocumentVersionAsync(
            id, kind, markdown, QcDocumentSources.Imported, User.GetUserId(), QcDocumentStatuses.Draft);

        return Ok(created);
    }

    [HttpGet("qc/docsets/{id:int}/prompt")]
    public async Task<ActionResult> GetPrompt(int id, [FromQuery] string? kind)
    {
        var docSet = await qcRepo.GetByIdAsync(id);
        if (docSet == null) return NotFoundDocSet(id);

        var level = await access.GetAsync(User, docSet.ProjectId);
        if (level == ProjectAccess.None) return NotFoundDocSet(id);

        string docKind = QcDocumentKinds.Normalise(kind);
        var screens = docSet.Screens.Select(s => (s.SortOrder, s.Caption, s.ScreenSummary)).ToList();

        string prompt = QcPrompts.PastePrompt(
            docKind, docSet.AppName, docSet.BusinessDescription, docSet.Language, screens);

        return Ok(new { prompt });
    }

    [HttpGet("qc/documents/{id:int}/export")]
    public async Task<IActionResult> Export(int id, [FromQuery] string? format)
    {
        var doc = await qcRepo.GetDocumentByIdAsync(id);
        if (doc == null) return NotFoundDocument(id);

        var docSet = await qcRepo.GetByIdAsync(doc.DocSetId);
        if (docSet == null) return NotFoundDocument(id);

        var level = await access.GetAsync(User, docSet.ProjectId);
        if (level == ProjectAccess.None) return NotFoundDocument(id);
        if (User.IsGuest() && doc.Status != QcDocumentStatuses.Approved) return NotFoundDocument(id);

        string fmt = (format ?? "md").Trim().ToLowerInvariant();
        string baseName = $"{Sanitize(docSet.AppName)}_{doc.Kind}_v{doc.Version}";

        if (fmt == "md" || fmt == "markdown")
        {
            var bytes = QcExporter.ExportMarkdown(doc, docSet);
            return File(bytes, "text/markdown; charset=utf-8", $"{baseName}.md");
        }

        // Load screenshots and logo for HTML / DOCX export
        var screenData = new List<(string caption, string contentType, byte[] bytes)>();
        var docxScreenData = new List<(string caption, byte[] bytes)>();

        foreach (var screen in docSet.Screens)
        {
            var att = await attachments.GetContentAsync(screen.AttachmentId);
            if (att != null)
            {
                string caption = screen.Caption ?? $"Screen {screen.SortOrder}";
                screenData.Add((caption, att.ContentType, att.Content));
                docxScreenData.Add((caption, att.Content));
            }
        }

        (string contentType, byte[] bytes)? logoData = null;
        byte[]? logoBytes = null;
        if (docSet.LogoAttachmentId.HasValue)
        {
            var logoAtt = await attachments.GetContentAsync(docSet.LogoAttachmentId.Value);
            if (logoAtt != null)
            {
                logoData = (logoAtt.ContentType, logoAtt.Content);
                logoBytes = logoAtt.Content;
            }
        }

        if (fmt == "html")
        {
            string html = QcExporter.ExportHtml(doc, docSet, screenData, logoData);
            return Content(html, "text/html; charset=utf-8");
        }

        if (fmt == "docx")
        {
            var docxBytes = QcExporter.ExportDocx(doc, docSet, docxScreenData, logoBytes);
            return File(
                docxBytes,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                $"{baseName}.docx");
        }

        return ValidationProblem(detail: $"Unknown export format \"{format}\". Supported formats: docx, md, html.");
    }

    // ---------- Helpers ----------

    private ActionResult NotFoundProject(int id) =>
        NotFound(new { message = $"Project #{id} does not exist or you do not have permission to view it." });

    private ActionResult NotFoundDocSet(int id) =>
        NotFound(new { message = $"Docset #{id} does not exist or you do not have permission to view it." });

    private ActionResult NotFoundDocument(int id) =>
        NotFound(new { message = $"Document #{id} does not exist or you do not have permission to view it." });

    private static string SafeName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "image.png" : Path.GetFileName(name).Trim();

    private static string Sanitize(string name) =>
        string.Join("_", name.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_");

    private static string Mb(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.#");

    private static string Minutes(TimeSpan span)
    {
        int mins = Math.Max(1, (int)Math.Ceiling(span.TotalMinutes));
        return mins == 1 ? "1 minute" : $"{mins} minutes";
    }
}
