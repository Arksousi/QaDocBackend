using QaDocBackend.Models;

namespace QaDocBackend.Infrastructure;

public static class DocumentProviders
{
    public const string None = "None";
    public const string Groq = "Groq";
    public const string Gemini = "Gemini";
    public const string Mock = "Mock";

    public static readonly string[] All = [None, Groq, Gemini, Mock];

    public static bool IsKnown(string? provider) =>
        All.Contains(provider ?? string.Empty, StringComparer.OrdinalIgnoreCase);
}

public class DocumentGeneratorSettings
{
    public string Provider { get; set; } = DocumentProviders.None;
    public bool Enabled { get; set; } = true;
    public int RateLimitPerHour { get; set; } = 5;
    public int MaxImages { get; set; } = 30;
    public int MaxImageBytes { get; set; } = 4 * 1024 * 1024;
    public int MaxImageWidth { get; set; } = 1400;
}

public class DocumentGeneratorException(string message, int statusCode = 400, Exception? inner = null)
    : Exception(message, inner)
{
    public int StatusCode { get; } = statusCode;
}

public interface IDocumentGenerator
{
    Task<QcScreenSummary> AnalyzeScreenAsync(
        GenerateImage image, string? caption, string? businessDescription,
        string? runningSummary, string language, CancellationToken ct);

    Task<string> GenerateDocumentMarkdownAsync(
        string kind, string appName, string? businessDescription,
        List<(int sortOrder, string? caption, QcScreenSummary summary)> screens,
        string language, CancellationToken ct);
}
