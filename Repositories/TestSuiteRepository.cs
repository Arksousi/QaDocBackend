using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using QaDocBackend.Data;
using QaDocBackend.Models;

namespace QaDocBackend.Repositories;

public interface ITestSuiteRepository
{
    /// <summary>Creates a suite and points it at screenshots already stored as attachments.</summary>
    Task<int> CreateAsync(int projectId, int? folderId, string title, string? description, bool isDemo, int userId, IReadOnlyList<int> attachmentIds);

    /// <summary>Suites of one project, newest first. <paramref name="onlyDemo"/> is the guest's view.</summary>
    Task<IEnumerable<TestSuite>> GetByProjectAsync(int projectId, bool onlyDemo);

    /// <summary>One suite with its screenshots and test cases, or null when it does not exist.</summary>
    Task<TestSuite?> GetByIdAsync(int suiteId);

    Task<bool> DeleteAsync(int suiteId);

    /// <summary>Attachment ids behind the screenshots, so the bytes can go too when the suite goes.</summary>
    Task<IReadOnlyList<int>> GetScreenAttachmentIdsAsync(int suiteId);

    /// <summary>
    /// Saves a batch as Draft under one lock: the numbers are handed out exactly the way ticket
    /// numbers are, so two generations at once can never share a TC-0001.
    /// </summary>
    Task<List<TestCase>> AddCasesAsync(int suiteId, IEnumerable<GeneratedTestCase> cases, string source);

    Task<TestCase?> GetCaseAsync(int caseId);

    Task<UpdateOutcome> UpdateCaseAsync(int caseId, UpdateTestCaseRequest request);

    /// <summary>Removes one case. A ticket it became is left alone; only the link goes.</summary>
    Task<bool> DeleteCaseAsync(int caseId);

    /// <summary>Points a case at the ticket it became. Returns false when the case has gone.</summary>
    Task<bool> LinkTicketAsync(int caseId, int ticketId);
}

public class TestSuiteRepository(ISqlConnectionFactory db) : ITestSuiteRepository
{
    private const string SuiteSelect = @"
        SELECT s.SuiteId, s.ProjectId, s.FolderId, s.Title, s.BusinessDescription, s.CreatedByUserId,
               u.DisplayName AS CreatedByName, s.CreatedAt, s.IsDemo,
               p.ProjectName, p.ProjectCode, COALESCE(f.FolderName, '') AS FolderName,
               (SELECT COUNT(*) FROM TestSuiteScreens sc WHERE sc.SuiteId = s.SuiteId) AS ScreenCount,
               (SELECT COUNT(*) FROM TestCases tc WHERE tc.SuiteId = s.SuiteId) AS CaseCount
        FROM TestSuites s
        JOIN Projects p            ON p.ProjectId = s.ProjectId
        LEFT JOIN Folders f        ON f.FolderId = s.FolderId
        LEFT JOIN Users u          ON u.UserId = s.CreatedByUserId";

    private const string ScreenSelect = @"
        SELECT sc.ScreenId, sc.SuiteId, sc.AttachmentId, sc.SortOrder,
               a.ContentType, a.ByteSize
        FROM TestSuiteScreens sc
        JOIN TicketAttachments a ON a.AttachmentId = sc.AttachmentId";

    private const string CaseSelect = @"
        SELECT c.TestCaseId, c.SuiteId, c.Number, c.Title, c.Category, c.Priority, c.Preconditions,
               c.Steps, c.Expected, c.Status, c.Source, c.CreatedAt,
               c.LinkedTicketId,
               tk.TicketKey AS LinkedTicketKey
        FROM TestCases c
        LEFT JOIN (SELECT t.TicketId,
                           p.ProjectCode || '-' || f.FolderCode || '-' || lpad(t.Sequence::text, 4, '0') AS TicketKey
                    FROM Tickets t
                    JOIN Folders f  ON f.FolderId = t.FolderId
                    JOIN Projects p ON p.ProjectId = t.ProjectId) tk ON tk.TicketId = c.LinkedTicketId";

