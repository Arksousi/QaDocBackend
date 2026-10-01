using Npgsql;
using QaDocBackend.Data;
using QaDocBackend.Models;

namespace QaDocBackend.Repositories;

public interface IUserRepository
{
    Task<bool> AnyUsersAsync();
    Task<IEnumerable<User>> GetAllAsync();
    Task<IEnumerable<UserOption>> GetActiveOptionsAsync();
    /// <summary>Every active person's load, fullest first against their limit, for the Users Dashboard.</summary>
    Task<IEnumerable<UserWorkload>> GetWorkloadAsync();
    Task<bool> SetTicketLimitAsync(int userId, int? ticketLimit);
    /// <summary>
    /// The hover card for <paramref name="userId"/>, or null when there is no such user. Projects are
    /// only those <paramref name="viewer"/> can open too, so a card never names a project they cannot see.
    /// </summary>
    Task<UserCard?> GetCardAsync(int userId, Viewer viewer);
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
    /// <summary>Your own profile fields. Username, role and limit are not touched.</summary>
    Task<bool> UpdateProfileAsync(int userId, UpdateProfileRequest request);
    /// <summary>Stores (or replaces) the picture and returns its new version.</summary>
    Task<int> SetAvatarAsync(int userId, string contentType, byte[] content);
    /// <summary>Removes the picture, if any, and returns the new version (so cached copies are dropped).</summary>
    Task<int> DeleteAvatarAsync(int userId);
    Task<AvatarContent?> GetAvatarAsync(int userId);
    /// <summary>Active people who have a picture, with its current version.</summary>
    Task<IEnumerable<AvatarVersion>> GetAvatarVersionsAsync();
}

public class UserRepository(ISqlConnectionFactory db) : IUserRepository
{
    private const string SelectUser =
        "SELECT UserId, Username, DisplayName, PasswordHash, Role, IsActive, TicketLimit, TokenVersion, CreatedAt, "
        + "Email, JobTitle, Phone, Bio, AvatarVersion FROM Users";

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
        await using var cmd = new NpgsqlCommand($@"
            SELECT u.UserId, u.DisplayName, u.Username, u.TicketLimit, {Workload.OpenTicketsOf("u.UserId")} AS OpenTickets
            FROM Users u WHERE u.IsActive ORDER BY u.DisplayName;", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(MapOption(r));
        return list;
    }

