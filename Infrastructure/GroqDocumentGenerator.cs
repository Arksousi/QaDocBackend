using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QaDocBackend.Models;

namespace QaDocBackend.Infrastructure;

public class GroqDocumentGenerator(
    IHttpClientFactory httpFactory,
    GroqSettings settings,
    ILogger<GroqDocumentGenerator> logger) : IDocumentGenerator
{
    private const string Endpoint = "https://api.groq.com/openai/v1/chat/completions";

    public async Task<QcScreenSummary> AnalyzeScreenAsync(
        GenerateImage image, string? caption, string? businessDescription,
        string? runningSummary, string language, CancellationToken ct)
    {
        EnsureConfigured();

        var screenPayloadObj = new Dictionary<string, object?>
        {
            ["model"] = settings.VisionModel.Trim(),
            ["response_format"] = new { type = "json_object" },
            ["temperature"] = 0.2,
            ["max_completion_tokens"] = 2048,
            ["messages"] = new object[]
            {
                new { role = "system", content = QcPrompts.ScreenSystem(language) },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = QcPrompts.ScreenUser(caption, businessDescription, runningSummary) },
                        new
                        {
                            type = "image_url",
                            image_url = new { url = GroqTestCaseGenerator.DataUrl(image) }
                        }
                    }
                }
            }
        };

        if (GroqTestCaseGenerator.IsReasoningModel(settings.VisionModel))
        {
            screenPayloadObj["reasoning_format"] = "parsed";
        }

        string payload = JsonSerializer.Serialize(screenPayloadObj);

        string reply = await SendChatCompletionAsync(payload, ct);
        return ParseScreenSummary(reply, caption);
    }

    public async Task<string> GenerateDocumentMarkdownAsync(
        string kind, string appName, string? businessDescription,
        List<(int sortOrder, string? caption, QcScreenSummary summary)> screens,
        string language, CancellationToken ct)
    {
        EnsureConfigured();

        string model = !string.IsNullOrWhiteSpace(settings.TextModel)
            ? settings.TextModel.Trim()
            : settings.VisionModel.Trim();

        var docPayloadObj = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["temperature"] = 0.3,
            ["max_completion_tokens"] = 8192,
            ["messages"] = new object[]
            {
                new { role = "system", content = QcPrompts.DocumentSystem(kind, appName, language) },
                new { role = "user", content = QcPrompts.DocumentUser(appName, businessDescription, screens) }
            }
        };

        if (GroqTestCaseGenerator.IsReasoningModel(model))
        {
            docPayloadObj["reasoning_format"] = "hidden";
        }

        string payload = JsonSerializer.Serialize(docPayloadObj);

        string reply = await SendChatCompletionAsync(payload, ct);
        return TestCaseParser.StripFences(reply).Trim();
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new DocumentGeneratorException(
                "No AI key is configured (set Groq__ApiKey). Use Import / manual to paste documentation in.");
        if (string.IsNullOrWhiteSpace(settings.VisionModel))
            throw new DocumentGeneratorException("No vision model is configured (set Groq__VisionModel).");
    }

    private async Task<string> SendChatCompletionAsync(string payload, CancellationToken ct)
    {
        string body = await ProviderHttp.PostJsonAsync(
            httpFactory.CreateClient(GroqTestCaseGenerator.HttpClientName),
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
                return message;
            },
            logger, ct);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            throw new DocumentGeneratorException(
                "The AI returned no answer. Try again, or use Import / manual.", 502);

        var messageElement = choices[0];
        if (!messageElement.TryGetProperty("message", out var messageBody)
            || !messageBody.TryGetProperty("content", out var content))
            throw new DocumentGeneratorException(
                "The AI returned an empty answer. Try again, or use Import / manual.", 502);

        return content.GetString() ?? string.Empty;
    }

    internal static QcScreenSummary ParseScreenSummary(string rawJson, string? defaultTitle)
    {
        string cleaned = TestCaseParser.StripFences(rawJson);
        try
        {
            var summary = JsonSerializer.Deserialize<QcScreenSummary>(cleaned,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (summary != null)
            {
                if (string.IsNullOrWhiteSpace(summary.ScreenTitle))
                    summary.ScreenTitle = defaultTitle ?? "Application Screen";
                return summary;
            }
        }
        catch (JsonException)
        {
            // Defensive fallback below
        }

        return new QcScreenSummary
        {
            ScreenTitle = defaultTitle ?? "Application Screen",
            Purpose = "Screen analyzed by AI",
            Notes = cleaned
        };
    }
}
