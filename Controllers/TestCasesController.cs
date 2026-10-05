using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using QaDocBackend.Infrastructure;
using QaDocBackend.Models;
using QaDocBackend.Repositories;

namespace QaDocBackend.Controllers;

/// <summary>
/// A test case belongs to a suite, and a suite belongs to a project: every check here is that
/// project's, through ProjectAccessService. Editing and turning a case into a ticket both need
/// Contributor, so a Viewer (and, through GuestReadOnlyFilter, a guest) may only read.
/// </summary>
[ApiController]
[Route("api")]
public class TestCasesController(
    ITestSuiteRepository suites,
    ITicketRepository tickets,
    IFolderRepository folders,
    IProjectAccessService access) : ControllerBase
{
    /// <summary>Edits fields and moves the status (Draft → Approved → Passed/Failed).</summary>
    [HttpPut("testcases/{id:int}")]
    public async Task<ActionResult> Update(int id, [FromBody] UpdateTestCaseRequest request)
    {
        if (await ReadAsync(id) is not { } target) return NotFoundCase(id);
        if (await access.GetAsync(User, target.Suite.ProjectId) < ProjectAccess.Contributor) return Forbid();

        if (request.Title != null && string.IsNullOrWhiteSpace(request.Title))
            return ValidationProblem(detail: "Title is required.");
        if (request.Category != null && !TestCategories.IsValid(request.Category))
            return ValidationProblem(detail: $"Category must be one of: {string.Join(", ", TestCategories.All)}.");
        if (request.Status != null && !TestStatuses.IsValid(request.Status))
            return ValidationProblem(detail: $"Status must be one of: {string.Join(", ", TestStatuses.All)}.");

        await suites.UpdateCaseAsync(id, request);
        return NoContent();
    }

    /// <summary>
    /// Removes one case. A case that has already become a ticket keeps the ticket: only the link
    /// from this side goes.
    /// </summary>
    [HttpDelete("testcases/{id:int}")]
    public async Task<ActionResult> Delete(int id)
    {
        if (await ReadAsync(id) is not { } target) return NotFoundCase(id);
        if (await access.GetAsync(User, target.Suite.ProjectId) < ProjectAccess.Contributor) return Forbid();

        return await suites.DeleteCaseAsync(id) ? NoContent() : NotFoundCase(id);
    }

    /// <summary>
    /// Turns a case into a Bug through the ordinary ticket creation path — same numbering, same
    /// history, same rules — with the description pre-filled from the case, then links the two
    /// together so the row can show its key.
    /// </summary>
    [HttpPost("testcases/{id:int}/create-ticket")]
    public async Task<ActionResult> CreateTicket(int id)
    {
        if (await ReadAsync(id) is not { } target) return NotFoundCase(id);
        var (suite, testCase) = target;
        if (await access.GetAsync(User, suite.ProjectId) < ProjectAccess.Contributor) return Forbid();

        if (testCase.LinkedTicketId is { } existing)
            return Conflict(new { message = $"{testCase.CaseKey} already has ticket #{existing}." });

        var folder = await TargetFolderAsync(suite);
        if (folder == null)
            return ValidationProblem(detail: "This project has no folder to file the ticket into.");

        var created = await tickets.CreateAsync(new SaveTicketRequest
        {
            FolderId = folder.FolderId,
            Title = testCase.Title,
            Description = Describe(suite, testCase),
            TicketType = TicketTypes.Bug,
            State = "Open",
            Priority = Math.Clamp(testCase.Priority, 1, 4),
            Impact = "Medium",
            Tags = []
        }, User.GetUserId());

        await suites.LinkTicketAsync(testCase.TestCaseId, created);
        var ticket = await tickets.GetByIdAsync(created);
        return Ok(new { id = created, ticketKey = ticket?.TicketKey ?? string.Empty });
    }

    /// <summary>The case with the suite it belongs to, or null when either cannot be seen.</summary>
    private async Task<(TestSuite Suite, TestCase Case)?> ReadAsync(int caseId)
    {
        var testCase = await suites.GetCaseAsync(caseId);
        if (testCase == null) return null;
        var suite = await suites.GetByIdAsync(testCase.SuiteId);
        if (suite == null) return null;
        // A suite in a project the caller cannot see must look missing, not forbidden.
        if (await access.GetAsync(User, suite.ProjectId) == ProjectAccess.None) return null;
        return (suite, testCase);
    }

    /// <summary>The suite's folder, or the project's first one when the suite has none.</summary>
    private async Task<Folder?> TargetFolderAsync(TestSuite suite)
    {
        if (suite.FolderId is { } folderId && await folders.GetByIdAsync(folderId) is { } folder
            && folder.ProjectId == suite.ProjectId)
            return folder;

        return (await folders.GetByProjectAsync(suite.ProjectId)).OrderBy(f => f.FolderCode).FirstOrDefault();
    }

    /// <summary>
    /// The description the ticket opens with: the app's own Actual / Expected / Steps headings,
    /// filled in from the case, so the ticket reads like any other bug report.
    /// </summary>
    private static string Describe(TestSuite suite, TestCase c)
    {
        static string Esc(string value) => WebUtility.HtmlEncode(value);

        var sb = new StringBuilder();
        sb.Append("<h4 class=\"rte-section\">Actual Result</h4>");
        sb.Append("<p>").Append(Esc($"Test case {c.CaseKey} failed: {c.Title}")).Append("</p>");
        sb.Append("<p>").Append(Esc($"From test suite: {suite.Title}")).Append("</p>");
        if (!string.IsNullOrWhiteSpace(c.Preconditions))
            sb.Append("<p>").Append(Esc("Preconditions: " + c.Preconditions)).Append("</p>");

        sb.Append("<h4 class=\"rte-section\">Expected Result</h4>");
        sb.Append("<p>").Append(Esc(c.Expected)).Append("</p>");

        sb.Append("<h4 class=\"rte-section\">Steps to Reproduce</h4><ol>");
        foreach (var step in c.Steps)
            sb.Append("<li>").Append(Esc(step)).Append("</li>");
        sb.Append("</ol>");
        return sb.ToString();
    }

    private NotFoundObjectResult NotFoundCase(int id) => NotFound(new { message = $"Test case #{id} not found." });
}
