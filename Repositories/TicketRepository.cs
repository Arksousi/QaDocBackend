using Npgsql;
using NpgsqlTypes;
using QaDocBackend.Data;
using QaDocBackend.Models;

namespace QaDocBackend.Repositories;

public interface ITicketRepository
{
    /// <summary>
    /// Ticket Viewer list, optionally narrowed to one folder. <paramref name="states"/>,
    /// <paramref name="types"/> and <paramref name="tags"/> are each an OR set within themselves and
    /// AND-ed with each other; null or empty means that field is not filtered at all.
    /// </summary>
    Task<IEnumerable<Ticket>> GetByProjectAsync(int projectId, int? folderId, string? search, string[]? states, string[]? types, string[]? tags, int? assignedToUserId);
    Task<Ticket?> GetByIdAsync(int ticketId);
    Task<int> CreateAsync(SaveTicketRequest request, int userId);
    Task<UpdateOutcome> UpdateAsync(int ticketId, SaveTicketRequest request, int userId);
    Task<bool> DeleteAsync(int ticketId);
    /// <returns>The saved comment, or null when the ticket does not exist.</returns>
    Task<TicketComment?> AddCommentAsync(int ticketId, string text, int userId);
}

public class TicketRepository(ISqlConnectionFactory db) : ITicketRepository
{
    /// <summary>
    /// TicketKey is assembled here rather than stored, so renaming a project or folder code
    /// renames its keys instead of leaving the two out of step.
    /// </summary>
    private const string TicketSelect = @"
        SELECT t.TicketId, t.ProjectId, t.FolderId, t.Sequence, t.Title,
               f.FolderName, f.FolderCode,
               p.ProjectCode || '-' || f.FolderCode || '-' || lpad(t.Sequence::text, 4, '0') AS TicketKey,
               t.TicketType, t.AssignedToUserId, a.DisplayName AS AssignedToName,
               ab.DisplayName AS AssignedByName,
               t.State, t.Priority, t.Impact, t.CreatedAt, t.ActivityDate,
               cb.DisplayName AS CreatedByName, ub.DisplayName AS UpdatedByName,
               (SELECT COUNT(*) FROM TicketComments c WHERE c.TicketId = t.TicketId) AS CommentCount
        FROM Tickets t
        JOIN Folders f     ON f.FolderId = t.FolderId
        JOIN Projects p    ON p.ProjectId = t.ProjectId
        LEFT JOIN Users a  ON a.UserId  = t.AssignedToUserId
        LEFT JOIN Users ab ON ab.UserId = t.AssignedByUserId
        LEFT JOIN Users cb ON cb.UserId = t.CreatedByUserId
        LEFT JOIN Users ub ON ub.UserId = t.UpdatedByUserId";

