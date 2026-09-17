using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace QaDocBackend.Models;

public static partial class Codes
{
    public const int MaxLength = 10;

    /// <summary>Letters and digits only: codes end up in ticket keys, where "-" is the separator.</summary>
    [GeneratedRegex("^[A-Za-z0-9]{1,10}$")]
    private static partial Regex Allowed();

    public static bool IsValid(string? code) => code != null && Allowed().IsMatch(code);

    /// <summary>Codes are compared and displayed uppercase, so store them that way.</summary>
    public static string Normalise(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>Builds the public ticket key, e.g. RMS-V1-0001.</summary>
    public static string TicketKey(string projectCode, string folderCode, int sequence) =>
        $"{projectCode}-{folderCode}-{sequence:D4}";
}

public class Folder
{
    public int FolderId { get; set; }
    public int ProjectId { get; set; }
    public string FolderName { get; set; } = string.Empty;
    public string FolderCode { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    // Read-only summary, same shape as Project
    public int TicketCount { get; set; }
    public int OpenTicketCount { get; set; }
}

public class SaveFolderRequest
{
    [Required, StringLength(150, MinimumLength = 1)]
    public string FolderName { get; set; } = string.Empty;

    [Required, StringLength(Codes.MaxLength, MinimumLength = 1)]
    public string FolderCode { get; set; } = string.Empty;
}
