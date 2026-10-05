using QaDocBackend.Models;

namespace QaDocBackend.Infrastructure;

public class ConfiguredDocumentGenerator(
    DocumentGeneratorSettings settings,
    GroqDocumentGenerator groq,
    GeminiDocumentGenerator gemini,
    MockDocumentGenerator mock) : IDocumentGenerator
{
    public Task<QcScreenSummary> AnalyzeScreenAsync(
        GenerateImage image, string? caption, string? businessDescription,
        string? runningSummary, string language, CancellationToken ct) =>
        SelectGenerator().AnalyzeScreenAsync(image, caption, businessDescription, runningSummary, language, ct);

    public Task<string> GenerateDocumentMarkdownAsync(
        string kind, string appName, string? businessDescription,
        List<(int sortOrder, string? caption, QcScreenSummary summary)> screens,
        string language, CancellationToken ct) =>
        SelectGenerator().GenerateDocumentMarkdownAsync(kind, appName, businessDescription, screens, language, ct);

    private IDocumentGenerator SelectGenerator()
    {
        string provider = settings.Provider.Trim();
        if (string.Equals(provider, DocumentProviders.Groq, StringComparison.OrdinalIgnoreCase))
            return groq;
        if (string.Equals(provider, DocumentProviders.Gemini, StringComparison.OrdinalIgnoreCase))
            return gemini;
        if (string.Equals(provider, DocumentProviders.Mock, StringComparison.OrdinalIgnoreCase))
            return mock;

        throw new DocumentGeneratorException(
            "AI generation is switched off (DocumentGenerator:Provider = None). Use Import / manual to paste documentation in.");
    }
}