    public async Task<int> CreateAsync(int projectId, int? folderId, string title, string? description,
        bool isDemo, int userId, IReadOnlyList<int> attachmentIds)
    {
        const string sql = @"
            INSERT INTO TestSuites (ProjectId, FolderId, Title, BusinessDescription, CreatedByUserId, IsDemo)
            VALUES (@ProjectId, @FolderId, @Title, @Description, @UserId, @IsDemo)
            RETURNING SuiteId;";

        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        int suiteId;
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            cmd.Parameters.AddInt("ProjectId", projectId);
            cmd.Parameters.AddInt("FolderId", folderId);
            cmd.Parameters.AddText("Title", title.Trim());
            cmd.Parameters.AddText("Description", description);
            cmd.Parameters.AddInt("UserId", userId);
            cmd.Parameters.AddBool("IsDemo", isDemo);
            suiteId = (int)(await cmd.ExecuteScalarAsync())!;
        }

        for (int i = 0; i < attachmentIds.Count; i++)
        {
            await using var screen = new NpgsqlCommand(@"
                INSERT INTO TestSuiteScreens (SuiteId, AttachmentId, SortOrder)
                VALUES (@SuiteId, @AttachmentId, @SortOrder);", conn, tx);
            screen.Parameters.AddInt("SuiteId", suiteId);
            screen.Parameters.AddInt("AttachmentId", attachmentIds[i]);
            screen.Parameters.AddInt("SortOrder", i);
            await screen.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return suiteId;
    }

    public async Task<IEnumerable<TestSuite>> GetByProjectAsync(int projectId, bool onlyDemo)
    {
        string sql = SuiteSelect + @"
            WHERE s.ProjectId = @ProjectId
              -- A guest only ever sees demo suites, the same split the tickets follow.
              AND (@OnlyDemo = FALSE OR s.IsDemo = TRUE)
            ORDER BY s.CreatedAt DESC, s.SuiteId DESC;";

        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("ProjectId", projectId);
        cmd.Parameters.AddBool("OnlyDemo", onlyDemo);

        var suites = new List<TestSuite>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) suites.Add(MapSuite(reader));
        return suites;
    }

    public async Task<TestSuite?> GetByIdAsync(int suiteId)
    {
        await using var conn = await db.OpenAsync();

        TestSuite? suite;
        await using (var cmd = new NpgsqlCommand(SuiteSelect + " WHERE s.SuiteId = @SuiteId;", conn))
        {
            cmd.Parameters.AddInt("SuiteId", suiteId);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            suite = MapSuite(reader);
        }

        await using (var cmd = new NpgsqlCommand(ScreenSelect + " WHERE sc.SuiteId = @SuiteId ORDER BY sc.SortOrder, sc.ScreenId;", conn))
        {
            cmd.Parameters.AddInt("SuiteId", suiteId);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) suite.Screens.Add(MapScreen(reader));
        }

