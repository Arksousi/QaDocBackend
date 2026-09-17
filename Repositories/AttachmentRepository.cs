using Npgsql;
using NpgsqlTypes;
using QaDocBackend.Data;
using QaDocBackend.Models;

namespace QaDocBackend.Repositories;

public interface IAttachmentRepository
{
    Task<Attachment> CreateAsync(int projectId, string fileName, string contentType, byte[] content, int userId);
    /// <summary>Bytes plus the project they belong to, so the caller can check access before sending.</summary>
    Task<AttachmentContent?> GetContentAsync(int attachmentId);
}

public class AttachmentRepository(ISqlConnectionFactory db) : IAttachmentRepository
{
    public async Task<Attachment> CreateAsync(int projectId, string fileName, string contentType, byte[] content, int userId)
    {
        const string sql = @"
            INSERT INTO TicketAttachments (ProjectId, FileName, ContentType, ByteSize, Content, UploadedByUserId)
            VALUES (@ProjectId, @FileName, @ContentType, @ByteSize, @Content, @UserId)
            RETURNING AttachmentId, CreatedAt;";

        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("ProjectId", projectId);
        cmd.Parameters.AddText("FileName", fileName);
        cmd.Parameters.AddText("ContentType", contentType);
        cmd.Parameters.Add(new NpgsqlParameter("ByteSize", NpgsqlDbType.Bigint) { Value = (long)content.Length });
        cmd.Parameters.Add(new NpgsqlParameter("Content", NpgsqlDbType.Bytea) { Value = content });
        cmd.Parameters.AddInt("UserId", userId);

        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return new Attachment
        {
            AttachmentId = r.Int("AttachmentId"),
            ProjectId = projectId,
            FileName = fileName,
            ContentType = contentType,
            ByteSize = content.Length,
            CreatedAt = r.Utc("CreatedAt")
        };
    }

    public async Task<AttachmentContent?> GetContentAsync(int attachmentId)
    {
        const string sql =
            "SELECT ProjectId, FileName, ContentType, Content FROM TicketAttachments WHERE AttachmentId = @AttachmentId;";

        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddInt("AttachmentId", attachmentId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        return new AttachmentContent
        {
            ProjectId = r.Int("ProjectId"),
            FileName = r.Str("FileName"),
            ContentType = r.Str("ContentType"),
            Content = (byte[])r["Content"]
        };
    }
}
