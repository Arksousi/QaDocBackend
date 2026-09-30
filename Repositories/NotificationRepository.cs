using Npgsql;
using QaDocBackend.Data;
using QaDocBackend.Models;

namespace QaDocBackend.Repositories;

public interface INotificationRepository
{
    /// <summary>The caller's newest notifications, limited to projects they can still open.</summary>
    Task<IEnumerable<Notification>> GetAsync(Viewer viewer, int top);
    Task<int> CountUnreadAsync(Viewer viewer);
    /// <summary>False when there is no such notification for this user, so another person's id looks missing.</summary>
    Task<bool> MarkReadAsync(int userId, int notificationId);
    Task MarkAllReadAsync(int userId);
}

/// <remarks>
/// Notifications are written by TicketRepository, inside the save that caused them. This class
/// only reads them and marks them read.
/// </remarks>
public class NotificationRepository(ISqlConnectionFactory db) : INotificationRepository
{
    /// <summary>
    /// The same visibility rule as the project list: an Admin sees every real project, anyone else
    /// only those with a membership row, and nobody sees the demo. A person removed from a project
    /// therefore stops seeing notifications that would only link to a "not found".
    /// </summary>
    private const string Visible = @"
        FROM Notifications n
        JOIN Tickets t  ON t.TicketId = n.TicketId
        JOIN Folders f  ON f.FolderId = t.FolderId
        JOIN Projects p ON p.ProjectId = t.ProjectId
        LEFT JOIN Users a ON a.UserId = n.ActorUserId
        WHERE n.UserId = @UserId AND NOT p.IsDemo
          AND (@IsAdmin OR EXISTS (SELECT 1 FROM ProjectMembers m WHERE m.ProjectId = p.ProjectId AND m.UserId = @UserId))";

    public async Task<IEnumerable<Notification>> GetAsync(Viewer viewer, int top)
    {
        string sql = $@"
            SELECT n.NotificationId, t.TicketId, t.ProjectId,
                   p.ProjectCode || '-' || f.FolderCode || '-' || lpad(t.Sequence::text, 4, '0') AS TicketKey,
                   t.Title, a.DisplayName AS ActorName, n.CreatedAt, n.ReadAt IS NOT NULL AS IsRead, n.Kind
            {Visible}
            ORDER BY n.CreatedAt DESC, n.NotificationId DESC
            LIMIT @Top;";

        var list = new List<Notification>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        AddViewer(cmd, viewer);
        cmd.Parameters.AddInt("Top", top);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new Notification(
                r.Int("NotificationId"), r.Int("TicketId"), r.Int("ProjectId"), r.Str("TicketKey"), r.Str("Title"),
                r.NStr("ActorName"), r.Utc("CreatedAt"), r.Bool("IsRead"), r.Str("Kind")));
        }
        return list;
    }

    public async Task<int> CountUnreadAsync(Viewer viewer)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) {Visible} AND n.ReadAt IS NULL;", conn);
        AddViewer(cmd, viewer);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<bool> MarkReadAsync(int userId, int notificationId)
    {
        // COALESCE keeps the first read time, and still counts an already-read row as found.
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE Notifications SET ReadAt = COALESCE(ReadAt, now()) WHERE NotificationId = @Id AND UserId = @UserId;", conn);
        cmd.Parameters.AddInt("Id", notificationId);
        cmd.Parameters.AddInt("UserId", userId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task MarkAllReadAsync(int userId)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE Notifications SET ReadAt = now() WHERE UserId = @UserId AND ReadAt IS NULL;", conn);
        cmd.Parameters.AddInt("UserId", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void AddViewer(NpgsqlCommand cmd, Viewer viewer)
    {
        cmd.Parameters.AddInt("UserId", viewer.UserId);
        cmd.Parameters.AddBool("IsAdmin", viewer.IsAdmin);
    }
}
