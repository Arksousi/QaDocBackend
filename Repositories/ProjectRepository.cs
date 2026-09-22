using Npgsql;
using QaDocBackend.Data;
using QaDocBackend.Models;

namespace QaDocBackend.Repositories;

public interface IProjectRepository
{
    /// <summary>Only the projects the user belongs to, or every project when they are an Admin.</summary>
    Task<IEnumerable<Project>> GetAllAsync(Viewer viewer);
    Task<IEnumerable<Project>> GetRecentAsync(int top, Viewer viewer);
    Task<Project?> GetByIdAsync(int projectId);
    /// <summary>Whether the project is demo data, or null when there is no such project.</summary>
    Task<bool?> IsDemoAsync(int projectId);
    /// <summary>The sample projects behind "Continue as a guest". For Admins, who cannot otherwise see them.</summary>
    Task<IEnumerable<Project>> GetDemoAsync();
    /// <summary>Moves a project between the guest tour and the real workspace.</summary>
    Task<bool> SetDemoAsync(int projectId, bool isDemo);
    /// <summary>Creates the project and makes the creator its first Manager, in one transaction.</summary>
    Task<int> CreateAsync(CreateProjectRequest request, int userId);
    Task<bool> DeleteAsync(int projectId);
    Task<ProjectSuggestions> GetSuggestionsAsync(int projectId);
}

public class ProjectRepository(ISqlConnectionFactory db) : IProjectRepository
{
    private const string SelectProjects = @"
        SELECT p.ProjectId, p.ProjectName, p.ProjectCode, p.CreatedAt, u.DisplayName AS CreatedByName,
               s.TicketCount, s.OpenTicketCount,
               COALESCE(s.LastTicketActivity, p.CreatedAt) AS LastActivity,
               NULL::text AS MyRole
        FROM Projects p
        LEFT JOIN Users u ON u.UserId = p.CreatedByUserId
        LEFT JOIN LATERAL (SELECT COUNT(*) AS TicketCount,
                                  COUNT(*) FILTER (WHERE t.State <> 'Closed') AS OpenTicketCount,
                                  MAX(t.ActivityDate) AS LastTicketActivity
                           FROM Tickets t WHERE t.ProjectId = p.ProjectId) s ON TRUE";

    /// <summary>
    /// Same shape as SelectProjects, but joined to the caller's membership so one query both filters
    /// the list and reports the role. Admins see every project and are reported as Manager.
    /// The IsDemo test is the same seam as in ProjectAccessService: a guest gets the demo projects
    /// and nobody else does, so neither list can leak a project from the other world.
    /// </summary>
    private const string SelectVisibleProjects = @"
        SELECT p.ProjectId, p.ProjectName, p.ProjectCode, p.CreatedAt, u.DisplayName AS CreatedByName,
               s.TicketCount, s.OpenTicketCount,
               COALESCE(s.LastTicketActivity, p.CreatedAt) AS LastActivity,
               CASE WHEN @IsGuest THEN 'Viewer' WHEN @IsAdmin THEN 'Manager' ELSE me.Role END AS MyRole
        FROM Projects p
        LEFT JOIN Users u ON u.UserId = p.CreatedByUserId
        LEFT JOIN ProjectMembers me ON me.ProjectId = p.ProjectId AND me.UserId = @UserId
        LEFT JOIN LATERAL (SELECT COUNT(*) AS TicketCount,
                                  COUNT(*) FILTER (WHERE t.State <> 'Closed') AS OpenTicketCount,
                                  MAX(t.ActivityDate) AS LastTicketActivity
                           FROM Tickets t WHERE t.ProjectId = p.ProjectId) s ON TRUE
        WHERE p.IsDemo = @IsGuest AND (@IsGuest OR @IsAdmin OR me.UserId IS NOT NULL)";