        await using (var cmd = new NpgsqlCommand(CaseSelect + " WHERE c.SuiteId = @SuiteId ORDER BY c.Number;", conn))
        {
            cmd.Parameters.AddInt("SuiteId", suiteId);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) suite.Cases.Add(MapCase(reader));
        }
        return suite;
    }

    /// <summary>Suite first, then its screens, then the attachments that only the screens pointed at.</summary>
    public async Task<bool> DeleteAsync(int suiteId)
    {
        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var attachments = new List<int>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT AttachmentId FROM TestSuiteScreens WHERE SuiteId = @SuiteId FOR UPDATE;", conn, tx))
        {
            cmd.Parameters.AddInt("SuiteId", suiteId);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) attachments.Add(reader.GetInt32(0));
        }

        await using (var cmd = new NpgsqlCommand("DELETE FROM TestSuites WHERE SuiteId = @SuiteId;", conn, tx))
        {
            cmd.Parameters.AddInt("SuiteId", suiteId);
            if (await cmd.ExecuteNonQueryAsync() == 0)
            {
                await tx.RollbackAsync();
                return false;
            }
        }

        // Screens cascade away with the suite; the pictures they pointed at do not, and nothing
        // else references them, so they go here rather than being left for a cleaner to find.
        if (attachments.Count > 0)
        {
            await using var cmd = new NpgsqlCommand(
                "DELETE FROM TicketAttachments WHERE AttachmentId = ANY(@Ids);", conn, tx);
            cmd.Parameters.AddIntArray("Ids", attachments.ToArray());
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return true;
    }

    public async Task<IReadOnlyList<int>> GetScreenAttachmentIdsAsync(int suiteId)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT AttachmentId FROM TestSuiteScreens WHERE SuiteId = @SuiteId ORDER BY SortOrder;", conn);
        cmd.Parameters.AddInt("SuiteId", suiteId);

        var ids = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    public async Task<List<TestCase>> AddCasesAsync(int suiteId, IEnumerable<GeneratedTestCase> cases, string source)
    {
        var incoming = cases.ToList();
        var saved = new List<TestCase>(incoming.Count);
        if (incoming.Count == 0) return saved;

        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Claim the whole block of numbers in one locked UPDATE. The row lock is what the folders
        // use for ticket numbers: two saves at once queue up instead of both taking 1.
        int next;
        await using (var cmd = new NpgsqlCommand(
            "UPDATE TestSuites SET NextSequence = NextSequence + @Count WHERE SuiteId = @SuiteId RETURNING NextSequence - @Count;", conn, tx))
        {
            cmd.Parameters.AddInt("Count", incoming.Count);
            cmd.Parameters.AddInt("SuiteId", suiteId);
            var result = await cmd.ExecuteScalarAsync();
            if (result == null)
            {
                await tx.RollbackAsync();
                return saved; // the suite has gone; the controller reports it as not found
            }
            next = Convert.ToInt32(result);
        }

        for (int i = 0; i < incoming.Count; i++)
        {
            var item = incoming[i];
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO TestCases (SuiteId, Number, Title, Category, Priority, Preconditions, Steps, Expected, Status, Source)
                VALUES (@SuiteId, @Number, @Title, @Category, @Priority, @Preconditions, @Steps, @Expected, 'Draft', @Source)
                RETURNING TestCaseId, CreatedAt;", conn, tx);
            cmd.Parameters.AddInt("SuiteId", suiteId);
            cmd.Parameters.AddInt("Number", next + i);
            cmd.Parameters.AddText("Title", item.Title.Trim());
            cmd.Parameters.AddText("Category", TestCategories.Normalise(item.Category) ?? TestCategories.Functional);
            cmd.Parameters.AddInt("Priority", Math.Clamp(item.Priority, 1, 4));
            cmd.Parameters.AddText("Preconditions", item.Preconditions);
            cmd.Parameters.Add(new NpgsqlParameter("Steps", NpgsqlDbType.Jsonb)
            {
                Value = JsonSerializer.Serialize(item.Steps ?? [])
            });
            cmd.Parameters.AddText("Expected", item.Expected);
            cmd.Parameters.AddText("Source", source);

            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            saved.Add(new TestCase
            {
                TestCaseId = reader.Int("TestCaseId"),
                SuiteId = suiteId,
                Number = next + i,
                Title = item.Title.Trim(),
                Category = TestCategories.Normalise(item.Category) ?? TestCategories.Functional,
                Priority = Math.Clamp(item.Priority, 1, 4),
                Preconditions = item.Preconditions,
                Steps = item.Steps ?? [],
                Expected = item.Expected,
                Status = TestStatuses.Draft,
                Source = source,
                CreatedAt = reader.Utc("CreatedAt")
            });
        }

        await tx.CommitAsync();
        return saved;
    }

    public async Task<TestCase?> GetCaseAsync(int caseId)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(CaseSelect + " WHERE c.TestCaseId = @CaseId;", conn);
        cmd.Parameters.AddInt("CaseId", caseId);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapCase(reader) : null;
    }

    public async Task<UpdateOutcome> UpdateCaseAsync(int caseId, UpdateTestCaseRequest request)
    {
        await using var conn = await db.OpenAsync();

        // Read first, write only what was actually sent, and report "unchanged" rather than
        // pretending a no-op save was a change — the same contract PUT tickets uses.
        var before = await ReadCaseAsync(conn, caseId);
        if (before == null) return UpdateOutcome.NotFound;

        string title = string.IsNullOrWhiteSpace(request.Title) ? before.Title : request.Title.Trim();
        string category = TestCategories.Normalise(request.Category) ?? before.Category;
        int priority = request.Priority is { } p ? Math.Clamp(p, 1, 4) : before.Priority;
        string preconditions = request.Preconditions ?? before.Preconditions;
        var steps = request.Steps ?? before.Steps;
        string expected = request.Expected ?? before.Expected;
        string status = TestStatuses.IsValid(request.Status) ? request.Status! : before.Status;

        bool changed = title != before.Title || category != before.Category || priority != before.Priority
            || preconditions != before.Preconditions || !steps.SequenceEqual(before.Steps)
            || expected != before.Expected || status != before.Status;
        if (!changed) return UpdateOutcome.Unchanged;

        await using var cmd = new NpgsqlCommand(@"
            UPDATE TestCases
            SET Title = @Title, Category = @Category, Priority = @Priority, Preconditions = @Preconditions,
                Steps = @Steps, Expected = @Expected, Status = @Status
            WHERE TestCaseId = @CaseId;", conn);
        cmd.Parameters.AddText("Title", title);
        cmd.Parameters.AddText("Category", category);
        cmd.Parameters.AddInt("Priority", priority);
        cmd.Parameters.AddText("Preconditions", preconditions);
        cmd.Parameters.Add(new NpgsqlParameter("Steps", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(steps) });
        cmd.Parameters.AddText("Expected", expected);
        cmd.Parameters.AddText("Status", status);
        cmd.Parameters.AddInt("CaseId", caseId);
        await cmd.ExecuteNonQueryAsync();
        return UpdateOutcome.Updated;
    }

    public async Task<bool> LinkTicketAsync(int caseId, int ticketId)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE TestCases SET LinkedTicketId = @TicketId WHERE TestCaseId = @CaseId;", conn);
        cmd.Parameters.AddInt("TicketId", ticketId);
        cmd.Parameters.AddInt("CaseId", caseId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeleteCaseAsync(int caseId)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM TestCases WHERE TestCaseId = @CaseId;", conn);
        cmd.Parameters.AddInt("CaseId", caseId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    private static async Task<TestCase?> ReadCaseAsync(NpgsqlConnection conn, int caseId)
    {
        await using var cmd = new NpgsqlCommand(CaseSelect + " WHERE c.TestCaseId = @CaseId;", conn);
        cmd.Parameters.AddInt("CaseId", caseId);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapCase(reader) : null;
    }

    private static TestSuite MapSuite(NpgsqlDataReader r) => new()
    {
        SuiteId = r.Int("SuiteId"),
        ProjectId = r.Int("ProjectId"),
        FolderId = r.NInt("FolderId"),
        Title = r.Str("Title"),
        BusinessDescription = r.NStr("BusinessDescription"),
        CreatedByUserId = r.NInt("CreatedByUserId"),
        CreatedByName = r.NStr("CreatedByName"),
        CreatedAt = r.Utc("CreatedAt"),
        IsDemo = r.Bool("IsDemo"),
        ProjectName = r.Str("ProjectName"),
        ProjectCode = r.Str("ProjectCode"),
        FolderName = r.Str("FolderName"),
        ScreenCount = r.Int("ScreenCount"),
        CaseCount = r.Int("CaseCount")
    };

    private static TestSuiteScreen MapScreen(NpgsqlDataReader r) => new()
    {
        ScreenId = r.Int("ScreenId"),
        SuiteId = r.Int("SuiteId"),
        AttachmentId = r.Int("AttachmentId"),
        SortOrder = r.Int("SortOrder"),
        ContentType = r.Str("ContentType"),
        ByteSize = Convert.ToInt64(r["ByteSize"])
    };

    private static TestCase MapCase(NpgsqlDataReader r)
    {
        var testCase = new TestCase
        {
            TestCaseId = r.Int("TestCaseId"),
            SuiteId = r.Int("SuiteId"),
            Number = r.Int("Number"),
            Title = r.Str("Title"),
            Category = r.Str("Category"),
            Priority = r.Int("Priority"),
            Preconditions = r.Str("Preconditions"),
            Expected = r.Str("Expected"),
            Status = r.Str("Status"),
            LinkedTicketId = r.NInt("LinkedTicketId"),
            LinkedTicketKey = r.NStr("LinkedTicketKey"),
            Source = r.Str("Source"),
            CreatedAt = r.Utc("CreatedAt")
        };
        testCase.Steps = ReadSteps(r.Str("Steps"));
        return testCase;
    }

    /// <summary>Steps are stored as JSONB; a row written by hand with invalid JSON still reads.</summary>
    private static List<string> ReadSteps(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
