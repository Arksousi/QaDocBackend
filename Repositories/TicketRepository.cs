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
    /// <param name="mentionedUserIds">Already checked by the caller: people who may be mentioned here.</param>
    Task<TicketComment?> AddCommentAsync(int ticketId, string text, int userId, IReadOnlyCollection<int> mentionedUserIds);
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
               t.TicketType,
               t.State, t.Priority, t.Impact, t.CreatedAt, t.ActivityDate,
               cb.DisplayName AS CreatedByName, ub.DisplayName AS UpdatedByName,
               (SELECT COUNT(*) FROM TicketComments c WHERE c.TicketId = t.TicketId) AS CommentCount
        FROM Tickets t
        JOIN Folders f     ON f.FolderId = t.FolderId
        JOIN Projects p    ON p.ProjectId = t.ProjectId
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
              -- ""Assigned to me"" matches a ticket I share with others, not only one I hold alone.
              AND (@AssignedTo::int IS NULL OR EXISTS (SELECT 1 FROM TicketAssignees ta WHERE ta.TicketId = t.TicketId AND ta.UserId = @AssignedTo))
              AND (@Tags::text[] IS NULL OR EXISTS (SELECT 1 FROM TicketTags tt WHERE tt.TicketId = t.TicketId AND lower(tt.Tag) = ANY(@Tags)))
              AND (@Search::text IS NULL
                   OR t.Title ILIKE '%' || @Search || '%'
                   OR EXISTS (SELECT 1 FROM TicketAssignees ta JOIN Users au ON au.UserId = ta.UserId
                              WHERE ta.TicketId = t.TicketId AND au.DisplayName ILIKE '%' || @Search || '%')
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
        await using (var assigneeCmd = new NpgsqlCommand(SelectAssignees, conn))
        {
            assigneeCmd.Parameters.AddIntArray("Ids", byId.Keys.ToArray());
            await using var reader = await assigneeCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                byId[reader.Int("TicketId")].Assignees.Add(MapAssignee(reader));
        }
        return tickets;
    }

    /// <summary>In the order people were added, so the first name shown is whoever had it first.</summary>
    private const string SelectAssignees = @"
        SELECT ta.TicketId, ta.UserId, u.DisplayName, ab.DisplayName AS AssignedByName
        FROM TicketAssignees ta
        JOIN Users u ON u.UserId = ta.UserId
        LEFT JOIN Users ab ON ab.UserId = ta.AssignedByUserId
        WHERE ta.TicketId = ANY(@Ids)
        ORDER BY ta.AssignedAt, u.DisplayName;";

    private static TicketAssignee MapAssignee(NpgsqlDataReader r) =>
        new(r.Int("UserId"), r.Str("DisplayName"), r.NStr("AssignedByName"));

    public async Task<Ticket?> GetByIdAsync(int ticketId)
    {
        string sql = $@"
            SELECT x.*, p.ProjectName, d.Description FROM ({TicketSelect} WHERE t.TicketId = @TicketId) x
            JOIN Projects p ON p.ProjectId = x.ProjectId
            JOIN Tickets d ON d.TicketId = x.TicketId;

            SELECT Tag FROM TicketTags WHERE TicketId = @TicketId ORDER BY Tag;

            {SelectAssignees.Replace("ANY(@Ids)", "@TicketId")}

            SELECT c.CommentId, c.TicketId, c.AuthorUserId, COALESCE(u.DisplayName, 'Unknown user') AS AuthorName, c.Text, c.CreatedAt
            FROM TicketComments c LEFT JOIN Users u ON u.UserId = c.AuthorUserId
            WHERE c.TicketId = @TicketId ORDER BY c.CreatedAt, c.CommentId;

            SELECT cm.CommentId, cm.UserId, u.DisplayName
            FROM CommentMentions cm
            JOIN TicketComments c ON c.CommentId = cm.CommentId
            JOIN Users u ON u.UserId = cm.UserId
            WHERE c.TicketId = @TicketId;

            SELECT h.HistoryId, h.UserId, COALESCE(u.DisplayName, 'Unknown user') AS UserName, h.Field, h.OldValue, h.NewValue, h.ChangedAt
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
        while (await reader.ReadAsync()) ticket.Assignees.Add(MapAssignee(reader));

        await reader.NextResultAsync();
        while (await reader.ReadAsync()) ticket.Comments.Add(MapComment(reader));

        await reader.NextResultAsync();
        var byComment = ticket.Comments.ToDictionary(c => c.CommentId);
        while (await reader.ReadAsync())
        {
            if (byComment.TryGetValue(reader.Int("CommentId"), out var c))
                c.Mentions.Add(new CommentMention(reader.Int("UserId"), reader.Str("DisplayName")));
        }

        await reader.NextResultAsync();
        while (await reader.ReadAsync())
        {
            ticket.History.Add(new TicketHistoryEntry
            {
                HistoryId = reader.Int("HistoryId"),
                UserId = reader.NInt("UserId"),
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
            INSERT INTO Tickets (ProjectId, FolderId, Sequence, Title, Description, TicketType, State, Priority, Impact, CreatedByUserId, UpdatedByUserId)
            VALUES (@ProjectId, @FolderId, @Sequence, @Title, @Description, @TicketType, @State, @Priority, @Impact, @UserId, @UserId)
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
            cmd.Parameters.AddInt("ProjectId", projectId);
            cmd.Parameters.AddInt("FolderId", request.FolderId);
            cmd.Parameters.AddInt("Sequence", sequence);
            cmd.Parameters.AddInt("UserId", userId);
            newId = (int)(await cmd.ExecuteScalarAsync())!;
        }
        await ReplaceTagsAsync(conn, tx, newId, request.Tags);
        await AddHistoryAsync(conn, tx, newId, userId, [("Created", null, null)]);
        // Creating a ticket already assigned counts as assigning it, so the creator is who assigned them.
        await AddAssigneesAsync(conn, tx, newId, request.AssignedToUserIds ?? [], userId);
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

        // Only the difference is written: someone who stays on the ticket keeps who assigned them and when.
        var wanted = (request.AssignedToUserIds ?? []).Distinct().ToList();
        var added = wanted.Where(id => before.Assignees.All(a => a.UserId != id)).ToList();
        var removed = before.Assignees.Where(a => !wanted.Contains(a.UserId)).Select(a => a.UserId).ToList();
        bool assigneesChanged = added.Count > 0 || removed.Count > 0;

        var changes = new List<(string Field, string? Old, string? New)>();
        void Track(string field, string? oldValue, string? newValue)
        {
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal)) changes.Add((field, oldValue, newValue));
        }
        Track("Title", before.Title, request.Title.Trim());
        // Descriptions can contain pictures, so history records that it changed but not the text.
        if (!string.Equals(before.Description, CleanDescription(request.Description), StringComparison.Ordinal))
            changes.Add(("Description", null, null));
        if (assigneesChanged)
        {
            var names = await DisplayNamesAsync(conn, tx, added);
            var after = before.Assignees.Where(a => !removed.Contains(a.UserId)).Select(a => a.DisplayName)
                .Concat(added.Select(id => names.GetValueOrDefault(id, $"User #{id}")));
            changes.Add(("Assigned To", NameList(before.Assignees.Select(a => a.DisplayName)), NameList(after)));
        }
        Track("Type", before.TicketType, request.TicketType);
        Track("State", before.State, request.State);
        Track("Priority", before.Priority.ToString(), request.Priority.ToString());
        Track("Impact", before.Impact, request.Impact);
        Track("Tag", string.Join(", ", before.Tags.Order(StringComparer.OrdinalIgnoreCase)),
                     string.Join(", ", request.Tags.Order(StringComparer.OrdinalIgnoreCase)));

        if (changes.Count == 0) return UpdateOutcome.Unchanged; // disposing the transaction rolls it back

        const string sql = @"
            UPDATE Tickets
            SET Title = @Title, Description = @Description, TicketType = @TicketType, State = @State,
                Priority = @Priority, Impact = @Impact, UpdatedByUserId = @UserId, ActivityDate = now()
            WHERE TicketId = @TicketId;";
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            AddTicketParams(cmd, request);
            cmd.Parameters.AddInt("TicketId", ticketId);
            cmd.Parameters.AddInt("UserId", userId);
            await cmd.ExecuteNonQueryAsync();
        }

        await ReplaceTagsAsync(conn, tx, ticketId, request.Tags);
        await AddHistoryAsync(conn, tx, ticketId, userId, changes);
        if (removed.Count > 0)
        {
            await using var del = new NpgsqlCommand(
                "DELETE FROM TicketAssignees WHERE TicketId = @TicketId AND UserId = ANY(@Ids);", conn, tx);
            del.Parameters.AddInt("TicketId", ticketId);
            del.Parameters.AddIntArray("Ids", removed.ToArray());
            await del.ExecuteNonQueryAsync();
        }
        await AddAssigneesAsync(conn, tx, ticketId, added, userId);
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

    public async Task<TicketComment?> AddCommentAsync(int ticketId, string text, int userId, IReadOnlyCollection<int> mentionedUserIds)
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
        // One transaction: a comment never lands without its mentions, nor a "mentioned you" without the comment.
        await using var tx = await conn.BeginTransactionAsync();

        TicketComment comment;
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            cmd.Parameters.AddInt("TicketId", ticketId);
            cmd.Parameters.AddInt("UserId", userId);
            cmd.Parameters.AddText("Text", text.Trim());
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            comment = MapComment(reader);
        }

        if (mentionedUserIds.Count > 0)
        {
            const string mentionSql = @"
                INSERT INTO CommentMentions (CommentId, UserId)
                SELECT @CommentId, id FROM unnest(@Ids) AS id
                ON CONFLICT DO NOTHING;
                INSERT INTO Notifications (UserId, TicketId, ActorUserId, Kind, CommentId)
                SELECT id, @TicketId, @ActorId, 'Mentioned', @CommentId FROM unnest(@Ids) AS id;
                SELECT u.UserId, u.DisplayName FROM Users u WHERE u.UserId = ANY(@Ids) ORDER BY u.DisplayName;";
            await using var cmd = new NpgsqlCommand(mentionSql, conn, tx);
            cmd.Parameters.AddInt("CommentId", comment.CommentId);
            cmd.Parameters.AddInt("TicketId", ticketId);
            cmd.Parameters.AddInt("ActorId", userId);
            cmd.Parameters.AddIntArray("Ids", mentionedUserIds.ToArray());
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) comment.Mentions.Add(new CommentMention(reader.Int("UserId"), reader.Str("DisplayName")));
        }

        await tx.CommitAsync();
        return comment;
    }

    private sealed record Snapshot(string Title, string? Description, string TicketType,
        string State, int Priority, string Impact, List<string> Tags, List<TicketAssignee> Assignees);

    private static async Task<Snapshot?> ReadForUpdateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int ticketId)
    {
        string sql = $@"
            SELECT t.Title, t.Description, t.TicketType, t.State, t.Priority, t.Impact
            FROM Tickets t
            WHERE t.TicketId = @TicketId
            FOR UPDATE OF t;
            SELECT Tag FROM TicketTags WHERE TicketId = @TicketId;
            {SelectAssignees.Replace("ANY(@Ids)", "@TicketId")}";
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddInt("TicketId", ticketId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        var snapshot = new Snapshot(r.Str("Title"), r.NStr("Description"), r.Str("TicketType"),
                                    r.Str("State"), r.Int("Priority"), r.Str("Impact"), [], []);
        await r.NextResultAsync();
        while (await r.ReadAsync()) snapshot.Tags.Add(r.Str("Tag"));
        await r.NextResultAsync();
        while (await r.ReadAsync()) snapshot.Assignees.Add(MapAssignee(r));
        return snapshot;
    }

    private static async Task<Dictionary<int, string>> DisplayNamesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, List<int> userIds)
    {
        var names = new Dictionary<int, string>();
        if (userIds.Count == 0) return names;
        await using var cmd = new NpgsqlCommand("SELECT UserId, DisplayName FROM Users WHERE UserId = ANY(@Ids);", conn, tx);
        cmd.Parameters.AddIntArray("Ids", userIds.ToArray());
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) names[r.Int("UserId")] = r.Str("DisplayName");
        return names;
    }

    /// <summary>How the history shows a set of assignees: names in the order they were added.</summary>
    private static string NameList(IEnumerable<string> names)
    {
        var list = string.Join(", ", names);
        return list.Length == 0 ? "Unassigned" : list;
    }

    /// <summary>
    /// Puts people on the ticket and tells each of them, except whoever is doing the assigning.
    /// The actor is recorded as who assigned them.
    /// </summary>
    private static async Task AddAssigneesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int ticketId, IEnumerable<int> userIds, int actorId)
    {
        foreach (int assignee in userIds.Distinct())
        {
            await using (var cmd = new NpgsqlCommand(
                "INSERT INTO TicketAssignees (TicketId, UserId, AssignedByUserId) VALUES (@TicketId, @UserId, @ActorId) ON CONFLICT DO NOTHING;", conn, tx))
            {
                cmd.Parameters.AddInt("TicketId", ticketId);
                cmd.Parameters.AddInt("UserId", assignee);
                cmd.Parameters.AddInt("ActorId", actorId);
                await cmd.ExecuteNonQueryAsync();
            }
            await NotifyAssigneeAsync(conn, tx, ticketId, assignee, actorId);
        }
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

    /// <summary>
    /// Tells the new assignee, unless they assigned it to themselves. Written in the ticket's own
    /// transaction, so a save that fails leaves no notification about a change that never happened.
    /// </summary>
    private static async Task NotifyAssigneeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int ticketId, int recipient, int actorId)
    {
        if (recipient == actorId) return;

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO Notifications (UserId, TicketId, ActorUserId) VALUES (@UserId, @TicketId, @ActorId);", conn, tx);
        cmd.Parameters.AddInt("UserId", recipient);
        cmd.Parameters.AddInt("TicketId", ticketId);
        cmd.Parameters.AddInt("ActorId", actorId);
        await cmd.ExecuteNonQueryAsync();
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