    public async Task<IEnumerable<Ticket>> GetByProjectAsync(
        int projectId, int? folderId, string? search, string[]? states, string[]? types, string[]? tags, int? assignedToUserId)
    {
        string sql = $@"
            {TicketSelect}
            WHERE t.ProjectId = @ProjectId
              AND (@FolderId::int IS NULL OR t.FolderId = @FolderId)
              -- Nothing ticked means no filter at all, the same as ticking every one. A ticket
              -- carrying any one of the ticked tags matches; it does not need all of them.
              AND (@States::text[] IS NULL OR t.State = ANY(@States))
              AND (@Types::text[] IS NULL OR t.TicketType = ANY(@Types))
              AND (@AssignedTo::int IS NULL OR t.AssignedToUserId = @AssignedTo)
              AND (@Tags::text[] IS NULL OR EXISTS (SELECT 1 FROM TicketTags tt WHERE tt.TicketId = t.TicketId AND lower(tt.Tag) = ANY(@Tags)))
              AND (@Search::text IS NULL
                   OR t.Title ILIKE '%' || @Search || '%'
                   OR a.DisplayName ILIKE '%' || @Search || '%'
                   OR p.ProjectCode || '-' || f.FolderCode || '-' || lpad(t.Sequence::text, 4, '0') ILIKE '%' || @Search || '%'
                   OR t.Sequence::text = @SearchId
                   OR t.TicketId::text = @SearchId)
            ORDER BY t.ActivityDate DESC, t.TicketId DESC;";

        var searchText = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        await using var conn = await db.OpenAsync();

        var tickets = new List<Ticket>();
        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.AddInt("ProjectId", projectId);
            cmd.Parameters.AddInt("FolderId", folderId);
            cmd.Parameters.AddTextArray("States", states is { Length: > 0 } ? states : null);
            cmd.Parameters.AddTextArray("Types", types is { Length: > 0 } ? types : null);
            cmd.Parameters.AddInt("AssignedTo", assignedToUserId);
            // Lowered here rather than in SQL so the comparison stays sargable against ANY().
            cmd.Parameters.AddTextArray("Tags", tags is { Length: > 0 } ? [.. tags.Select(t => t.Trim().ToLowerInvariant())] : null);
            cmd.Parameters.AddText("Search", searchText == null ? null : SqlExtensions.EscapeLike(searchText));
            // "#57" and "57" both find ticket 57
            cmd.Parameters.AddText("SearchId", searchText?.TrimStart('#'));

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tickets.Add(MapTicket(reader));
        }
        if (tickets.Count == 0) return tickets;

