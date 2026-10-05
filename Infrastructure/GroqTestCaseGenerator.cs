using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QaDocBackend.Models;

namespace QaDocBackend.Infrastructure;

/// <summary>
/// Groq speaks the OpenAI chat-completions dialect, so this is one POST with a Bearer key, JSON
/// mode on, and the screenshots inline as base64 data URLs. The model name comes from config
/// (Groq__VisionModel) because Groq rotates its vision models; nothing about the model is written
/// into the code.
/// </summary>
public class GroqTestCaseGenerator(
    IHttpClientFactory httpFactory,
    GroqSettings settings,
    ILogger<GroqTestCaseGenerator> logger) : ITestCaseGenerator
{
    /// <summary>Named client, registered in Program.cs with a 90 second timeout.</summary>
    public const string HttpClientName = "ai-provider";

    private const string Endpoint = "https://api.groq.com/openai/v1/chat/completions";

    public async Task<GeneratedTestCaseSet> GenerateAsync(GenerateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new TestCaseGeneratorException(
                "No AI key is configured (set Groq__ApiKey). Use Import / manual to paste cases in.");
        if (string.IsNullOrWhiteSpace(settings.VisionModel))
            throw new TestCaseGeneratorException("No vision model is configured (set Groq__VisionModel).");
        if (req.Images.Count > MaxImages())
            throw new TestCaseGeneratorException(
                $"This suite has {req.Images.Count} screenshots; this provider accepts at most {MaxImages()} at a time. " +
                "Remove some, or use Import / manual.");

        var payloadObj = new Dictionary<string, object?>
        {
            ["model"] = settings.VisionModel.Trim(),
            ["response_format"] = new { type = "json_object" },
            ["temperature"] = 0.2,
            ["max_completion_tokens"] = 4096,
            ["messages"] = new object[]
            {
                new { role = "system", content = TestCasePrompt.System(req.Options) },
                new { role = "user", content = UserParts(req) }
            }
        };

        // Groq docs: For reasoning models (e.g. qwen/qwen3.8-27b), raw format with json_object returns HTTP 400.
        // It must be "parsed" or "hidden".
        if (IsReasoningModel(settings.VisionModel))
        {
            payloadObj["reasoning_format"] = "parsed";
        }

        string payload = JsonSerializer.Serialize(payloadObj);

        string body = await ProviderHttp.PostJsonAsync(
            httpFactory.CreateClient(HttpClientName),
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

        return TestCaseParser.Parse(ReplyText(body), req.Options);
    }

    internal static bool IsReasoningModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        return model.Contains("qwen", StringComparison.OrdinalIgnoreCase)
            || model.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase)
            || model.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
            || model.Contains("r1", StringComparison.OrdinalIgnoreCase);
    }

    private int MaxImages() => Math.Max(1, settings.MaxImages);

    /// <summary>One text part describing the job, then one image_url part per screenshot.</summary>
    private static object[] UserParts(GenerateRequest req)
    {
        var parts = new List<object>
        {
            new { type = "text", text = TestCasePrompt.User(req.BusinessDescription, req.Images.Count) }
        };
        parts.AddRange(req.Images.Select(image => new
        {
            type = "image_url",
            image_url = new { url = DataUrl(image) }
        }));
        return parts.ToArray();
    }

    internal static string DataUrl(GenerateImage image) =>
        $"data:{image.ContentType};base64,{Convert.ToBase64String(image.Content)}";

    /// <summary>choices[0].message.content — the reply in JSON mode. Never logs what came back.</summary>
    private static string ReplyText(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            throw new TestCaseGeneratorException(
                "The AI returned no answer. Try again, or use Import / manual.", 502);

        var message = choices[0];
        if (!message.TryGetProperty("message", out var messageBody)
            || !messageBody.TryGetProperty("content", out var content))
            throw new TestCaseGeneratorException(
                "The AI returned an empty answer. Try again, or use Import / manual.", 502);

        string? text = content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : content.ValueKind == JsonValueKind.Array
                ? string.Join("\n", content.EnumerateArray()
                    .Where(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("text", out _))
                    .Select(p => p.GetProperty("text").GetString()))
                : null;

        if (string.IsNullOrWhiteSpace(text))
            throw new TestCaseGeneratorException(
                "The AI returned an empty answer. Try again, or use Import / manual.", 502);
        return text;
    }
}
