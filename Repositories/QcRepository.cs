using Npgsql;
using QaDocBackend.Data;
using QaDocBackend.Models;

namespace QaDocBackend.Repositories;

public interface IQcRepository
{
    Task<int> CreateDocSetAsync(
        int projectId, string title, string appName, string? description, string language,
        int? logoAttachmentId, bool isDemo, int? createdBy,
        List<(int attachmentId, string? caption, int sortOrder)> screens);

    Task<IEnumerable<QcDocSet>> GetByProjectAsync(int projectId, bool onlyDemo);

    Task<QcDocSet?> GetByIdAsync(int docSetId, bool onlyApprovedDocs = false);

    Task<bool> UpdateDocSetAsync(
        int docSetId, string? title, string? appName, string? description, string? language,
        int? logoAttachmentId, bool clearLogo = false);

    Task<bool> DeleteDocSetAsync(int docSetId);

    Task<bool> UpdateScreenOrderAndCaptionsAsync(int docSetId, List<ScreenOrderItem> screens);

    Task<List<QcDocScreen>> AddScreensAsync(int docSetId, List<(int attachmentId, string? caption)> newScreens);

    Task<bool> DeleteScreenAsync(int docSetId, int screenId);

    Task<QcDocScreen?> GetScreenByIdAsync(int screenId);

    Task UpdateScreenSummaryAsync(int screenId, string summaryJson);

    Task<QcDocument> CreateDocumentVersionAsync(
        int docSetId, string kind, string markdown, string source, int? generatedBy,
        string status = QcDocumentStatuses.Draft);

    Task<QcDocument?> GetDocumentByIdAsync(int documentId);

    Task<bool> UpdateDocumentAsync(int documentId, string? markdown, string? status);

    Task<IReadOnlyList<int>> GetAttachmentIdsForCleanupAsync(int docSetId);
}

public class QcRepository(ISqlConnectionFactory db) : IQcRepository
{
    private const string DocSetSelect = @"
        SELECT ds.DocSetId, ds.ProjectId, ds.Title, ds.AppName, ds.BusinessDescription,
               ds.Language, ds.LogoAttachmentId, ds.CreatedBy, u.DisplayName AS CreatedByName,
               ds.CreatedAt, ds.IsDemo,
               p.ProjectName, p.ProjectCode,
               (SELECT COUNT(*) FROM QcDocScreens sc WHERE sc.DocSetId = ds.DocSetId) AS ScreenCount,
               (SELECT COUNT(*) FROM QcDocuments doc WHERE doc.DocSetId = ds.DocSetId) AS DocumentCount
        FROM QcDocSets ds
        JOIN Projects p ON p.ProjectId = ds.ProjectId
        LEFT JOIN Users u ON u.UserId = ds.CreatedBy";

    private const string ScreenSelect = @"
        SELECT sc.ScreenId, sc.DocSetId, sc.SortOrder, sc.Caption, sc.AttachmentId,
               sc.ScreenSummary, a.ContentType, a.ByteSize
        FROM QcDocScreens sc
        JOIN TicketAttachments a ON a.AttachmentId = sc.AttachmentId";

    private const string DocumentSelect = @"
        SELECT doc.DocumentId, doc.DocSetId, doc.Kind, doc.Version, doc.Markdown,
               doc.Status, doc.Source, doc.GeneratedBy, u.DisplayName AS GeneratedByName,
               doc.CreatedAt
        FROM QcDocuments doc
        LEFT JOIN Users u ON u.UserId = doc.GeneratedBy";