        var byId = tickets.ToDictionary(t => t.TicketId);
        await using (var tagCmd = new NpgsqlCommand("SELECT TicketId, Tag FROM TicketTags WHERE TicketId = ANY(@Ids) ORDER BY Tag;", conn))
        {
            tagCmd.Parameters.Add(new NpgsqlParameter("Ids", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = byId.Keys.ToArray() });
            await using var reader = await tagCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                byId[reader.Int("TicketId")].Tags.Add(reader.Str("Tag"));
        }
        return tickets;
    }

    public async Task<Ticket?> GetByIdAsync(int ticketId)
    {
        string sql = $@"
            SELECT x.*, p.ProjectName, d.Description FROM ({TicketSelect} WHERE t.TicketId = @TicketId) x
            JOIN Projects p ON p.ProjectId = x.ProjectId
            JOIN Tickets d ON d.TicketId = x.TicketId;

            SELECT Tag FROM TicketTags WHERE TicketId = @TicketId ORDER BY Tag;

            SELECT c.CommentId, c.TicketId, c.AuthorUserId, COALESCE(u.DisplayName, 'Unknown user') AS AuthorName, c.Text, c.CreatedAt
            FROM TicketComments c LEFT JOIN Users u ON u.UserId = c.AuthorUserId
            WHERE c.TicketId = @TicketId ORDER BY c.CreatedAt, c.CommentId;

            SELECT h.HistoryId, COALESCE(u.DisplayName, 'Unknown user') AS UserName, h.Field, h.OldValue, h.NewValue, h.ChangedAt
            FROM TicketHistory h LEFT JOIN Users u ON u.UserId = h.UserId
            WHERE h.TicketId = @TicketId ORDER BY h.ChangedAt DESC, h.HistoryId DESC;";

        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("TicketId", ticketId);
        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync()) return null;
        var ticket = MapTicket(reader);
        ticket.ProjectName = reader.Str("ProjectName");
        // Only loaded for a single ticket: it can hold large embedded pictures.
        ticket.Description = reader.NStr("Description");

        await reader.NextResultAsync();
        while (await reader.ReadAsync()) ticket.Tags.Add(reader.Str("Tag"));

        await reader.NextResultAsync();
        while (await reader.ReadAsync()) ticket.Comments.Add(MapComment(reader));

        await reader.NextResultAsync();
        while (await reader.ReadAsync())
        {
            ticket.History.Add(new TicketHistoryEntry
            {
                HistoryId = reader.Int("HistoryId"),
                UserName = reader.Str("UserName"),
                Field = reader.Str("Field"),
                OldValue = reader.NStr("OldValue"),
                NewValue = reader.NStr("NewValue"),
                ChangedAt = reader.Utc("ChangedAt")
            });
        }
        return ticket;
    }

    public async Task<int> CreateAsync(SaveTicketRequest request, int userId)
    {
        const string sql = @"
            INSERT INTO Tickets (ProjectId, FolderId, Sequence, Title, Description, TicketType, AssignedToUserId, AssignedByUserId, State, Priority, Impact, CreatedByUserId, UpdatedByUserId)
            VALUES (@ProjectId, @FolderId, @Sequence, @Title, @Description, @TicketType, @AssignedTo, @AssignedBy, @State, @Priority, @Impact, @UserId, @UserId)
            RETURNING TicketId;";

        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Claim the next number inside the transaction. The UPDATE locks the folder row, so two
        // people creating a ticket at once queue up instead of both taking the same sequence —
        // which MAX(Sequence) + 1 would do.
        int projectId;
        int sequence;
        await using (var cmd = new NpgsqlCommand(
            "UPDATE Folders SET NextSequence = NextSequence + 1 WHERE FolderId = @FolderId RETURNING ProjectId, NextSequence - 1;", conn, tx))
        {
            cmd.Parameters.AddInt("FolderId", request.FolderId);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new InvalidOperationException($"Folder #{request.FolderId} does not exist.");
            projectId = reader.GetInt32(0);
            sequence = reader.GetInt32(1);
        }

        int newId;
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            AddTicketParams(cmd, request);
            // Creating a ticket already assigned counts as assigning it.
            cmd.Parameters.AddInt("AssignedBy", request.AssignedToUserId == null ? null : userId);
            cmd.Parameters.AddInt("ProjectId", projectId);
            cmd.Parameters.AddInt("FolderId", request.FolderId);
            cmd.Parameters.AddInt("Sequence", sequence);
            cmd.Parameters.AddInt("UserId", userId);
            newId = (int)(await cmd.ExecuteScalarAsync())!;
        }
        await ReplaceTagsAsync(conn, tx, newId, request.Tags);
        await AddHistoryAsync(conn, tx, newId, userId, [("Created", null, null)]);
        await tx.CommitAsync();
        return newId;
    }

    public async Task<UpdateOutcome> UpdateAsync(int ticketId, SaveTicketRequest request, int userId)
    {
        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Lock the row so concurrent saves record history against the right "old" values.
        var before = await ReadForUpdateAsync(conn, tx, ticketId);
        if (before == null) return UpdateOutcome.NotFound;

        string? newAssignee = request.AssignedToUserId == before.AssignedToUserId
            ? before.AssignedToName
            : await DisplayNameAsync(conn, tx, request.AssignedToUserId);

        var changes = new List<(string Field, string? Old, string? New)>();
        void Track(string field, string? oldValue, string? newValue)
        {
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal)) changes.Add((field, oldValue, newValue));
        }
        Track("Title", before.Title, request.Title.Trim());
        // Descriptions can contain pictures, so history records that it changed but not the text.
        if (!string.Equals(before.Description, CleanDescription(request.Description), StringComparison.Ordinal))
            changes.Add(("Description", null, null));
        bool assigneeChanged = request.AssignedToUserId != before.AssignedToUserId;
        if (assigneeChanged)
            changes.Add(("Assigned To", before.AssignedToName ?? "Unassigned", newAssignee ?? "Unassigned"));
        Track("Type", before.TicketType, request.TicketType);
        Track("State", before.State, request.State);
        Track("Priority", before.Priority.ToString(), request.Priority.ToString());
        Track("Impact", before.Impact, request.Impact);
        Track("Tag", string.Join(", ", before.Tags.Order(StringComparer.OrdinalIgnoreCase)),
                     string.Join(", ", request.Tags.Order(StringComparer.OrdinalIgnoreCase)));

        if (changes.Count == 0) return UpdateOutcome.Unchanged; // disposing the transaction rolls it back

        const string sql = @"
            UPDATE Tickets
            SET Title = @Title, Description = @Description, TicketType = @TicketType,
                AssignedToUserId = @AssignedTo, AssignedByUserId = @AssignedBy, State = @State,
                Priority = @Priority, Impact = @Impact, UpdatedByUserId = @UserId, ActivityDate = now()
            WHERE TicketId = @TicketId;";
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            AddTicketParams(cmd, request);
            // Only a change of assignee changes who assigned it; other edits leave that alone.
            int? assignedBy = request.AssignedToUserId == null ? null
                : assigneeChanged ? userId
                : before.AssignedByUserId;
            cmd.Parameters.AddInt("AssignedBy", assignedBy);
            cmd.Parameters.AddInt("TicketId", ticketId);
            cmd.Parameters.AddInt("UserId", userId);
            await cmd.ExecuteNonQueryAsync();
        }

        await ReplaceTagsAsync(conn, tx, ticketId, request.Tags);
        await AddHistoryAsync(conn, tx, ticketId, userId, changes);
        await tx.CommitAsync();
        return UpdateOutcome.Updated;
    }

    public async Task<bool> DeleteAsync(int ticketId)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM Tickets WHERE TicketId = @TicketId;", conn);
        cmd.Parameters.AddInt("TicketId", ticketId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<TicketComment?> AddCommentAsync(int ticketId, string text, int userId)
    {
        // Posting a comment counts as ticket activity (but not as a field change).
        // Nothing is inserted when the ticket doesn't exist.
        const string sql = @"
            WITH touched AS (
                UPDATE Tickets SET ActivityDate = now() WHERE TicketId = @TicketId RETURNING TicketId
            ), inserted AS (
                INSERT INTO TicketComments (TicketId, AuthorUserId, Text)
                SELECT TicketId, @UserId, @Text FROM touched
                RETURNING CommentId, TicketId, AuthorUserId, Text, CreatedAt
            )
            SELECT i.CommentId, i.TicketId, i.AuthorUserId, u.DisplayName AS AuthorName, i.Text, i.CreatedAt
            FROM inserted i LEFT JOIN Users u ON u.UserId = i.AuthorUserId;";

        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("TicketId", ticketId);
        cmd.Parameters.AddInt("UserId", userId);
        cmd.Parameters.AddText("Text", text.Trim());

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapComment(reader) : null;
    }

    private sealed record Snapshot(string Title, string? Description, string TicketType, int? AssignedToUserId,
        string? AssignedToName, int? AssignedByUserId, string State, int Priority, string Impact, List<string> Tags);

    private static async Task<Snapshot?> ReadForUpdateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int ticketId)
    {
        const string sql = @"
            SELECT t.Title, t.Description, t.TicketType, t.AssignedToUserId, u.DisplayName AS AssignedToName,
                   t.AssignedByUserId, t.State, t.Priority, t.Impact
            FROM Tickets t LEFT JOIN Users u ON u.UserId = t.AssignedToUserId
            WHERE t.TicketId = @TicketId
            FOR UPDATE OF t;
            SELECT Tag FROM TicketTags WHERE TicketId = @TicketId;";
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddInt("TicketId", ticketId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        var snapshot = new Snapshot(r.Str("Title"), r.NStr("Description"), r.Str("TicketType"), r.NInt("AssignedToUserId"),
                                    r.NStr("AssignedToName"), r.NInt("AssignedByUserId"),
                                    r.Str("State"), r.Int("Priority"), r.Str("Impact"), []);
        await r.NextResultAsync();
        while (await r.ReadAsync()) snapshot.Tags.Add(r.Str("Tag"));
        return snapshot;
    }

    private static async Task<string?> DisplayNameAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int? userId)
    {
        if (userId == null) return null;
        await using var cmd = new NpgsqlCommand("SELECT DisplayName FROM Users WHERE UserId = @UserId;", conn, tx);
        cmd.Parameters.AddInt("UserId", userId);
        return (string?)await cmd.ExecuteScalarAsync();
    }

    private static async Task AddHistoryAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int ticketId, int userId,
        IEnumerable<(string Field, string? Old, string? New)> changes)
    {
        foreach (var (field, oldValue, newValue) in changes)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO TicketHistory (TicketId, UserId, Field, OldValue, NewValue) VALUES (@TicketId, @UserId, @Field, @Old, @New);",
                conn, tx);
            cmd.Parameters.AddInt("TicketId", ticketId);
            cmd.Parameters.AddInt("UserId", userId);
            cmd.Parameters.AddText("Field", field);
            cmd.Parameters.AddText("Old", string.IsNullOrEmpty(oldValue) ? null : oldValue);
            cmd.Parameters.AddText("New", string.IsNullOrEmpty(newValue) ? null : newValue);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task ReplaceTagsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int ticketId, IEnumerable<string> tags)
    {
        await using (var del = new NpgsqlCommand("DELETE FROM TicketTags WHERE TicketId = @TicketId;", conn, tx))
        {
            del.Parameters.AddInt("TicketId", ticketId);
            await del.ExecuteNonQueryAsync();
        }

        foreach (var tag in tags)
        {
            await using var ins = new NpgsqlCommand("INSERT INTO TicketTags (TicketId, Tag) VALUES (@TicketId, @Tag);", conn, tx);
            ins.Parameters.AddInt("TicketId", ticketId);
            ins.Parameters.AddText("Tag", tag);
            await ins.ExecuteNonQueryAsync();
        }
    }

    private static void AddTicketParams(NpgsqlCommand cmd, SaveTicketRequest r)
    {
        cmd.Parameters.AddText("Title", r.Title.Trim());
        cmd.Parameters.AddText("Description", CleanDescription(r.Description));
        cmd.Parameters.AddText("TicketType", r.TicketType);
        cmd.Parameters.AddInt("AssignedTo", r.AssignedToUserId);
        cmd.Parameters.AddText("State", r.State);
        cmd.Parameters.AddInt("Priority", r.Priority);
        cmd.Parameters.AddText("Impact", r.Impact);
    }

    /// <summary>Blank descriptions are stored as NULL; trailing whitespace is dropped.</summary>
    private static string? CleanDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.TrimEnd();

    private static Ticket MapTicket(NpgsqlDataReader r) => new()
    {
        TicketId = r.Int("TicketId"),
        ProjectId = r.Int("ProjectId"),
        FolderId = r.Int("FolderId"),
        Sequence = r.Int("Sequence"),
        TicketKey = r.Str("TicketKey"),
        FolderName = r.Str("FolderName"),
        FolderCode = r.Str("FolderCode"),
        Title = r.Str("Title"),
        TicketType = r.Str("TicketType"),
        AssignedToUserId = r.NInt("AssignedToUserId"),
        AssignedToName = r.NStr("AssignedToName"),
        AssignedByName = r.NStr("AssignedByName"),
        State = r.Str("State"),
        Priority = r.Int("Priority"),
        Impact = r.Str("Impact"),
        CreatedAt = r.Utc("CreatedAt"),
        ActivityDate = r.Utc("ActivityDate"),
        CreatedByName = r.NStr("CreatedByName"),
        UpdatedByName = r.NStr("UpdatedByName"),
        CommentCount = r.Int("CommentCount")
    };

    private static TicketComment MapComment(NpgsqlDataReader r) => new()
    {
        CommentId = r.Int("CommentId"),
        TicketId = r.Int("TicketId"),
        AuthorUserId = r.NInt("AuthorUserId"),
        AuthorName = r.Str("AuthorName"),
        Text = r.Str("Text"),
        CreatedAt = r.Utc("CreatedAt")
    };
}
