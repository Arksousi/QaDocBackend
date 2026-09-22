using System.ComponentModel.DataAnnotations;

namespace QaDocBackend.Models;

public static class TicketTypes
{
    public const string Bug = "Bug";
    public const string Enhancement = "Enhancement";
    public const string Issue = "Issue";

    public const string Message = "Type must be Bug, Enhancement or Issue.";
}

public class Ticket
{
    public int TicketId { get; set; }
    public int ProjectId { get; set; }
    public int FolderId { get; set; }
    /// <summary>Number within the folder; the trailing 0001 of the key.</summary>
    public int Sequence { get; set; }
    /// <summary>The key people quote, e.g. RMS-V1-0001. Built from the codes, never stored.</summary>
    public string TicketKey { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public string FolderCode { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>"Bug", "Enhancement" or "Issue".</summary>
    public string TicketType { get; set; } = TicketTypes.Bug;
    public int? AssignedToUserId { get; set; }
    public string? AssignedToName { get; set; }
    /// <summary>Who gave it to the current assignee. Null while the ticket is unassigned.</summary>
    public string? AssignedByName { get; set; }
    public string State { get; set; } = "Open";
    public int Priority { get; set; } = 3;
    public string Impact { get; set; } = "Medium";
    public DateTime CreatedAt { get; set; }
    public DateTime ActivityDate { get; set; }
    public string? CreatedByName { get; set; }
    public string? UpdatedByName { get; set; }
    public List<string> Tags { get; set; } = new();
    public int CommentCount { get; set; }

    // Filled only when a single ticket is requested
    public string? ProjectName { get; set; }
    public List<TicketComment> Comments { get; set; } = new();
    public List<TicketHistoryEntry> History { get; set; } = new();
}

public class TicketComment
{
    public int CommentId { get; set; }
    public int TicketId { get; set; }
    public int? AuthorUserId { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class TicketHistoryEntry
{
    public int HistoryId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTime ChangedAt { get; set; }
}

/// <summary>Body for creating (with FolderId) or updating (FolderId ignored) a ticket.</summary>
public class SaveTicketRequest
{
    /// <summary>Which folder the new ticket belongs to. Its project decides access and the key.</summary>
    public int FolderId { get; set; }

    [Required, StringLength(200, MinimumLength = 1)]
    public string Title { get; set; } = string.Empty;

    /// <summary>HTML; pictures are embedded as data URLs, hence the large limit.</summary>
    [StringLength(MaxDescriptionLength, ErrorMessage = "The description is too large. Use fewer or smaller pictures.")]
    public string? Description { get; set; }

    public const int MaxDescriptionLength = 15_000_000;

    /// <summary>Null means unassigned. Must be an active user.</summary>
    public int? AssignedToUserId { get; set; }

    [AllowedValues(TicketTypes.Bug, TicketTypes.Enhancement, TicketTypes.Issue, ErrorMessage = TicketTypes.Message)]
    public string TicketType { get; set; } = TicketTypes.Bug;

    [AllowedValues("Open", "In Progress", "Resolved", "Retest", "Closed", ErrorMessage = "State must be Open, In Progress, Resolved, Retest or Closed.")]
    public string State { get; set; } = "Open";

    [Range(1, 4, ErrorMessage = "Priority must be between 1 and 4.")]
    public int Priority { get; set; } = 3;

    [AllowedValues("Low", "Medium", "High", "Critical", "Showstopper",
        ErrorMessage = "Impact must be Low, Medium, High, Critical or Showstopper.")]
    public string Impact { get; set; } = "Medium";

    public List<string> Tags { get; set; } = new();
}

public class AddCommentRequest
{
    [Required, MinLength(1)]
    public string Text { get; set; } = string.Empty;
}

public enum UpdateOutcome
{
    NotFound,
    Unchanged,
    Updated
}
