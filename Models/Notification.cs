namespace QaDocBackend.Models;

/// <summary>
/// "ActorName assigned TicketKey to you", as the bell shows it. Everything but the ids and
/// timestamps is joined in when read, so it always matches the ticket as it is now.
/// </summary>
public record Notification(
    int NotificationId, int TicketId, int ProjectId, string TicketKey, string Title,
    string? ActorName, DateTime CreatedAt, bool IsRead, string Kind);

public static class NotificationKinds
{
    public const string Assigned = "Assigned";
    public const string Mentioned = "Mentioned";
    /// <summary>The ticket was moved to Retest; sent to everyone assigned to it.</summary>
    public const string Retest = "Retest";
}

public record UnreadCount(int Count);
