namespace QaDocBackend.Models;

public static class Attachments
{
    /// <summary>Upload ceiling. Kept modest: the bytes live in Postgres, not object storage.</summary>
    public const int MaxBytes = 25 * 1024 * 1024;

    /// <summary>
    /// Only formats a browser can play back natively. Anything else would upload happily and then
    /// show an empty player, which is worse than refusing it.
    /// </summary>
    public static readonly string[] AllowedContentTypes =
    [
        "video/mp4",
        "video/webm",
        "video/ogg",
        "video/quicktime"
    ];

    public static bool IsAllowed(string? contentType) =>
        contentType != null && AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase);
}

/// <summary>An attachment's metadata, without its bytes.</summary>
public class Attachment
{
    public int AttachmentId { get; set; }
    public int ProjectId { get; set; }
    public int? TicketId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long ByteSize { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>An attachment's bytes, ready to send.</summary>
public class AttachmentContent
{
    public byte[] Content { get; set; } = [];
    public string ContentType { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int ProjectId { get; set; }
}
