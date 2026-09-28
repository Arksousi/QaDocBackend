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

    /// <summary>
    /// What the requesting user may do here ("Viewer"/"Contributor"/"Manager"), so the UI can hide
    /// what they cannot do. Manager is never a stored role: it means an Admin, or a Leader who contributes.
    /// </summary>
    public string? MyRole { get; set; }
}

/// <summary>One project on the Leader Dashboard: the usual summary plus how each member is doing.</summary>
public class ProjectScoreboard : Project
{
    public List<MemberScore> Members { get; set; } = new();
}

/// <summary>
/// A member's share of the project's tickets. Finished means Closed, the same line the project's
/// open count draws, so a full bar and the circle never disagree about what "done" is.
/// </summary>
/// <remarks>
/// Assigned and Closed are this project only. OpenTickets is their load across every project,
/// against TicketLimit: a Leader needs to see that someone is full even when this project is not
/// what filled them.
/// </remarks>
public record MemberScore(int UserId, string DisplayName, string Role, bool IsActive, int Assigned, int Closed,
    int OpenTickets = 0, int? TicketLimit = null);

/// <summary>
/// Who is asking for a list of projects. A guest is shown the demo projects and a real account is
/// shown the real ones, so the two can never appear in the same list.
/// </summary>
public readonly record struct Viewer(int UserId, bool IsAdmin, bool IsGuest, bool IsLeader = false);

public class CreateProjectRequest
{
    [Required, StringLength(150, MinimumLength = 1)]
    public string ProjectName { get; set; } = string.Empty;

    [Required, StringLength(Codes.MaxLength, MinimumLength = 1)]
    public string ProjectCode { get; set; } = string.Empty;
}

/// <summary>Body for moving a project between the guest tour and the real workspace.</summary>
public class SetDemoRequest
{
    public bool IsDemo { get; set; }
}

/// <summary>Values already used in a project, offered as suggestions in the UI.</summary>
public class ProjectSuggestions
{
    public List<string> Tags { get; set; } = new();
}
