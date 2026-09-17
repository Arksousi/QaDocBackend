using Npgsql;
using QaDocBackend.Data;
using QaDocBackend.Models;

namespace QaDocBackend.Repositories;

public interface IFolderRepository
{
    Task<IEnumerable<Folder>> GetByProjectAsync(int projectId);
    Task<Folder?> GetByIdAsync(int folderId);
    Task<int> CreateAsync(int projectId, SaveFolderRequest request, int userId);
    Task<bool> UpdateAsync(int folderId, SaveFolderRequest request);
    Task<bool> DeleteAsync(int folderId);
    Task<bool> HasTicketsAsync(int folderId);
}

public class FolderRepository(ISqlConnectionFactory db) : IFolderRepository
{
    private const string SelectFolders = @"
        SELECT f.FolderId, f.ProjectId, f.FolderName, f.FolderCode, f.CreatedAt,
               s.TicketCount, s.OpenTicketCount
        FROM Folders f
        LEFT JOIN LATERAL (SELECT COUNT(*) AS TicketCount,
                                  COUNT(*) FILTER (WHERE t.State <> 'Closed') AS OpenTicketCount
                           FROM Tickets t WHERE t.FolderId = f.FolderId) s ON TRUE";

    public async Task<IEnumerable<Folder>> GetByProjectAsync(int projectId)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            SelectFolders + " WHERE f.ProjectId = @ProjectId ORDER BY f.FolderCode;", conn);
        cmd.Parameters.AddInt("ProjectId", projectId);
        return await ReadAsync(cmd);
    }

    public async Task<Folder?> GetByIdAsync(int folderId)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(SelectFolders + " WHERE f.FolderId = @FolderId;", conn);
        cmd.Parameters.AddInt("FolderId", folderId);
        return (await ReadAsync(cmd)).FirstOrDefault();
    }

    public async Task<int> CreateAsync(int projectId, SaveFolderRequest request, int userId)
    {
        const string sql = @"
            INSERT INTO Folders (ProjectId, FolderName, FolderCode, CreatedByUserId)
            VALUES (@ProjectId, @FolderName, @FolderCode, @UserId) RETURNING FolderId;";

        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("ProjectId", projectId);
        cmd.Parameters.AddText("FolderName", request.FolderName.Trim());
        cmd.Parameters.AddText("FolderCode", Codes.Normalise(request.FolderCode));
        cmd.Parameters.AddInt("UserId", userId);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Renaming a folder's code renames every ticket key in it, by design.</summary>
    public async Task<bool> UpdateAsync(int folderId, SaveFolderRequest request)
    {
        const string sql = "UPDATE Folders SET FolderName = @FolderName, FolderCode = @FolderCode WHERE FolderId = @FolderId;";
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("FolderId", folderId);
        cmd.Parameters.AddText("FolderName", request.FolderName.Trim());
        cmd.Parameters.AddText("FolderCode", Codes.Normalise(request.FolderCode));
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeleteAsync(int folderId)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM Folders WHERE FolderId = @FolderId;", conn);
        cmd.Parameters.AddInt("FolderId", folderId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> HasTicketsAsync(int folderId)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM Tickets WHERE FolderId = @FolderId);", conn);
        cmd.Parameters.AddInt("FolderId", folderId);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<List<Folder>> ReadAsync(NpgsqlCommand cmd)
    {
        var list = new List<Folder>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new Folder
            {
                FolderId = r.Int("FolderId"),
                ProjectId = r.Int("ProjectId"),
                FolderName = r.Str("FolderName"),
                FolderCode = r.Str("FolderCode"),
                CreatedAt = r.Utc("CreatedAt"),
                TicketCount = r.Int("TicketCount"),
                OpenTicketCount = r.Int("OpenTicketCount")
            });
        }
        return list;
    }
}