    public async Task<int> CreateDocSetAsync(
        int projectId, string title, string appName, string? description, string language,
        int? logoAttachmentId, bool isDemo, int? createdBy,
        List<(int attachmentId, string? caption, int sortOrder)> screens)
    {
        const string sql = @"
            INSERT INTO QcDocSets (ProjectId, Title, AppName, BusinessDescription, Language, LogoAttachmentId, CreatedBy, IsDemo)
            VALUES (@ProjectId, @Title, @AppName, @Description, @Language, @LogoAttachmentId, @CreatedBy, @IsDemo)
            RETURNING DocSetId;";

        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        int docSetId;
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            cmd.Parameters.AddInt("ProjectId", projectId);
            cmd.Parameters.AddText("Title", title.Trim());
            cmd.Parameters.AddText("AppName", appName.Trim());
            cmd.Parameters.AddText("Description", description);
            cmd.Parameters.AddText("Language", string.IsNullOrWhiteSpace(language) ? "en" : language.Trim().ToLowerInvariant());
            cmd.Parameters.AddInt("LogoAttachmentId", logoAttachmentId);
            cmd.Parameters.AddInt("CreatedBy", createdBy);
            cmd.Parameters.AddBool("IsDemo", isDemo);
            docSetId = (int)(await cmd.ExecuteScalarAsync())!;
        }

        foreach (var (attachmentId, caption, sortOrder) in screens)
        {
            await using var screenCmd = new NpgsqlCommand(@"
                INSERT INTO QcDocScreens (DocSetId, AttachmentId, SortOrder, Caption)
                VALUES (@DocSetId, @AttachmentId, @SortOrder, @Caption);", conn, tx);
            screenCmd.Parameters.AddInt("DocSetId", docSetId);
            screenCmd.Parameters.AddInt("AttachmentId", attachmentId);
            screenCmd.Parameters.AddInt("SortOrder", sortOrder);
            screenCmd.Parameters.AddText("Caption", caption);
            await screenCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return docSetId;
    }

    public async Task<IEnumerable<QcDocSet>> GetByProjectAsync(int projectId, bool onlyDemo)
    {
        string sql = DocSetSelect + @"
            WHERE ds.ProjectId = @ProjectId
              AND (@OnlyDemo = FALSE OR ds.IsDemo = TRUE)
            ORDER BY ds.DocSetId DESC;";

        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("ProjectId", projectId);
        cmd.Parameters.AddBool("OnlyDemo", onlyDemo);

        var list = new List<QcDocSet>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(ReadDocSet(reader));
        }
        return list;
    }

