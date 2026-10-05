using System.ComponentModel.DataAnnotations;

namespace QaDocBackend.Models;

public static class QcDocumentKinds
{
    public const string Documentation = "Documentation";
    public const string UserManual = "UserManual";

    public static readonly string[] All = [Documentation, UserManual];

    public static bool IsValid(string? kind) =>
        All.Contains(kind ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    public static string Normalise(string? kind) =>
        All.FirstOrDefault(k => string.Equals(k, kind?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Documentation;
}

public static class QcDocumentStatuses
{
    public const string Draft = "Draft";
    public const string Approved = "Approved";

    public static readonly string[] All = [Draft, Approved];

    public static bool IsValid(string? status) =>
        All.Contains(status ?? string.Empty, StringComparer.OrdinalIgnoreCase);
}

public static class QcDocumentSources
{
    public const string Ai = "Ai";
    public const string Imported = "Imported";
    public const string Manual = "Manual";

    public static readonly string[] All = [Ai, Imported, Manual];
}

/// <summary>
/// A collection of ordered screenshots for an application, used to generate product documentation
/// and user manuals.
/// </summary>
public class QcDocSet
{
    public int DocSetId { get; set; }
    public int Id => DocSetId;
    public int ProjectId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string? BusinessDescription { get; set; }
    public string Language { get; set; } = "en";
    public int? LogoAttachmentId { get; set; }
    public int? CreatedBy { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsDemo { get; set; }

    public int ScreenCount { get; set; }
    public int DocumentCount { get; set; }
    public string? MyRole { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string ProjectCode { get; set; } = string.Empty;

    public List<QcDocScreen> Screens { get; set; } = new();
    public List<QcDocument> Documents { get; set; } = new();
}

/// <summary>
/// One screenshot in a docset, arranged in a specific sequence (SortOrder).
/// Stores the AI-extracted screen summary so subsequent runs or user manual generations
/// do not need to re-read images through vision AI.
/// </summary>
public class QcDocScreen
{
    public int ScreenId { get; set; }
    public int Id => ScreenId;
    public int DocSetId { get; set; }
    public int SortOrder { get; set; }
    public string? Caption { get; set; }
    public int AttachmentId { get; set; }
    public string? ScreenSummary { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public long ByteSize { get; set; }
}

/// <summary>
/// Versioned product documentation or user manual markdown generated from a docset.
/// </summary>
public class QcDocument
{
    public int DocumentId { get; set; }
    public int Id => DocumentId;
    public int DocSetId { get; set; }
    public string Kind { get; set; } = QcDocumentKinds.Documentation;
    public int Version { get; set; } = 1;
    public string Markdown { get; set; } = string.Empty;
    public string Status { get; set; } = QcDocumentStatuses.Draft;
    public string Source { get; set; } = QcDocumentSources.Ai;
    public int? GeneratedBy { get; set; }
    public string? GeneratedByName { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Structured screen analysis returned by the vision model for one screen.
/// </summary>
public class QcScreenSummary
{
    public string ScreenTitle { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public List<QcVisibleElement> VisibleElements { get; set; } = new();
    public List<string> UserActions { get; set; } = new();
    public string NavigatesTo { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public class QcVisibleElement
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

// ---------- Request DTOs ----------

public class CreateQcDocSetRequest
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Title { get; set; } = string.Empty;

    [Required, StringLength(200, MinimumLength = 1)]
    public string AppName { get; set; } = string.Empty;

    [StringLength(50_000)]
    public string? Description { get; set; }

    [StringLength(10)]
    public string Language { get; set; } = "en";

    /// <summary>Optional captions for each uploaded image, aligned with the images array.</summary>
    public List<string>? Captions { get; set; }
}

public class UpdateQcDocSetRequest
{
    [StringLength(200, MinimumLength = 1)]
    public string? Title { get; set; }

    [StringLength(200, MinimumLength = 1)]
    public string? AppName { get; set; }

    [StringLength(50_000)]
    public string? Description { get; set; }

    [StringLength(10)]
    public string? Language { get; set; }

    public int? LogoAttachmentId { get; set; }
    public bool ClearLogo { get; set; }
}

public class ScreenOrderItem
{
    public int ScreenId { get; set; }
    public int SortOrder { get; set; }
    public string? Caption { get; set; }
}

public class ReorderScreensRequest
{
    [Required]
    public List<ScreenOrderItem> Screens { get; set; } = new();
}

public class GenerateQcDocumentRequest
{
    /// <summary>Documentation or UserManual.</summary>
    public string Kind { get; set; } = QcDocumentKinds.Documentation;

    /// <summary>Force re-reading screenshots with vision AI even if screen summaries already exist.</summary>
    public bool ReReadScreens { get; set; } = false;
}

public class ImportQcDocumentRequest
{
    [Required]
    public string Kind { get; set; } = QcDocumentKinds.Documentation;

    [Required(ErrorMessage = "Paste the markdown document.")]
    public string Markdown { get; set; } = string.Empty;
}

public class UpdateQcDocumentRequest
{
    public string? Markdown { get; set; }
    public string? Status { get; set; }
}

public class QcJobStatus
{
    public string JobId { get; set; } = string.Empty;
    public int DocSetId { get; set; }
    public string Kind { get; set; } = QcDocumentKinds.Documentation;
    public string Status { get; set; } = "Pending"; // Pending, Running, Completed, Failed
    public string Progress { get; set; } = string.Empty;
    public int CurrentStep { get; set; }
    public int TotalSteps { get; set; }
    public string? Error { get; set; }
    public int? DocumentId { get; set; }
}
