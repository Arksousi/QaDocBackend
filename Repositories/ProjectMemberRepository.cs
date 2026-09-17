using Npgsql;
using QaDocBackend.Data;
using QaDocBackend.Models;

namespace QaDocBackend.Repositories;

public interface IProjectMemberRepository
{
    /// <summary>The user's role in the project, or null when they are not a member.</summary>
    Task<string?> GetRoleAsync(int projectId, int userId);
    Task<IEnumerable<ProjectMember>> GetByProjectAsync(int projectId);
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
            SELECT m.ProjectId, m.UserId, u.DisplayName, u.Username, m.Role, u.IsActive, m.AddedAt
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
                IsActive = r.Bool("IsActive"),
                AddedAt = r.Utc("AddedAt")
            });
        }
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