    public async Task<QcDocSet?> GetByIdAsync(int docSetId, bool onlyApprovedDocs = false)
    {
        string sql = DocSetSelect + " WHERE ds.DocSetId = @DocSetId;";

        await using var conn = await db.OpenAsync();
        QcDocSet? docSet = null;

        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.AddInt("DocSetId", docSetId);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                docSet = ReadDocSet(reader);
            }
        }

        if (docSet == null) return null;

        // Load screens in sort order
        string screenSql = ScreenSelect + " WHERE sc.DocSetId = @DocSetId ORDER BY sc.SortOrder ASC, sc.ScreenId ASC;";
        await using (var cmd = new NpgsqlCommand(screenSql, conn))
        {
            cmd.Parameters.AddInt("DocSetId", docSetId);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                docSet.Screens.Add(ReadScreen(reader));
            }
        }

        // Load documents (newest versions first)
        string docSql = DocumentSelect + @"
            WHERE doc.DocSetId = @DocSetId
              AND (@OnlyApproved = FALSE OR doc.Status = 'Approved')
            ORDER BY doc.Kind ASC, doc.Version DESC;";
        await using (var cmd = new NpgsqlCommand(docSql, conn))
        {
            cmd.Parameters.AddInt("DocSetId", docSetId);
            cmd.Parameters.AddBool("OnlyApproved", onlyApprovedDocs);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                docSet.Documents.Add(ReadDocument(reader));
            }
        }

        return docSet;
    }

    public async Task<bool> UpdateDocSetAsync(
        int docSetId, string? title, string? appName, string? description, string? language,
        int? logoAttachmentId, bool clearLogo = false)
    {
        var sets = new List<string>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand();
        cmd.Connection = conn;
        cmd.Parameters.AddInt("DocSetId", docSetId);

        if (title != null)
        {
            sets.Add("Title = @Title");
            cmd.Parameters.AddText("Title", title.Trim());
        }
        if (appName != null)
        {
            sets.Add("AppName = @AppName");
            cmd.Parameters.AddText("AppName", appName.Trim());
        }
        if (description != null)
        {
            sets.Add("BusinessDescription = @Description");
            cmd.Parameters.AddText("Description", description);
        }
        if (language != null)
        {
            sets.Add("Language = @Language");
            cmd.Parameters.AddText("Language", language.Trim().ToLowerInvariant());
        }
        if (clearLogo)
        {
            sets.Add("LogoAttachmentId = NULL");
        }
        else if (logoAttachmentId.HasValue)
        {
            sets.Add("LogoAttachmentId = @LogoAttachmentId");
            cmd.Parameters.AddInt("LogoAttachmentId", logoAttachmentId.Value);
        }

        if (sets.Count == 0) return true;

        cmd.CommandText = $"UPDATE QcDocSets SET {string.Join(", ", sets)} WHERE DocSetId = @DocSetId;";
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeleteDocSetAsync(int docSetId)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM QcDocSets WHERE DocSetId = @DocSetId;", conn);
        cmd.Parameters.AddInt("DocSetId", docSetId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> UpdateScreenOrderAndCaptionsAsync(int docSetId, List<ScreenOrderItem> screens)
    {
        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        foreach (var item in screens)
        {
            await using var cmd = new NpgsqlCommand(@"
                UPDATE QcDocScreens
                SET SortOrder = @SortOrder,
                    Caption = COALESCE(@Caption, Caption)
                WHERE DocSetId = @DocSetId AND ScreenId = @ScreenId;", conn, tx);
            cmd.Parameters.AddInt("DocSetId", docSetId);
            cmd.Parameters.AddInt("ScreenId", item.ScreenId);
            cmd.Parameters.AddInt("SortOrder", item.SortOrder);
            cmd.Parameters.AddText("Caption", item.Caption);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return true;
    }

    public async Task<List<QcDocScreen>> AddScreensAsync(int docSetId, List<(int attachmentId, string? caption)> newScreens)
    {
        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Get max current sortorder
        int maxOrder = 0;
        await using (var maxCmd = new NpgsqlCommand(
            "SELECT COALESCE(MAX(SortOrder), 0) FROM QcDocScreens WHERE DocSetId = @DocSetId;", conn, tx))
        {
            maxCmd.Parameters.AddInt("DocSetId", docSetId);
            var res = await maxCmd.ExecuteScalarAsync();
            if (res != null && res != DBNull.Value) maxOrder = Convert.ToInt32(res);
        }

        var added = new List<QcDocScreen>();
        foreach (var (attachmentId, caption) in newScreens)
        {
            maxOrder++;
            await using var insertCmd = new NpgsqlCommand(@"
                INSERT INTO QcDocScreens (DocSetId, AttachmentId, SortOrder, Caption)
                VALUES (@DocSetId, @AttachmentId, @SortOrder, @Caption)
                RETURNING ScreenId;", conn, tx);
            insertCmd.Parameters.AddInt("DocSetId", docSetId);
            insertCmd.Parameters.AddInt("AttachmentId", attachmentId);
            insertCmd.Parameters.AddInt("SortOrder", maxOrder);
            insertCmd.Parameters.AddText("Caption", caption);
            int screenId = (int)(await insertCmd.ExecuteScalarAsync())!;

            added.Add(new QcDocScreen
            {
                ScreenId = screenId,
                DocSetId = docSetId,
                AttachmentId = attachmentId,
                SortOrder = maxOrder,
                Caption = caption
            });
        }

        await tx.CommitAsync();
        return added;
    }

    public async Task<bool> DeleteScreenAsync(int docSetId, int screenId)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM QcDocScreens WHERE DocSetId = @DocSetId AND ScreenId = @ScreenId;", conn);
        cmd.Parameters.AddInt("DocSetId", docSetId);
        cmd.Parameters.AddInt("ScreenId", screenId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<QcDocScreen?> GetScreenByIdAsync(int screenId)
    {
        string sql = ScreenSelect + " WHERE sc.ScreenId = @ScreenId;";
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("ScreenId", screenId);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadScreen(reader) : null;
    }

    public async Task UpdateScreenSummaryAsync(int screenId, string summaryJson)
    {
        const string sql = "UPDATE QcDocScreens SET ScreenSummary = @Summary WHERE ScreenId = @ScreenId;";
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("ScreenId", screenId);
        cmd.Parameters.AddText("Summary", summaryJson);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<QcDocument> CreateDocumentVersionAsync(
        int docSetId, string kind, string markdown, string source, int? generatedBy,
        string status = QcDocumentStatuses.Draft)
    {
        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Lock docset to compute next version cleanly
        int nextVersion = 1;
        await using (var vCmd = new NpgsqlCommand(@"
            SELECT COALESCE(MAX(Version), 0) + 1
            FROM QcDocuments
            WHERE DocSetId = @DocSetId AND Kind = @Kind;", conn, tx))
        {
            vCmd.Parameters.AddInt("DocSetId", docSetId);
            vCmd.Parameters.AddText("Kind", kind);
            var res = await vCmd.ExecuteScalarAsync();
            if (res != null && res != DBNull.Value) nextVersion = Convert.ToInt32(res);
        }

        const string sql = @"
            INSERT INTO QcDocuments (DocSetId, Kind, Version, Markdown, Status, Source, GeneratedBy)
            VALUES (@DocSetId, @Kind, @Version, @Markdown, @Status, @Source, @GeneratedBy)
            RETURNING DocumentId, CreatedAt;";

        int documentId;
        DateTime createdAt;
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            cmd.Parameters.AddInt("DocSetId", docSetId);
            cmd.Parameters.AddText("Kind", kind);
            cmd.Parameters.AddInt("Version", nextVersion);
            cmd.Parameters.AddText("Markdown", markdown);
            cmd.Parameters.AddText("Status", status);
            cmd.Parameters.AddText("Source", source);
            cmd.Parameters.AddInt("GeneratedBy", generatedBy);

            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            documentId = reader.GetInt32(0);
            createdAt = reader.GetDateTime(1);
        }

        await tx.CommitAsync();

        return new QcDocument
        {
            DocumentId = documentId,
            DocSetId = docSetId,
            Kind = kind,
            Version = nextVersion,
            Markdown = markdown,
            Status = status,
            Source = source,
            GeneratedBy = generatedBy,
            CreatedAt = createdAt
        };
    }

    public async Task<QcDocument?> GetDocumentByIdAsync(int documentId)
    {
        string sql = DocumentSelect + " WHERE doc.DocumentId = @DocumentId;";
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("DocumentId", documentId);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadDocument(reader) : null;
    }

    public async Task<bool> UpdateDocumentAsync(int documentId, string? markdown, string? status)
    {
        var sets = new List<string>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand();
        cmd.Connection = conn;
        cmd.Parameters.AddInt("DocumentId", documentId);

        if (markdown != null)
        {
            sets.Add("Markdown = @Markdown");
            cmd.Parameters.AddText("Markdown", markdown);
        }
        if (status != null && QcDocumentStatuses.IsValid(status))
        {
            sets.Add("Status = @Status");
            cmd.Parameters.AddText("Status", status);
        }

        if (sets.Count == 0) return true;

        cmd.CommandText = $"UPDATE QcDocuments SET {string.Join(", ", sets)} WHERE DocumentId = @DocumentId;";
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<IReadOnlyList<int>> GetAttachmentIdsForCleanupAsync(int docSetId)
    {
        const string sql = @"
            SELECT AttachmentId FROM QcDocScreens WHERE DocSetId = @DocSetId
            UNION
            SELECT LogoAttachmentId FROM QcDocSets WHERE DocSetId = @DocSetId AND LogoAttachmentId IS NOT NULL;";

        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("DocSetId", docSetId);

        var list = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(0)) list.Add(reader.GetInt32(0));
        }
        return list;
    }

    private static QcDocSet ReadDocSet(NpgsqlDataReader r) => new()
    {
        DocSetId = r.GetInt32(r.GetOrdinal("DocSetId")),
        ProjectId = r.GetInt32(r.GetOrdinal("ProjectId")),
        Title = r.GetString(r.GetOrdinal("Title")),
        AppName = r.GetString(r.GetOrdinal("AppName")),
        BusinessDescription = r.IsDBNull(r.GetOrdinal("BusinessDescription")) ? null : r.GetString(r.GetOrdinal("BusinessDescription")),
        Language = r.GetString(r.GetOrdinal("Language")),
        LogoAttachmentId = r.IsDBNull(r.GetOrdinal("LogoAttachmentId")) ? null : r.GetInt32(r.GetOrdinal("LogoAttachmentId")),
        CreatedBy = r.IsDBNull(r.GetOrdinal("CreatedBy")) ? null : r.GetInt32(r.GetOrdinal("CreatedBy")),
        CreatedByName = r.IsDBNull(r.GetOrdinal("CreatedByName")) ? null : r.GetString(r.GetOrdinal("CreatedByName")),
        CreatedAt = r.GetDateTime(r.GetOrdinal("CreatedAt")),
        IsDemo = r.GetBoolean(r.GetOrdinal("IsDemo")),
        ProjectName = r.GetString(r.GetOrdinal("ProjectName")),
        ProjectCode = r.GetString(r.GetOrdinal("ProjectCode")),
        ScreenCount = Convert.ToInt32(r["ScreenCount"]),
        DocumentCount = Convert.ToInt32(r["DocumentCount"])
    };

    private static QcDocScreen ReadScreen(NpgsqlDataReader r) => new()
    {
        ScreenId = r.GetInt32(r.GetOrdinal("ScreenId")),
        DocSetId = r.GetInt32(r.GetOrdinal("DocSetId")),
        SortOrder = r.GetInt32(r.GetOrdinal("SortOrder")),
        Caption = r.IsDBNull(r.GetOrdinal("Caption")) ? null : r.GetString(r.GetOrdinal("Caption")),
        AttachmentId = r.GetInt32(r.GetOrdinal("AttachmentId")),
        ScreenSummary = r.IsDBNull(r.GetOrdinal("ScreenSummary")) ? null : r.GetString(r.GetOrdinal("ScreenSummary")),
        ContentType = r.GetString(r.GetOrdinal("ContentType")),
        ByteSize = r.GetInt64(r.GetOrdinal("ByteSize"))
    };

    private static QcDocument ReadDocument(NpgsqlDataReader r) => new()
    {
        DocumentId = r.GetInt32(r.GetOrdinal("DocumentId")),
        DocSetId = r.GetInt32(r.GetOrdinal("DocSetId")),
        Kind = r.GetString(r.GetOrdinal("Kind")),
        Version = r.GetInt32(r.GetOrdinal("Version")),
        Markdown = r.GetString(r.GetOrdinal("Markdown")),
        Status = r.GetString(r.GetOrdinal("Status")),
        Source = r.GetString(r.GetOrdinal("Source")),
        GeneratedBy = r.IsDBNull(r.GetOrdinal("GeneratedBy")) ? null : r.GetInt32(r.GetOrdinal("GeneratedBy")),
        GeneratedByName = r.IsDBNull(r.GetOrdinal("GeneratedByName")) ? null : r.GetString(r.GetOrdinal("GeneratedByName")),
        CreatedAt = r.GetDateTime(r.GetOrdinal("CreatedAt"))
    };
}
