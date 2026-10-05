using System.Text;
using System.Text.Json;
using QaDocBackend.Models;

namespace QaDocBackend.Infrastructure;

/// <summary>
/// The second provider behind the same interface: Google's generateContent endpoint, with the
/// screenshots inline as inline_data. The key rides in the x-goog-api-key header rather than the
/// query string, so it never appears in a URL a proxy might log.
/// </summary>
public class GeminiTestCaseGenerator(
    IHttpClientFactory httpFactory,
    GeminiSettings settings,
    TestCaseGeneratorSettings limits,
    ILogger<GeminiTestCaseGenerator> logger) : ITestCaseGenerator
{
    private const string EndpointPrefix = "https://generativelanguage.googleapis.com/v1beta/models/";

    public async Task<GeneratedTestCaseSet> GenerateAsync(GenerateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new TestCaseGeneratorException(
                "No AI key is configured (set Gemini__ApiKey). Use Import / manual to paste cases in.");
        if (string.IsNullOrWhiteSpace(settings.Model))
            throw new TestCaseGeneratorException("No model is configured (set Gemini__Model).");

        int maxImages = Math.Max(1, limits.MaxImages);
        if (req.Images.Count > maxImages)
            throw new TestCaseGeneratorException(
                $"This suite has {req.Images.Count} screenshots; this provider accepts at most {maxImages} at a time. " +
                "Remove some, or use Import / manual.");

        string payload = JsonSerializer.Serialize(new
        {
            systemInstruction = new { parts = new[] { new { text = TestCasePrompt.System(req.Options) } } },
            contents = new[]
            {
                new { role = "user", parts = UserParts(req) }
            },
            generationConfig = new { responseMimeType = "application/json", temperature = 0.2 }
        });

        string body = await ProviderHttp.PostJsonAsync(
            httpFactory.CreateClient(GroqTestCaseGenerator.HttpClientName),
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, Url())
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                message.Headers.Add("x-goog-api-key", settings.ApiKey);
                return message;
            },
            logger, ct);

        return TestCaseParser.Parse(ReplyText(body), req.Options);
    }

    private string Url() =>
        $"{EndpointPrefix}{Uri.EscapeDataString(settings.Model.Trim())}:generateContent";

    private static object[] UserParts(GenerateRequest req)
    {
        var parts = new List<object>
        {
            new { text = TestCasePrompt.User(req.BusinessDescription, req.Images.Count) }
        };
        parts.AddRange(req.Images.Select(image => new
        {
            inline_data = new { mime_type = image.ContentType, data = Convert.ToBase64String(image.Content) }
        }));
        return parts.ToArray();
    }

    /// <summary>candidates[0].content.parts[*].text, joined. Never logs what came back.</summary>
    private static string ReplyText(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() == 0)
            throw new TestCaseGeneratorException(
                "The AI returned no answer. Try again, or use Import / manual.", 502);

        var candidate = candidates[0];
        if (!candidate.TryGetProperty("content", out var content)
            || !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            throw new TestCaseGeneratorException(
                "The AI returned an empty answer. Try again, or use Import / manual.", 502);

        string text = string.Join("\n", parts.EnumerateArray()
            .Where(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("text", out _))
            .Select(p => p.GetProperty("text").GetString()));

        if (string.IsNullOrWhiteSpace(text))
            throw new TestCaseGeneratorException(
                "The AI returned an empty answer. Try again, or use Import / manual.", 502);
        return text;
    }
}
