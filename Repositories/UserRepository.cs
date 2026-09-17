using Npgsql;
using QaDocBackend.Data;
using QaDocBackend.Models;

namespace QaDocBackend.Repositories;

public interface IUserRepository
{
    Task<bool> AnyUsersAsync();
    Task<IEnumerable<User>> GetAllAsync();
    Task<IEnumerable<UserOption>> GetActiveOptionsAsync();
    Task<UserRecord?> GetByIdAsync(int userId);
    Task<UserRecord?> GetByUsernameAsync(string username);
    Task<int> CreateAsync(CreateUserRequest request, string passwordHash);
    /// <summary>Creates the first admin only while the Users table is empty.</summary>
    /// <returns>The new id, or null when a user already exists.</returns>
    Task<int?> CreateFirstAdminAsync(CreateUserRequest request, string passwordHash);
    Task<bool> UpdateAsync(int userId, UpdateUserRequest request);
    /// <summary>Sets a new hash and bumps TokenVersion so existing sign-ins stop working.</summary>
    Task<bool> SetPasswordAsync(int userId, string passwordHash);
    Task<int> CountActiveAdminsAsync();
    Task<bool> IsActiveUserAsync(int userId);
}

public class UserRepository(ISqlConnectionFactory db) : IUserRepository
{
    private const string SelectUser =
        "SELECT UserId, Username, DisplayName, PasswordHash, Role, IsActive, TokenVersion, CreatedAt FROM Users";

    public async Task<bool> AnyUsersAsync()
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM Users);", conn);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<IEnumerable<User>> GetAllAsync()
    {
        var list = new List<User>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(SelectUser + " ORDER BY IsActive DESC, DisplayName;", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(Map(r));
        return list;
    }

    public async Task<IEnumerable<UserOption>> GetActiveOptionsAsync()
    {
        var list = new List<UserOption>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT UserId, DisplayName, Username FROM Users WHERE IsActive ORDER BY DisplayName;", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new UserOption(r.Int("UserId"), r.Str("DisplayName"), r.Str("Username")));
        return list;
    }

    public async Task<UserRecord?> GetByIdAsync(int userId)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(SelectUser + " WHERE UserId = @UserId;", conn);
        cmd.Parameters.AddInt("UserId", userId);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Map(r) : null;
    }

    public async Task<UserRecord?> GetByUsernameAsync(string username)
    {
        await using var conn = await db.OpenAsync();
        // Usernames are case-insensitive (see ux_users_username).
        await using var cmd = new NpgsqlCommand(SelectUser + " WHERE lower(Username) = lower(@Username);", conn);
        cmd.Parameters.AddText("Username", username.Trim());
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Map(r) : null;
    }

    public async Task<int> CreateAsync(CreateUserRequest request, string passwordHash)
    {
        const string sql = @"
            INSERT INTO Users (Username, DisplayName, PasswordHash, Role)
            VALUES (@Username, @DisplayName, @PasswordHash, @Role)
            RETURNING UserId;";
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        AddCreateParams(cmd, request, passwordHash);
        cmd.Parameters.AddText("Role", request.Role);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<int?> CreateFirstAdminAsync(CreateUserRequest request, string passwordHash)
    {
        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // The table lock makes the "table is empty" check race-free.
        await using (var lockCmd = new NpgsqlCommand("LOCK TABLE Users IN EXCLUSIVE MODE;", conn, tx))
            await lockCmd.ExecuteNonQueryAsync();

        const string sql = @"
            INSERT INTO Users (Username, DisplayName, PasswordHash, Role)
            SELECT @Username, @DisplayName, @PasswordHash, 'Admin'
            WHERE NOT EXISTS (SELECT 1 FROM Users)
            RETURNING UserId;";
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        AddCreateParams(cmd, request, passwordHash);
        var result = await cmd.ExecuteScalarAsync();
        await tx.CommitAsync();
        return result is int id ? id : null;
    }

    public async Task<bool> UpdateAsync(int userId, UpdateUserRequest request)
    {
        // Deactivating also revokes existing tokens.
        const string sql = @"
            UPDATE Users
            SET DisplayName = @DisplayName, Role = @Role,
                TokenVersion = CASE WHEN IsActive AND NOT @IsActive THEN TokenVersion + 1 ELSE TokenVersion END,
                IsActive = @IsActive
            WHERE UserId = @UserId;";
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("UserId", userId);
        cmd.Parameters.AddText("DisplayName", request.DisplayName.Trim());
        cmd.Parameters.AddText("Role", request.Role);
        cmd.Parameters.AddBool("IsActive", request.IsActive);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> SetPasswordAsync(int userId, string passwordHash)
    {
        const string sql = "UPDATE Users SET PasswordHash = @PasswordHash, TokenVersion = TokenVersion + 1 WHERE UserId = @UserId;";
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("UserId", userId);
        cmd.Parameters.AddText("PasswordHash", passwordHash);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<int> CountActiveAdminsAsync()
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM Users WHERE Role = 'Admin' AND IsActive;", conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<bool> IsActiveUserAsync(int userId)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM Users WHERE UserId = @UserId AND IsActive);", conn);
        cmd.Parameters.AddInt("UserId", userId);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private static void AddCreateParams(NpgsqlCommand cmd, CreateUserRequest r, string passwordHash)
    {
        cmd.Parameters.AddText("Username", r.Username.Trim());
        cmd.Parameters.AddText("DisplayName", r.DisplayName.Trim());
        cmd.Parameters.AddText("PasswordHash", passwordHash);
    }

    private static UserRecord Map(NpgsqlDataReader r) => new()
    {
        UserId = r.Int("UserId"),
        Username = r.Str("Username"),
        DisplayName = r.Str("DisplayName"),
        PasswordHash = r.Str("PasswordHash"),
        Role = r.Str("Role"),
        IsActive = r.Bool("IsActive"),
        TokenVersion = r.Int("TokenVersion"),
        CreatedAt = r.Utc("CreatedAt")
    };
}