    public async Task<IEnumerable<UserWorkload>> GetWorkloadAsync()
    {
        // Ordered by how full they are, so whoever needs attention is at the top; people with no
        // limit follow, busiest first.
        string sql = $@"
            SELECT x.* FROM (
                SELECT u.UserId, u.DisplayName, u.Username, u.Role, u.TicketLimit,
                       {Workload.OpenTicketsOf("u.UserId")} AS OpenTickets
                FROM Users u WHERE u.IsActive) x
            ORDER BY (x.TicketLimit IS NULL), x.OpenTickets::numeric / NULLIF(x.TicketLimit, 0) DESC NULLS LAST,
                     x.OpenTickets DESC, x.DisplayName;";

        var list = new List<UserWorkload>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new UserWorkload(r.Int("UserId"), r.Str("DisplayName"), r.Str("Username"), r.Str("Role"),
                r.Int("OpenTickets"), r.NInt("TicketLimit")));
        }
        return list;
    }

    public async Task<UserCard?> GetCardAsync(int userId, Viewer viewer)
    {
        // Totals use the same "real projects only" rule as Workload, so the card's unfinished count
        // always equals the one on the Users Dashboard and in Assigned To.
        string sql = $@"
            SELECT u.UserId, u.DisplayName, u.Username, u.Role, u.IsActive, u.CreatedAt, u.TicketLimit,
                   u.JobTitle, u.Email, u.AvatarVersion,
                   {Workload.OpenTicketsOf("u.UserId")} AS OpenTickets,
                   (SELECT COUNT(*) FILTER (WHERE t.State = 'Closed')
                    FROM TicketAssignees ta JOIN Tickets t ON t.TicketId = ta.TicketId
                    JOIN Projects p ON p.ProjectId = t.ProjectId
                    WHERE ta.UserId = u.UserId AND NOT p.IsDemo) AS ClosedTickets,
                   (SELECT COUNT(*)
                    FROM TicketAssignees ta JOIN Tickets t ON t.TicketId = ta.TicketId
                    JOIN Projects p ON p.ProjectId = t.ProjectId
                    WHERE ta.UserId = u.UserId AND NOT p.IsDemo) AS TotalAssigned
            FROM Users u WHERE u.UserId = @UserId;

            SELECT p.ProjectId, p.ProjectCode, p.ProjectName, m.Role
            FROM ProjectMembers m
            JOIN Projects p ON p.ProjectId = m.ProjectId
            WHERE m.UserId = @UserId AND NOT p.IsDemo
              AND (@IsAdmin OR EXISTS (SELECT 1 FROM ProjectMembers me WHERE me.ProjectId = p.ProjectId AND me.UserId = @ViewerId))
            ORDER BY p.ProjectName;";

        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("UserId", userId);
        cmd.Parameters.AddInt("ViewerId", viewer.UserId);
        cmd.Parameters.AddBool("IsAdmin", viewer.IsAdmin);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        var projects = new List<UserCardProject>();
        var card = new UserCard(r.Int("UserId"), r.Str("DisplayName"), r.Str("Username"), r.Str("Role"), r.Bool("IsActive"),
            r.Utc("CreatedAt"), r.NInt("TicketLimit"), r.Int("OpenTickets"), r.Int("ClosedTickets"), r.Int("TotalAssigned"), projects,
            r.NStr("JobTitle"), r.NStr("Email"), r.Int("AvatarVersion"));

        await r.NextResultAsync();
        while (await r.ReadAsync())
            projects.Add(new UserCardProject(r.Int("ProjectId"), r.Str("ProjectCode"), r.Str("ProjectName"), r.Str("Role")));
        return card;
    }

    public async Task<bool> SetTicketLimitAsync(int userId, int? ticketLimit)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE Users SET TicketLimit = @TicketLimit WHERE UserId = @UserId;", conn);
        cmd.Parameters.AddInt("UserId", userId);
        cmd.Parameters.AddInt("TicketLimit", ticketLimit);
        return await cmd.ExecuteNonQueryAsync() > 0;
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
            INSERT INTO Users (Username, DisplayName, PasswordHash, Role, TicketLimit)
            VALUES (@Username, @DisplayName, @PasswordHash, @Role, @TicketLimit)
            RETURNING UserId;";
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        AddCreateParams(cmd, request, passwordHash);
        cmd.Parameters.AddText("Role", request.Role);
        cmd.Parameters.AddInt("TicketLimit", request.TicketLimit);
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
            SET DisplayName = @DisplayName, Role = @Role, TicketLimit = @TicketLimit,
                TokenVersion = CASE WHEN IsActive AND NOT @IsActive THEN TokenVersion + 1 ELSE TokenVersion END,
                IsActive = @IsActive
            WHERE UserId = @UserId;";
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("UserId", userId);
        cmd.Parameters.AddText("DisplayName", request.DisplayName.Trim());
        cmd.Parameters.AddText("Role", request.Role);
        cmd.Parameters.AddBool("IsActive", request.IsActive);
        cmd.Parameters.AddInt("TicketLimit", request.TicketLimit);
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

    public async Task<bool> UpdateProfileAsync(int userId, UpdateProfileRequest request)
    {
        // Blank means "not given": stored as NULL, never as an empty string.
        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        const string sql = @"
            UPDATE Users SET DisplayName = @DisplayName, Email = @Email, JobTitle = @JobTitle, Phone = @Phone, Bio = @Bio
            WHERE UserId = @UserId;";
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("UserId", userId);
        cmd.Parameters.AddText("DisplayName", request.DisplayName.Trim());
        cmd.Parameters.AddText("Email", Clean(request.Email));
        cmd.Parameters.AddText("JobTitle", Clean(request.JobTitle));
        cmd.Parameters.AddText("Phone", Clean(request.Phone));
        cmd.Parameters.AddText("Bio", Clean(request.Bio));
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<int> SetAvatarAsync(int userId, string contentType, byte[] content)
    {
        // One statement: the picture and its version move together.
        const string sql = @"
            WITH saved AS (
                INSERT INTO UserAvatars (UserId, ContentType, Content) VALUES (@UserId, @ContentType, @Content)
                ON CONFLICT (UserId) DO UPDATE SET ContentType = EXCLUDED.ContentType, Content = EXCLUDED.Content, UpdatedAt = now()
                RETURNING UserId)
            UPDATE Users SET AvatarVersion = AvatarVersion + 1
            WHERE UserId = (SELECT UserId FROM saved) RETURNING AvatarVersion;";
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("UserId", userId);
        cmd.Parameters.AddText("ContentType", contentType);
        cmd.Parameters.Add(new NpgsqlParameter("Content", NpgsqlTypes.NpgsqlDbType.Bytea) { Value = content });
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<int> DeleteAvatarAsync(int userId)
    {
        const string sql = @"
            DELETE FROM UserAvatars WHERE UserId = @UserId;
            UPDATE Users SET AvatarVersion = AvatarVersion + 1 WHERE UserId = @UserId RETURNING AvatarVersion;";
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("UserId", userId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<AvatarContent?> GetAvatarAsync(int userId)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT ContentType, Content FROM UserAvatars WHERE UserId = @UserId;", conn);
        cmd.Parameters.AddInt("UserId", userId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new AvatarContent(r.Str("ContentType"), (byte[])r["Content"]);
    }

    public async Task<IEnumerable<AvatarVersion>> GetAvatarVersionsAsync()
    {
        var list = new List<AvatarVersion>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT u.UserId, u.AvatarVersion FROM Users u JOIN UserAvatars a ON a.UserId = u.UserId
            WHERE u.IsActive ORDER BY u.UserId;", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new AvatarVersion(r.Int("UserId"), r.Int("AvatarVersion")));
        return list;
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
        TicketLimit = r.NInt("TicketLimit"),
        TokenVersion = r.Int("TokenVersion"),
        CreatedAt = r.Utc("CreatedAt"),
        Email = r.NStr("Email"),
        JobTitle = r.NStr("JobTitle"),
        Phone = r.NStr("Phone"),
        Bio = r.NStr("Bio"),
        AvatarVersion = r.Int("AvatarVersion")
    };

    /// <summary>Shared with ProjectMemberRepository's assignee list: both feed the same picker.</summary>
    internal static UserOption MapOption(NpgsqlDataReader r) =>
        new(r.Int("UserId"), r.Str("DisplayName"), r.Str("Username"), r.Int("OpenTickets"), r.NInt("TicketLimit"));
}
