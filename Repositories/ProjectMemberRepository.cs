using Npgsql;
using QaDocBackend.Data;
using QaDocBackend.Models;

namespace QaDocBackend.Repositories;

public interface IProjectMemberRepository
{
    /// <summary>The user's role in the project, or null when they are not a member.</summary>
    Task<string?> GetRoleAsync(int projectId, int userId);
    Task<IEnumerable<ProjectMember>> GetByProjectAsync(int projectId);
    /// <summary>Every member of these projects with how many tickets they hold and have closed, for the Leader Dashboard.</summary>
    Task<ILookup<int, MemberScore>> GetScoresAsync(int[] projectIds);
    /// <summary>Active users a ticket here may be given to: Contributors and global Admins.</summary>
    Task<IEnumerable<UserOption>> GetAssignableAsync(int projectId);
    /// <summary>Adds the member, or changes their role when they are already on the project.</summary>
    Task SaveAsync(int projectId, int userId, string role);
    Task<bool> RemoveAsync(int projectId, int userId);
}

public class ProjectMemberRepository(ISqlConnectionFactory db) : IProjectMemberRepository
{
    public async Task<string?> GetRoleAsync(int projectId, int userId)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT Role FROM ProjectMembers WHERE ProjectId = @ProjectId AND UserId = @UserId;", conn);
        cmd.Parameters.AddInt("ProjectId", projectId);
        cmd.Parameters.AddInt("UserId", userId);
        return await cmd.ExecuteScalarAsync() as string;
    }

    public async Task<IEnumerable<ProjectMember>> GetByProjectAsync(int projectId)
    {
        const string sql = @"
            SELECT m.ProjectId, m.UserId, u.DisplayName, u.Username, m.Role, u.Role AS UserRole, u.IsActive, m.AddedAt
            FROM ProjectMembers m
            JOIN Users u ON u.UserId = m.UserId
            WHERE m.ProjectId = @ProjectId
            ORDER BY u.DisplayName;";

        var list = new List<ProjectMember>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("ProjectId", projectId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new ProjectMember
            {
                ProjectId = r.Int("ProjectId"),
                UserId = r.Int("UserId"),
                DisplayName = r.Str("DisplayName"),
                Username = r.Str("Username"),
                Role = r.Str("Role"),
                UserRole = r.Str("UserRole"),
                IsActive = r.Bool("IsActive"),
                AddedAt = r.Utc("AddedAt")
            });
        }
        return list;
    }

    /// <remarks>
    /// Keyed on membership, so a member with nothing assigned still gets a row (an empty bar), and
    /// a ticket held by someone who is not a member -- an Admin, or a person since removed -- is in
    /// the project's totals but on nobody's bar.
    /// </remarks>
    public async Task<ILookup<int, MemberScore>> GetScoresAsync(int[] projectIds)
    {
        string sql = $@"
            SELECT m.ProjectId, m.UserId, u.DisplayName, m.Role, u.IsActive, u.TicketLimit,
                   COUNT(t.TicketId) AS Assigned,
                   COUNT(t.TicketId) FILTER (WHERE t.State = 'Closed') AS Closed,
                   {Workload.OpenTicketsOf("m.UserId")} AS OpenTickets
            FROM ProjectMembers m
            JOIN Users u ON u.UserId = m.UserId
            -- A shared ticket counts once for each person on it: closing it is done for all of them.
            LEFT JOIN (TicketAssignees ta JOIN Tickets t ON t.TicketId = ta.TicketId)
                   ON ta.UserId = m.UserId AND t.ProjectId = m.ProjectId
            WHERE m.ProjectId = ANY(@ProjectIds)
            GROUP BY m.ProjectId, m.UserId, u.DisplayName, m.Role, u.IsActive, u.TicketLimit
            ORDER BY u.DisplayName;";

        var rows = new List<(int ProjectId, MemberScore Score)>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddIntArray("ProjectIds", projectIds);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            rows.Add((r.Int("ProjectId"), new MemberScore(
                r.Int("UserId"), r.Str("DisplayName"), r.Str("Role"), r.Bool("IsActive"),
                r.Int("Assigned"), r.Int("Closed"), r.Int("OpenTickets"), r.NInt("TicketLimit"))));
        }
        return rows.ToLookup(x => x.ProjectId, x => x.Score);
    }

    public async Task<IEnumerable<UserOption>> GetAssignableAsync(int projectId)
    {
        string sql = $@"
            SELECT u.UserId, u.DisplayName, u.Username, u.TicketLimit, {Workload.OpenTicketsOf("u.UserId")} AS OpenTickets
            FROM Users u
            LEFT JOIN ProjectMembers m ON m.UserId = u.UserId AND m.ProjectId = @ProjectId
            WHERE u.IsActive AND (u.Role = 'Admin' OR m.Role = 'Contributor')
            ORDER BY u.DisplayName;";

        var list = new List<UserOption>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("ProjectId", projectId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(UserRepository.MapOption(r));
        return list;
    }

    public async Task SaveAsync(int projectId, int userId, string role)
    {
        const string sql = @"
            INSERT INTO ProjectMembers (ProjectId, UserId, Role) VALUES (@ProjectId, @UserId, @Role)
            ON CONFLICT (ProjectId, UserId) DO UPDATE SET Role = EXCLUDED.Role;";

        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("ProjectId", projectId);
        cmd.Parameters.AddInt("UserId", userId);
        cmd.Parameters.AddText("Role", role);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> RemoveAsync(int projectId, int userId)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM ProjectMembers WHERE ProjectId = @ProjectId AND UserId = @UserId;", conn);
        cmd.Parameters.AddInt("ProjectId", projectId);
        cmd.Parameters.AddInt("UserId", userId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }
}