    public async Task<IEnumerable<Project>> GetAllAsync(Viewer viewer)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(SelectVisibleProjects + " ORDER BY p.ProjectName;", conn);
        AddViewer(cmd, viewer);
        return await ReadProjectsAsync(cmd);
    }

    public async Task<IEnumerable<Project>> GetRecentAsync(int top, Viewer viewer)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT * FROM ({SelectVisibleProjects}) x ORDER BY x.LastActivity DESC LIMIT @Top;", conn);
        AddViewer(cmd, viewer);
        cmd.Parameters.AddInt("Top", top);
        return await ReadProjectsAsync(cmd);
    }

    public async Task<bool?> IsDemoAsync(int projectId)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT IsDemo FROM Projects WHERE ProjectId = @ProjectId;", conn);
        cmd.Parameters.AddInt("ProjectId", projectId);
        return await cmd.ExecuteScalarAsync() as bool?;
    }

    /// <summary>Plain lookup with no access filtering; callers check access via IProjectAccessService.</summary>
    public async Task<Project?> GetByIdAsync(int projectId)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(SelectProjects + " WHERE p.ProjectId = @ProjectId;", conn);
        cmd.Parameters.AddInt("ProjectId", projectId);
        return (await ReadProjectsAsync(cmd)).FirstOrDefault();
    }

    public async Task<IEnumerable<Project>> GetDemoAsync()
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(SelectProjects + " WHERE p.IsDemo ORDER BY p.ProjectName;", conn);
        return await ReadProjectsAsync(cmd);
    }

    public async Task<bool> SetDemoAsync(int projectId, bool isDemo)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE Projects SET IsDemo = @IsDemo WHERE ProjectId = @ProjectId;", conn);
        cmd.Parameters.AddInt("ProjectId", projectId);
        cmd.Parameters.AddBool("IsDemo", isDemo);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<int> CreateAsync(CreateProjectRequest request, int userId)
    {
        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        int projectId;
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO Projects (ProjectName, ProjectCode, CreatedByUserId) VALUES (@ProjectName, @ProjectCode, @UserId) RETURNING ProjectId;", conn, tx))
        {
            cmd.Parameters.AddText("ProjectName", request.ProjectName.Trim());
            cmd.Parameters.AddText("ProjectCode", Codes.Normalise(request.ProjectCode));
            cmd.Parameters.AddInt("UserId", userId);
            projectId = (int)(await cmd.ExecuteScalarAsync())!;
        }

        // Without this the creator would immediately lose sight of their own project.
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO ProjectMembers (ProjectId, UserId, Role) VALUES (@ProjectId, @UserId, @Role);", conn, tx))
        {
            cmd.Parameters.AddInt("ProjectId", projectId);
            cmd.Parameters.AddInt("UserId", userId);
            cmd.Parameters.AddText("Role", ProjectRoles.Manager);
            await cmd.ExecuteNonQueryAsync();
        }

        // A project with no folder cannot hold a ticket, so give it one to start with.
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO Folders (ProjectId, FolderName, FolderCode, CreatedByUserId) VALUES (@ProjectId, 'General', 'GEN', @UserId);", conn, tx))
        {
            cmd.Parameters.AddInt("ProjectId", projectId);
            cmd.Parameters.AddInt("UserId", userId);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return projectId;
    }

    /// <summary>
    /// Deletes the project and, by cascade, every ticket in it with their tags, comments and history,
    /// plus its membership rows. Returns false when the project no longer exists.
    /// </summary>
    public async Task<bool> DeleteAsync(int projectId)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM Projects WHERE ProjectId = @ProjectId;", conn);
        cmd.Parameters.AddInt("ProjectId", projectId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<ProjectSuggestions> GetSuggestionsAsync(int projectId)
    {
        const string sql = @"
            SELECT DISTINCT tt.Tag FROM TicketTags tt JOIN Tickets t ON t.TicketId = tt.TicketId
            WHERE t.ProjectId = @ProjectId ORDER BY tt.Tag;";

        var result = new ProjectSuggestions();
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("ProjectId", projectId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Tags.Add(reader.Str("Tag"));
        return result;
    }

    private static void AddViewer(NpgsqlCommand cmd, Viewer viewer)
    {
        cmd.Parameters.AddInt("UserId", viewer.UserId);
        cmd.Parameters.AddBool("IsAdmin", viewer.IsAdmin);
        cmd.Parameters.AddBool("IsGuest", viewer.IsGuest);
    }

    private static async Task<List<Project>> ReadProjectsAsync(NpgsqlCommand cmd)
    {
        var list = new List<Project>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new Project
            {
                ProjectId = r.Int("ProjectId"),
                ProjectName = r.Str("ProjectName"),
                ProjectCode = r.Str("ProjectCode"),
                CreatedAt = r.Utc("CreatedAt"),
                CreatedByName = r.NStr("CreatedByName"),
                TicketCount = r.Int("TicketCount"),
                OpenTicketCount = r.Int("OpenTicketCount"),
                LastActivity = r.Utc("LastActivity"),
                MyRole = r.NStr("MyRole")
            });
        }
        return list;
    }
}
