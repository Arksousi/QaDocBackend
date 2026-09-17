using System.ComponentModel.DataAnnotations;

namespace QaDocBackend.Models;

public class Project
{
    public int ProjectId { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    /// <summary>First segment of every ticket key in this project, e.g. RMS.</summary>
    public string ProjectCode { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? CreatedByName { get; set; }

    // Read-only summary
    public int TicketCount { get; set; }
    public int OpenTicketCount { get; set; }

    /// <summary>Newest ticket activity, or the project's creation date when it has no tickets.</summary>
    public DateTime LastActivity { get; set; }

    /// <summary>The requesting user's role here ("Viewer"/"Contributor"/"Manager"), so the UI can hide what they cannot do.</summary>
    public string? MyRole { get; set; }
}

public class CreateProjectRequest
{
    [Required, StringLength(150, MinimumLength = 1)]
    public string ProjectName { get; set; } = string.Empty;

    [Required, StringLength(Codes.MaxLength, MinimumLength = 1)]
    public string ProjectCode { get; set; } = string.Empty;
}

/// <summary>Values already used in a project, offered as suggestions in the UI.</summary>
public class ProjectSuggestions
{
    public List<string> Tags { get; set; } = new();
}
