using System.Text;
using System.Text.Json;
using QaDocBackend.Models;

namespace QaDocBackend.Infrastructure;

public class GeminiDocumentGenerator(
    IHttpClientFactory httpFactory,
    GeminiSettings settings,
    ILogger<GeminiDocumentGenerator> logger) : IDocumentGenerator
{
    public async Task<QcScreenSummary> AnalyzeScreenAsync(
        GenerateImage image, string? caption, string? businessDescription,
        string? runningSummary, string language, CancellationToken ct)
    {
        EnsureConfigured();

        string payload = JsonSerializer.Serialize(new
        {
            system_instruction = new
            {
                parts = new object[] { new { text = QcPrompts.ScreenSystem(language) } }
            },
            contents = new object[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = QcPrompts.ScreenUser(caption, businessDescription, runningSummary) },
                        new
                        {
                            inline_data = new
                            {
                                mime_type = image.ContentType,
                                data = Convert.ToBase64String(image.Content)
                            }
                        }
                    }
                }
            },
            generationConfig = new
            {
                response_mime_type = "application/json",
                temperature = 0.2
            }
        });

        string reply = await SendGenerateContentAsync(payload, ct);
        return GroqDocumentGenerator.ParseScreenSummary(reply, caption);
    }

    public async Task<string> GenerateDocumentMarkdownAsync(
        string kind, string appName, string? businessDescription,
        List<(int sortOrder, string? caption, QcScreenSummary summary)> screens,
        string language, CancellationToken ct)
    {
        EnsureConfigured();

        string payload = JsonSerializer.Serialize(new
        {
            system_instruction = new
            {
                parts = new object[] { new { text = QcPrompts.DocumentSystem(kind, appName, language) } }
            },
            contents = new object[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = QcPrompts.DocumentUser(appName, businessDescription, screens) }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.3
            }
        });

        string reply = await SendGenerateContentAsync(payload, ct);
        return TestCaseParser.StripFences(reply).Trim();
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new DocumentGeneratorException(
                "No AI key is configured (set Gemini__ApiKey). Use Import / manual to paste documentation in.");
        if (string.IsNullOrWhiteSpace(settings.Model))
            throw new DocumentGeneratorException("No model is configured (set Gemini__Model).");
    }

    private string Url() =>
        $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(settings.Model)}:generateContent?key={Uri.EscapeDataString(settings.ApiKey)}";

    private async Task<string> SendGenerateContentAsync(string payload, CancellationToken ct)
    {
        string body = await ProviderHttp.PostJsonAsync(
            httpFactory.CreateClient(GroqTestCaseGenerator.HttpClientName),
            () => new HttpRequestMessage(HttpMethod.Post, Url())
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            },
            logger, ct);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() == 0)
            throw new DocumentGeneratorException(
                "The AI returned no answer. Try again, or use Import / manual.", 502);

        var candidate = candidates[0];
        if (!candidate.TryGetProperty("content", out var content)
            || !content.TryGetProperty("parts", out var parts)
            || parts.ValueKind != JsonValueKind.Array
            || parts.GetArrayLength() == 0)
            throw new DocumentGeneratorException(
                "The AI returned an empty answer. Try again, or use Import / manual.", 502);

        var part = parts[0];
        return part.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty;
    }
}
