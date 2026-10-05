using Microsoft.AspNetCore.Mvc;
using QaDocBackend.Infrastructure;
using QaDocBackend.Models;
using QaDocBackend.Repositories;

namespace QaDocBackend.Controllers;

/// <summary>
/// The Test Case Generator: a suite of screenshots belongs to a project, and every answer comes
/// from that project's membership through ProjectAccessService — Viewer reads, Contributor creates
/// and edits, no second permission system. Guests are read-only through GuestReadOnlyFilter, and
/// see only the demo projects' suites, exactly like tickets.
/// </summary>
[ApiController]
[Route("api")]
public class TestSuitesController(
    ITestSuiteRepository suites,
    IProjectRepository projects,
    IFolderRepository folders,
    IAttachmentRepository attachments,
    IProjectAccessService access,
    ITestCaseGenerator generator,
    IGenerationRateLimiter rateLimiter,
    TestCaseGeneratorSettings settings) : ControllerBase
{
    /// <summary>Screenshots are capped and resized before they leave; this is the headroom above that.</summary>
    private const long MaxUploadBytes = 64 * 1024 * 1024;

    // ---------- Suites ----------

    /// <summary>Creates a suite with its screenshots (multipart: title, description, images).</summary>
    [HttpPost("projects/{projectId:int}/testsuites")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<ActionResult> Create(int projectId, [FromForm] CreateTestSuiteRequest request, List<IFormFile>? images)
    {
        var level = await access.GetAsync(User, projectId);
        if (level == ProjectAccess.None) return NotFoundProject(projectId);
        if (level < ProjectAccess.Contributor) return Forbid();

        if (await projects.GetByIdAsync(projectId) == null) return NotFoundProject(projectId);
        if (request.FolderId is { } folderId && await FolderOfAsync(projectId, folderId) is null)
            return ValidationProblem(detail: $"Folder #{folderId} does not exist in this project.");

        var files = images ?? [];
        if (files.Count > settings.MaxImages)
            return ValidationProblem(detail: $"A suite takes at most {settings.MaxImages} screenshots (you sent {files.Count}).");

        var stored = new List<int>();
        foreach (var file in files)
        {
            if (file.Length == 0) continue;
            if (file.Length > settings.MaxImageBytes)
                return ValidationProblem(detail:
                    $"Screenshot \"{SafeName(file.FileName)}\" is {Mb(file.Length)} MB; the limit is {Mb(settings.MaxImageBytes)} MB.");

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer);
            var bytes = buffer.ToArray();

            // Read the type from the bytes, never from the name or the header the browser chose.
            string? contentType = Avatars.Sniff(bytes);
            if (contentType == null)
                return ValidationProblem(detail: $"\"{SafeName(file.FileName)}\" is not a PNG, JPEG or WebP picture.");

            var saved = await attachments.CreateAsync(projectId, SafeName(file.FileName), contentType, bytes, User.GetUserId());
            stored.Add(saved.AttachmentId);
        }

        bool isDemo = await projects.IsDemoAsync(projectId) ?? false;
        int suiteId = await suites.CreateAsync(projectId, request.FolderId, request.Title, request.Description,
            isDemo, User.GetUserId(), stored);

        return CreatedAtAction(nameof(GetById), new { id = suiteId }, new { id = suiteId });
    }

    /// <summary>The suites of a project. Any member, including a guest on a demo project.</summary>
    [HttpGet("projects/{projectId:int}/testsuites")]
    public async Task<ActionResult<IEnumerable<TestSuite>>> GetForProject(int projectId)
    {
        var level = await access.GetAsync(User, projectId);
        if (level == ProjectAccess.None) return NotFoundProject(projectId);

        return Ok(await GetSuitesAsync(projectId, level));
    }

    /// <summary>The project's suites, each carrying what the caller may do there.</summary>
    private async Task<IEnumerable<TestSuite>> GetSuitesAsync(int projectId, ProjectAccess level)
    {
        var list = await suites.GetByProjectAsync(projectId, onlyDemo: User.IsGuest());
        foreach (var suite in list) suite.MyRole = level.ToString();
        return list;
    }

    /// <summary>One suite with its screenshots and test cases.</summary>
    [HttpGet("testsuites/{id:int}")]
    public async Task<ActionResult<TestSuite>> GetById(int id)
    {
        var suite = await suites.GetByIdAsync(id);
        if (suite == null) return NotFoundSuite(id);

        var level = await access.GetAsync(User, suite.ProjectId);
        if (level == ProjectAccess.None) return NotFoundSuite(id);

        suite.MyRole = level.ToString();
        return Ok(suite);
    }

    /// <summary>
    /// The suite and everything in it. A Contributor may delete their own suite; deleting someone
    /// else's needs the same access as managing the project.
    /// </summary>
    [HttpDelete("testsuites/{id:int}")]
    public async Task<ActionResult> Delete(int id)
    {
        var suite = await suites.GetByIdAsync(id);
        if (suite == null) return NotFoundSuite(id);

        var level = await access.GetAsync(User, suite.ProjectId);
        if (level == ProjectAccess.None) return NotFoundSuite(id);
        if (level < ProjectAccess.Contributor) return Forbid();
        if (suite.CreatedByUserId != User.GetUserId() && level < ProjectAccess.Manager) return Forbid();

        return await suites.DeleteAsync(id) ? NoContent() : NotFoundSuite(id);
    }

    // ---------- Generation ----------

    /// <summary>Sends the screenshots and the description to the configured provider and saves the reply as Draft.</summary>
    [HttpPost("testsuites/{id:int}/generate")]
    public async Task<ActionResult<IEnumerable<TestCase>>> Generate(
        int id, [FromBody] GenerateTestCasesRequest? request, CancellationToken ct)
    {
        if (await ReadSuiteAsync(id) is not { } suite) return NotFoundSuite(id);
        if (await access.GetAsync(User, suite.ProjectId) < ProjectAccess.Contributor) return Forbid();

        if (!settings.Enabled)
            return ValidationProblem(detail:
                "AI generation is switched off by an administrator. Use Import / manual to paste cases in.");

        var quota = rateLimiter.Consume(User.GetUserId());
        if (!quota.Allowed)
            return Problem(statusCode: StatusCodes.Status429TooManyRequests, detail:
                $"AI generation ran {quota.Used} times in the last hour (limit {quota.Limit}). " +
                $"Try again in {Minutes(quota.RetryAfter)}, or use Import / manual.");

        if (suite.Screens.Count == 0)
            return ValidationProblem(detail: "Add at least one screenshot before generating.");

        var images = await LoadImagesAsync(suite);
        var options = BuildOptions(request?.MaxCases, request?.Categories, request?.Language);

        var generated = await generator.GenerateAsync(
            new GenerateRequest
            {
                BusinessDescription = suite.BusinessDescription ?? string.Empty,
                Images = images,
                Options = options
            }, ct);

        var saved = await suites.AddCasesAsync(id, generated.TestCases, TestSources.Ai);
        if (saved.Count == 0) return ValidationProblem(detail: "The AI reply held no usable test cases. Nothing was saved.");
        return Ok(saved);
    }

    /// <summary>
    /// The manual fallback: the JSON a person copied out of any chat AI, in the same shape the
    /// provider returns, saved as Draft with source Imported. This is the route that works with no
    /// key at all.
    /// </summary>
    [HttpPost("testsuites/{id:int}/import")]
    public async Task<ActionResult<IEnumerable<TestCase>>> Import(int id, [FromBody] ImportTestCasesRequest request)
    {
        if (await ReadSuiteAsync(id) is not { } suite) return NotFoundSuite(id);
        if (await access.GetAsync(User, suite.ProjectId) < ProjectAccess.Contributor) return Forbid();

        var parsed = TestCaseParser.Parse(request.Json, BuildOptions(null, categories: null, language: null));
        var saved = await suites.AddCasesAsync(id, parsed.TestCases, TestSources.Imported);
        if (saved.Count == 0) return ValidationProblem(detail: "The JSON held no usable test cases. Nothing was saved.");
        return Ok(saved);
    }

    /// <summary>
    /// The ready-made prompt, so somebody can paste it into any chat AI and bring the answer back
    /// through Import. Read-only on purpose: this is what a guest without a key uses.
    /// </summary>
    [HttpGet("testsuites/{id:int}/prompt")]
    public async Task<ActionResult> GetPrompt(int id, [FromQuery] int? maxCases, [FromQuery] string? language)
    {
        if (await ReadSuiteAsync(id) is not { } suite) return NotFoundSuite(id);
        if (await access.GetAsync(User, suite.ProjectId) == ProjectAccess.None) return NotFoundSuite(id);

        return Ok(new
        {
            prompt = TestCasePrompt.PastePrompt(
                suite.BusinessDescription ?? string.Empty, suite.Screens.Count, BuildOptions(maxCases, null, language))
        });
    }

    // ---------- Helpers ----------

    /// <summary>The suite, or null — with the same "not found" answer for a project you cannot see.</summary>
    private async Task<TestSuite?> ReadSuiteAsync(int id)
    {
        var suite = await suites.GetByIdAsync(id);
        if (suite == null) return null;
        return await access.GetAsync(User, suite.ProjectId) == ProjectAccess.None ? null : suite;
    }

    /// <summary>Stored screenshots as the provider wants them: real bytes, real type, capped width.</summary>
    private async Task<List<GenerateImage>> LoadImagesAsync(TestSuite suite)
    {
        var images = new List<GenerateImage>();
        foreach (var screen in suite.Screens)
        {
            var file = await attachments.GetContentAsync(screen.AttachmentId);
            if (file == null) continue;
            if (file.Content.Length > settings.MaxImageBytes)
                throw new TestCaseGeneratorException(
                    $"Screenshot \"{file.FileName}\" is {Mb(file.Content.Length)} MB; the limit is {Mb(settings.MaxImageBytes)} MB. Remove it or upload a smaller copy.");

            string contentType = Avatars.Sniff(file.Content) ?? file.ContentType;
            images.Add(new GenerateImage(file.FileName, contentType,
                TestCaseImages.FitToWidth(file.Content, settings.MaxImageWidth)));
        }
        return images;
    }

    /// <summary>What to ask for: the caller's choices, clamped to what this installation allows.</summary>
    private GenerateOptions BuildOptions(int? maxCases, string[]? categories, string? language) => new()
    {
        MaxCases = Math.Clamp(maxCases ?? settings.MaxCases, 1, Math.Max(1, settings.MaxCases)),
        Categories = (categories ?? [])
            .Select(TestCategories.Normalise)
            .Where(c => c != null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray(),
        Language = string.IsNullOrWhiteSpace(language) ? "English" : language.Trim()
    };

    private async Task<Folder?> FolderOfAsync(int projectId, int folderId) =>
        await folders.GetByIdAsync(folderId) is { ProjectId: int p } folder && p == projectId ? folder : null;

    /// <summary>Keeps a stored name to its last path segment, so nothing looks like a path.</summary>
    private static string SafeName(string? name)
    {
        string trimmed = Path.GetFileName(name ?? string.Empty).Trim();
        return string.IsNullOrEmpty(trimmed) ? "screenshot" : trimmed[..Math.Min(trimmed.Length, 255)];
    }

    private static string Mb(long bytes) => Math.Round(bytes / (1024d * 1024d), 1).ToString("0.#");

    private static string Minutes(TimeSpan span) =>
        span.TotalMinutes < 1 ? "under a minute" : $"{Math.Ceiling(span.TotalMinutes):0} minutes";

    private NotFoundObjectResult NotFoundProject(int id) => NotFound(new { message = $"Project #{id} not found." });

    private NotFoundObjectResult NotFoundSuite(int id) => NotFound(new { message = $"Test suite #{id} not found." });
}
