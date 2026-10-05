using System.ComponentModel.DataAnnotations;

namespace QaDocBackend.Models;

/// <summary>The four buckets a test case may be filed under. Shown as chips and offered as filters.</summary>
public static class TestCategories
{
    public const string Functional = "Functional";
    public const string Negative = "Negative";
    public const string Boundary = "Boundary";
    public const string Ui = "UI";

    public static readonly string[] All = [Functional, Negative, Boundary, Ui];

    /// <summary>Canonical spelling of a category, or null when it is not one of the four.</summary>
    public static string? Normalise(string? value) =>
        All.FirstOrDefault(c => string.Equals(c, value?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool IsValid(string? value) => Normalise(value) != null;
}

/// <summary>Where a case came from. Draft is the state every new case lands in, however it arrived.</summary>
public static class TestStatuses
{
    public const string Draft = "Draft";
    public const string Approved = "Approved";
    public const string Passed = "Passed";
    public const string Failed = "Failed";

    public static readonly string[] All = [Draft, Approved, Passed, Failed];

    public static bool IsValid(string? value) => All.Contains(value ?? string.Empty, StringComparer.Ordinal);
}

/// <summary>Who wrote the case: a vision model, a pasted JSON import, or a person.</summary>
public static class TestSources
{
    public const string Ai = "Ai";
    public const string Imported = "Imported";
    public const string Manual = "Manual";

    public static readonly string[] All = [Ai, Imported, Manual];

    public static bool IsValid(string? value) => All.Contains(value ?? string.Empty, StringComparer.Ordinal);
}

/// <summary>
/// A set of screenshots plus a business description, handed to whatever provider the config names.
/// Everything the provider needs is already checked and resized by the controller.
/// </summary>
public class GenerateRequest
{
    public string BusinessDescription { get; set; } = string.Empty;
    public List<GenerateImage> Images { get; set; } = new();
    public GenerateOptions Options { get; set; } = new();
}

public record GenerateImage(string FileName, string ContentType, byte[] Content);

public class GenerateOptions
{
    /// <summary>How many cases to ask for at most. The parser never returns more than this.</summary>
    public int MaxCases { get; set; } = 20;

    /// <summary>Categories to cover; empty means all four.</summary>
    public string[] Categories { get; set; } = [];

    public string Language { get; set; } = "English";
}

/// <summary>Exactly the shape the model is told to return, and the shape it is parsed back into.</summary>
public class GeneratedTestCaseSet
{
    public List<GeneratedTestCase> TestCases { get; set; } = new();
}

public class GeneratedTestCase
{
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = TestCategories.Functional;
    public int Priority { get; set; } = 3;
    public string Preconditions { get; set; } = string.Empty;
    public List<string> Steps { get; set; } = new();
    public string Expected { get; set; } = string.Empty;
}

/// <summary>A suite of screenshots and the test cases drawn from them.</summary>
public class TestSuite
{
    public int SuiteId { get; set; }
    public int ProjectId { get; set; }
    /// <summary>Where its tickets go when one is created; the project's folder is used when null.</summary>
    public int? FolderId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? BusinessDescription { get; set; }
    public int? CreatedByUserId { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Copied from the project when the suite is created, so guests only reach demo suites.</summary>
    public bool IsDemo { get; set; }

    // List summaries
    public int ScreenCount { get; set; }
    public int CaseCount { get; set; }

    /// <summary>
    /// What the caller may do in the suite's project, so the UI can hide what they cannot do —
    /// the same field Project returns, and filled the same way (never stored).
    /// </summary>
    public string? MyRole { get; set; }

    // Filled only when one suite is requested
    public string ProjectName { get; set; } = string.Empty;
    public string ProjectCode { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public List<TestSuiteScreen> Screens { get; set; } = new();
    public List<TestCase> Cases { get; set; } = new();
}

/// <summary>One screenshot. The bytes stay in ticketattachments and are streamed by GET /api/attachments/{id}.</summary>
public class TestSuiteScreen
{
    public int ScreenId { get; set; }
    public int SuiteId { get; set; }
    public int AttachmentId { get; set; }
    public int SortOrder { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public long ByteSize { get; set; }
}

public class TestCase
{
    public int TestCaseId { get; set; }
    public int SuiteId { get; set; }
    /// <summary>Counter within the suite; the number people quote as TC-0001.</summary>
    public int Number { get; set; }
    /// <summary>Assembled on read, the way a ticket key is, so nothing has to be rewritten later.</summary>
    public string CaseKey => $"TC-{Number:0000}";
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = TestCategories.Functional;
    public int Priority { get; set; } = 3;
    public string Preconditions { get; set; } = string.Empty;
    public List<string> Steps { get; set; } = new();
    public string Expected { get; set; } = string.Empty;
    public string Status { get; set; } = TestStatuses.Draft;
    /// <summary>The QaDoc ticket this case became, null until somebody turns it into one.</summary>
    public int? LinkedTicketId { get; set; }
    public string? LinkedTicketKey { get; set; }
    public string Source { get; set; } = TestSources.Manual;
    public DateTime CreatedAt { get; set; }
}

/// <summary>Body for POST projects/{id}/testsuites (multipart: title, description, images).</summary>
public class CreateTestSuiteRequest
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Title { get; set; } = string.Empty;

    /// <summary>What the app does and what matters about it — the context the model is given.</summary>
    [StringLength(TestSuiteLimits.MaxDescriptionLength)]
    public string? Description { get; set; }

    /// <summary>Where tickets created from this suite go. The project's folder is used when absent.</summary>
    public int? FolderId { get; set; }
}

public static class TestSuiteLimits
{
    public const int MaxDescriptionLength = 20_000;
    public const int MaxTitleLength = 200;
    public const int MaxTitleCases = 50;
    public const int MaxSteps = 20;
    public const int MaxStepLength = 500;
    public const int MaxFieldLength = 2_000;
}

/// <summary>Body for POST testsuites/{id}/import: the JSON the user pasted back from any chat AI.</summary>
public class ImportTestCasesRequest
{
    [Required(ErrorMessage = "Paste the JSON your chat AI returned.")]
    public string Json { get; set; } = string.Empty;
}

/// <summary>Body for PUT testcases/{id}. Absent fields are left as they are.</summary>
public class UpdateTestCaseRequest
{
    [StringLength(TestSuiteLimits.MaxTitleLength, MinimumLength = 1)]
    public string? Title { get; set; }

    public string? Category { get; set; }

    [Range(1, 4, ErrorMessage = "Priority must be between 1 and 4.")]
    public int? Priority { get; set; }

    public string? Preconditions { get; set; }

    public List<string>? Steps { get; set; }

    public string? Expected { get; set; }

    public string? Status { get; set; }
}

/// <summary>Optional body for POST testsuites/{id}/generate; every field has a sensible default.</summary>
public class GenerateTestCasesRequest
{
    /// <summary>How many cases to ask for at most (1–50).</summary>
    [Range(1, 50, ErrorMessage = "Max cases must be between 1 and 50.")]
    public int? MaxCases { get; set; }

    /// <summary>Which of the four categories to cover; empty means all of them.</summary>
    public string[]? Categories { get; set; }

    public string? Language { get; set; }
}
